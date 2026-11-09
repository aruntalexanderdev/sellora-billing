using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Multi-company Phase 2 / B3 - DocumentSequences per branch (D4, Q2): one migration, one transaction.
     *   guard G4 : a branch-level row already exists for (company's default branch, PUR/PRT/INV/SRT/ADJ) -> RAISE (the move
     *              below would collide on IX_DocumentSequences_CompanyId_BranchId_Series).
     *   enforce  : FK_DocumentSequences_Branches_CompanyId_BranchId (CompanyId, BranchId) -> Branches (CompanyId, Id), MATCH
     *              SIMPLE: a NULL BranchId (company-level series) is not checked.
     *   backfill : the five document counters move from company level to the company's default branch (BranchId = Main),
     *              NextValue untouched, so numbering continues exactly. EXP stays company-level (D4).
     *   assert   : a checksum of (CompanyId, Series, NextValue) is identical before/after; no NULL-branch row remains for the five
     *              series (V5).
     * Application code from this release allocates numbers with Company + Branch predicates; an older build would keep
     * writing the (now empty) company-level slot - deploy together (stop API, apply, deploy).
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: read-only, on a prod copy BEFORE the release.
     *
     * -- P1 counters (expect BranchId NULL everywhere; keep the output to compare with V5)
     * SELECT "CompanyId", "Series", "BranchId", "NextValue" FROM "DocumentSequences" ORDER BY 1, 2;
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: AFTER applying; V5 must return no rows and the NextValue per series must equal P1.
     *
     * SELECT * FROM "DocumentSequences" WHERE "BranchId" IS NULL AND "Series" <> 'EXP';
     * SELECT "CompanyId", "Series", "BranchId", "NextValue" FROM "DocumentSequences" ORDER BY 1, 2;
     * -- V7 post-deploy smoke: the next PUR/INV/SRT/PRT/ADJ/EXP number is previous max + 1 in the unchanged format
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down (guard D-G2 first: raises when a company has more than one branch, a counter sits on a non-default branch, or a
     * company-level row already exists for one of the five series): moves the counters back to company level, drops the FK.
     */
    /// <inheritdoc />
    public partial class ScopeDocumentSequencesToBranches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guard G4: a branch-level row for the default branch would collide with the move.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "DocumentSequences" s JOIN "Branches" b ON b."Id" = s."BranchId" AND b."IsDefault"
                               WHERE s."Series" IN ('PUR', 'PRT', 'INV', 'SRT', 'ADJ')) THEN
                        RAISE EXCEPTION 'ScopeDocumentSequencesToBranches: a branch-level counter already exists for a default branch (G4)';
                    END IF;
                END $g$;
                """);

            // ===== enforce (additive FK) =====
            migrationBuilder.AddForeignKey(
                name: "FK_DocumentSequences_Branches_CompanyId_BranchId",
                table: "DocumentSequences",
                columns: new[] { "CompanyId", "BranchId" },
                principalTable: "Branches",
                principalColumns: new[] { "CompanyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // ===== backfill + assertions: move the five counters to the default branch =====
            migrationBuilder.Sql("""
                DO $s$
                DECLARE
                    before_sum text;
                    after_sum text;
                    leftover bigint;
                BEGIN
                    SELECT md5(COALESCE(string_agg("CompanyId"::text || ':' || "Series" || ':' || "NextValue"::text, ',' ORDER BY "CompanyId", "Series"), ''))
                    INTO before_sum FROM "DocumentSequences";

                    UPDATE "DocumentSequences" s SET "BranchId" = b."Id", "UpdatedAt" = now()
                    FROM "Branches" b
                    WHERE b."CompanyId" = s."CompanyId" AND b."IsDefault" AND s."BranchId" IS NULL
                      AND s."Series" IN ('PUR', 'PRT', 'INV', 'SRT', 'ADJ');

                    SELECT md5(COALESCE(string_agg("CompanyId"::text || ':' || "Series" || ':' || "NextValue"::text, ',' ORDER BY "CompanyId", "Series"), ''))
                    INTO after_sum FROM "DocumentSequences";
                    IF before_sum <> after_sum THEN
                        RAISE EXCEPTION 'ScopeDocumentSequencesToBranches: NextValue changed during the move';
                    END IF;

                    SELECT COUNT(*) INTO leftover FROM "DocumentSequences" WHERE "BranchId" IS NULL AND "Series" IN ('PUR', 'PRT', 'INV', 'SRT', 'ADJ');
                    IF leftover > 0 THEN
                        RAISE EXCEPTION 'ScopeDocumentSequencesToBranches: % counter(s) still at company level (V5)', leftover;
                    END IF;
                END $s$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guard D-G2.
            migrationBuilder.Sql("""
                DO $g$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Branches" GROUP BY "CompanyId" HAVING COUNT(*) > 1)
                       OR EXISTS (SELECT 1 FROM "DocumentSequences" s JOIN "Branches" b ON b."Id" = s."BranchId" WHERE NOT b."IsDefault")
                       OR EXISTS (SELECT 1 FROM "DocumentSequences" c JOIN "DocumentSequences" d
                                  ON d."CompanyId" = c."CompanyId" AND d."Series" = c."Series" AND d."BranchId" IS NOT NULL
                                  WHERE c."BranchId" IS NULL AND c."Series" IN ('PUR', 'PRT', 'INV', 'SRT', 'ADJ')) THEN
                        RAISE EXCEPTION 'ScopeDocumentSequencesToBranches Down: counters exist beyond the default branch; restore the pre-deploy snapshot instead.';
                    END IF;
                END $g$;

                UPDATE "DocumentSequences" SET "BranchId" = NULL, "UpdatedAt" = now()
                WHERE "BranchId" IS NOT NULL AND "Series" IN ('PUR', 'PRT', 'INV', 'SRT', 'ADJ');
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_DocumentSequences_Branches_CompanyId_BranchId",
                table: "DocumentSequences");
        }
    }
}
