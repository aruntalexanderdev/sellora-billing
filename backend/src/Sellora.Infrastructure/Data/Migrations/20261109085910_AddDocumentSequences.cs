using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Multi-company Phase 1 / M4 - DocumentSequences (ADR-08): gap-free per-company document counters replacing COUNT(*)+1 (one
     * migration, one transaction).
     *   additive : DocumentSequences (Id PK, CompanyId FK -> Companies, BranchId uuid NULL - the FK to Branches is added in
     *              Phase 2, Series varchar(10), NextValue bigint, UpdatedAt; CK NextValue >= 1, CK Series not empty). Two partial
     *              uniques instead of one over nullable BranchId: IX_DocumentSequences_CompanyId_Series WHERE "BranchId" IS NULL
     *              and IX_DocumentSequences_CompanyId_BranchId_Series WHERE "BranchId" IS NOT NULL.
     *   backfill : one company-level row per company x series: PUR (PurchaseOrders.PurchaseNo), PRT (PurchaseReturns.ReturnNo),
     *              INV (SalesInvoices.InvoiceNo, whatever the prefix), SRT (SalesReturns.ReturnNo), ADJ (StockAdjustments.
     *              AdjustmentNo), EXP (Expenses.ExpenseNo). NextValue = GREATEST(COUNT(*), MAX(numeric suffix)) + 1, so the next
     *              number is above every existing suffix even with gaps or an edited prefix; non-numeric numbers fall back to the
     *              count (P2). Deterministic Id = md5('docseq:' || CompanyId || ':' || series)::uuid (no gen_random_uuid
     *              dependency); ON CONFLICT DO NOTHING makes it idempotent.
     *   assert   : every seeded NextValue is greater than every existing numeric suffix of its series (V5).
     * Services allocate with UPDATE ... SET "NextValue" = "NextValue" + 1 ... RETURNING (row lock until commit: rollback leaves no
     * gap). Format stays {prefix}-{n:D4}.
     *
     * RELEASE NOTE: small table; seeding scans six document tables once. Startup auto-migrate must stay off in production (D10).
     * After deploy (V7) new documents are numbered max+1 per series.
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release.
     *
     * -- P2 document numbers whose suffix is not numeric (informational: seeding falls back to the count), one per series
     * SELECT "PurchaseNo" FROM "PurchaseOrders" WHERE "PurchaseNo" !~ '[0-9]$';
     * SELECT "InvoiceNo" FROM "SalesInvoices" WHERE "InvoiceNo" !~ '[0-9]$';
     * -- (same for PurchaseReturns.ReturnNo, SalesReturns.ReturnNo, StockAdjustments.AdjustmentNo, Expenses.ExpenseNo)
     * -- P2b current maxima, to compare with the seeded NextValue
     * SELECT MAX(substring("InvoiceNo" FROM '([0-9]{1,18})$')::bigint), COUNT(*) FROM "SalesInvoices";
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying; V5 must return no rows.
     *
     * -- V0 one row per company x series
     * SELECT "CompanyId", "Series", "NextValue" FROM "DocumentSequences" ORDER BY 1, 2;
     * -- V5 sequence not ahead of history (per series; example for invoices)
     * SELECT i."Id" FROM "SalesInvoices" i JOIN "DocumentSequences" s ON s."CompanyId" = i."CompanyId" AND s."BranchId" IS NULL AND s."Series" = 'INV'
     * WHERE substring(i."InvoiceNo" FROM '([0-9]{1,18})$')::bigint >= s."NextValue";
     * -- V7 post-deploy: the next created document of each series has suffix = NextValue before creation
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down (guard D-G first: raises when more than one company exists or a sequence belongs to another company): drops
     * DocumentSequences. The counter state is lost; the pre-Phase-1 code numbers by COUNT(*)+1, which collides after gaps -
     * roll the application back together with this Down only on a database without gaps, otherwise restore the snapshot.
     */
    /// <inheritdoc />
    public partial class AddDocumentSequences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ===== 1. additive =====
            migrationBuilder.CreateTable(
                name: "DocumentSequences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Series = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    NextValue = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentSequences", x => x.Id);
                    table.CheckConstraint("CK_DocumentSequences_NextValue_Positive", "\"NextValue\" >= 1");
                    table.CheckConstraint("CK_DocumentSequences_Series_NotEmpty", "char_length(\"Series\") > 0");
                    table.ForeignKey(
                        name: "FK_DocumentSequences_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentSequences_CompanyId_BranchId_Series",
                table: "DocumentSequences",
                columns: new[] { "CompanyId", "BranchId", "Series" },
                unique: true,
                filter: "\"BranchId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentSequences_CompanyId_Series",
                table: "DocumentSequences",
                columns: new[] { "CompanyId", "Series" },
                unique: true,
                filter: "\"BranchId\" IS NULL");

            // ===== 2. backfill + assertions =====
            migrationBuilder.Sql("""
                DO $s$
                DECLARE
                    r record;
                    behind bigint;
                BEGIN
                    FOR r IN SELECT * FROM (VALUES
                        ('PUR', 'PurchaseOrders', 'PurchaseNo'),
                        ('PRT', 'PurchaseReturns', 'ReturnNo'),
                        ('INV', 'SalesInvoices', 'InvoiceNo'),
                        ('SRT', 'SalesReturns', 'ReturnNo'),
                        ('ADJ', 'StockAdjustments', 'AdjustmentNo'),
                        ('EXP', 'Expenses', 'ExpenseNo')
                    ) AS v(series, tbl, col) LOOP
                        EXECUTE format($f$
                            INSERT INTO "DocumentSequences" ("Id", "CompanyId", "BranchId", "Series", "NextValue", "UpdatedAt")
                            SELECT md5('docseq:' || c."Id"::text || ':' || %1$L)::uuid, c."Id", NULL, %1$L,
                                   GREATEST(COALESCE(d.cnt, 0), COALESCE(d.mx, 0)) + 1, now()
                            FROM "Companies" c
                            LEFT JOIN (SELECT "CompanyId", COUNT(*) AS cnt, MAX(substring(%3$I FROM '([0-9]{1,18})$')::bigint) AS mx
                                       FROM %2$I GROUP BY "CompanyId") d ON d."CompanyId" = c."Id"
                            ON CONFLICT DO NOTHING
                            $f$, r.series, r.tbl, r.col);

                        EXECUTE format($f$
                            SELECT COUNT(*) FROM %2$I d
                            JOIN "DocumentSequences" s ON s."CompanyId" = d."CompanyId" AND s."BranchId" IS NULL AND s."Series" = %1$L
                            WHERE substring(d.%3$I FROM '([0-9]{1,18})$')::bigint >= s."NextValue"
                            $f$, r.series, r.tbl, r.col) INTO behind;
                        IF behind > 0 THEN
                            RAISE EXCEPTION 'AddDocumentSequences: sequence % is not ahead of % existing number(s) (V5)', r.series, behind;
                        END IF;
                    END LOOP;

                    IF (SELECT COUNT(*) FROM "DocumentSequences" WHERE "BranchId" IS NULL) <> (SELECT COUNT(*) FROM "Companies") * 6 THEN
                        RAISE EXCEPTION 'AddDocumentSequences: expected six company-level series per company (V0)';
                    END IF;
                END $s$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guard D-G: a second tenant cannot be folded back into the single-tenant schema.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF (SELECT COUNT(*) FROM "Companies") > 1
                       OR EXISTS (SELECT 1 FROM "DocumentSequences" WHERE "CompanyId" <> '0c0a1f00-0000-4000-8000-000000000001') THEN
                        RAISE EXCEPTION 'AddDocumentSequences Down: more than one company exists; restore the pre-deploy snapshot instead.';
                    END IF;
                END $g$;
                """);

            migrationBuilder.DropTable(
                name: "DocumentSequences");
        }
    }
}
