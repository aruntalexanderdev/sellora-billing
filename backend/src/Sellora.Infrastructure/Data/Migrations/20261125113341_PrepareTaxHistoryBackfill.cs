using System;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PrepareTaxHistoryBackfill : Migration
    {
        private static readonly string[] LineTables = ["PurchaseReturnLines", "PurchaseOrderLines", "SalesInvoiceLines", "SalesReturnLines"];
        private const string VersionNeedsProfile = "\"TaxProfileVersionId\" IS NULL OR \"TaxProfileId\" IS NOT NULL";
        private const string ProfilePair = "(\"TaxProfileId\" IS NULL) = (\"TaxProfileVersionId\" IS NULL)";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TP-5a (docs/features/tax-profiles.md, "History backfill (M3a, additive)"): purely additive, one transaction, no table rewrite and no row scan under a lock, so the TP-4 build
            // keeps working during the app swap and can be redeployed without a database rollback. Rollback is application-only; Down() is dev-only.
            // Order inside the transaction (T31d, shortest ACCESS EXCLUSIVE window on the hot tables): (1) row-level work only (permission rows, grants); (2) the two small master tables
            // Units and BusinessSettings (ADD COLUMN, CHECK, the UQC seed UPDATE); (3) LAST, ONE ACCESS EXCLUSIVE LOCK TABLE on the four line tables, in the order the services take their
            // row locks (purchase returns, purchases, sales, sales returns), then catalog-only ALTERs. No foreign key is added, so there is no referenced table to lock first.
            // The new constraints are NOT VALID (no scan under the lock); ValidateTaxHistoryBackfill validates them with SHARE UPDATE EXCLUSIVE (writes continue). The new rule is weaker
            // than the old pair (every row that satisfied the pair satisfies it), so validation cannot fail.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            // 1. Permission Reports.Tax (precedent: AddTaxProfiles step 6): granted to the current holders of Reports.Sales. Id as DbSeeder: md5('perm:' || code).
            migrationBuilder.Sql("""
                INSERT INTO "Permissions" ("Id", "Code", "Module", "Action", "Description")
                VALUES (md5('perm:Reports.Tax')::uuid, 'Reports.Tax', 'Reports', 'View', 'View the tax reports: GST or VAT summary and the HSN summary.')
                ON CONFLICT ("Code") DO NOTHING;

                INSERT INTO "RolePermissions" ("RoleId", "PermissionId", "GrantedAt")
                SELECT DISTINCT rp."RoleId", np."Id", now()
                FROM "RolePermissions" rp
                JOIN "Permissions" ep ON ep."Id" = rp."PermissionId" AND ep."Code" = 'Reports.Sales'
                JOIN "Permissions" np ON np."Code" = 'Reports.Tax'
                ON CONFLICT DO NOTHING;
                """);

            // 2. Units.Uqc (GST Unit Quantity Code, three capital letters) and the mapping for the standard units. Only rows without a code are touched (re-running is a no-op, a code an
            // operator set is never overwritten). The same mapping is in DbSeeder.UnitSeed for new databases. Units without a mapping stay NULL: the HSN summary falls back to the short code.
            migrationBuilder.AddColumn<string>(name: "Uqc", table: "Units", type: "character varying(3)", maxLength: 3, nullable: true);
            migrationBuilder.AddCheckConstraint(name: "CK_Units_Uqc_Format", table: "Units", sql: "\"Uqc\" IS NULL OR \"Uqc\" ~ '^[A-Z]{3}$'");
            migrationBuilder.Sql("""
                UPDATE "Units" u SET "Uqc" = m.uqc
                FROM (VALUES
                  ('pc', 'PCS'), ('pcs', 'PCS'), ('piece', 'PCS'), ('nos', 'NOS'),
                  ('kg', 'KGS'), ('kgs', 'KGS'), ('g', 'GMS'), ('gm', 'GMS'), ('gms', 'GMS'),
                  ('litre', 'LTR'), ('liter', 'LTR'), ('ltr', 'LTR'), ('l', 'LTR'), ('ml', 'MLT'),
                  ('m', 'MTR'), ('mtr', 'MTR'),
                  ('box', 'BOX'), ('bag', 'BAG'), ('hctn', 'CTN'), ('fctn', 'CTN'), ('ctn', 'CTN'),
                  ('doz', 'DOZ'), ('dozen', 'DOZ'), ('pkt', 'PAC'), ('pack', 'PAC')
                ) AS m(code, uqc)
                WHERE lower(u."ShortCode") = m.code AND u."Uqc" IS NULL;
                """);

            // 3. BusinessSettings.HsnRequiredFrom: NULL for every existing company (the HSN rule stays a warning until the owner sets a date).
            migrationBuilder.AddColumn<DateOnly>(name: "HsnRequiredFrom", table: "BusinessSettings", type: "date", nullable: true);

            // 4. THE exclusive window: relax the pair constraint on the four line tables. Catalog-only (NOT VALID adds no scan).
            migrationBuilder.Sql($"LOCK TABLE {string.Join(", ", LineTables.Select(t => $"\"{t}\""))} IN ACCESS EXCLUSIVE MODE;");
            foreach (var table in LineTables)
            {
                migrationBuilder.Sql($"ALTER TABLE \"{table}\" ADD CONSTRAINT \"CK_{table}_TaxProfile_VersionNeedsProfile\" CHECK ({VersionNeedsProfile}) NOT VALID, DROP CONSTRAINT \"CK_{table}_TaxProfile_Pair\";");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dev-only (design: rollback is application-only). Restores the TP-3/TP-4 "both or neither" rule: profiles backfilled onto lines WITHOUT a version are cleared first (that is
            // the whole backfill; tax-history-profile-backfill re-creates it), the pair constraint comes back validated. Uqc values, HsnRequiredFrom and the Reports.Tax permission
            // (with its grants) are dropped. Re-running Up afterwards works. Heavy on a production-sized database: never used there.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.Sql($"LOCK TABLE {string.Join(", ", LineTables.Select(t => $"\"{t}\""))} IN ACCESS EXCLUSIVE MODE;");
            foreach (var table in LineTables)
            {
                migrationBuilder.Sql($"UPDATE \"{table}\" SET \"TaxProfileId\" = NULL WHERE \"TaxProfileVersionId\" IS NULL AND \"TaxProfileId\" IS NOT NULL;");
                migrationBuilder.Sql($"ALTER TABLE \"{table}\" ADD CONSTRAINT \"CK_{table}_TaxProfile_Pair\" CHECK ({ProfilePair}), DROP CONSTRAINT IF EXISTS \"CK_{table}_TaxProfile_VersionNeedsProfile\";");
            }

            migrationBuilder.DropCheckConstraint(name: "CK_Units_Uqc_Format", table: "Units");
            migrationBuilder.DropColumn(name: "Uqc", table: "Units");
            migrationBuilder.DropColumn(name: "HsnRequiredFrom", table: "BusinessSettings");

            migrationBuilder.Sql("""
                DELETE FROM "RolePermissions" WHERE "PermissionId" IN (SELECT "Id" FROM "Permissions" WHERE "Code" = 'Reports.Tax');
                DELETE FROM "Permissions" WHERE "Code" = 'Reports.Tax';
                """);
        }
    }
}
