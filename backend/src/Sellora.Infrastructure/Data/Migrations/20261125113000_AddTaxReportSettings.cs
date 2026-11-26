using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaxReportSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TP-5a, first of three migrations (AddTaxReportSettings, PrepareTaxHistoryBackfill, ValidateTaxHistoryBackfill; T35). Purely additive and independent of the line tables:
            // the ACCESS EXCLUSIVE locks taken here (Units, BusinessSettings) are released at the end of THIS transaction, never held while a later migration waits for the hot line
            // tables (a sale holding line locks that reads Units would otherwise deadlock with it). Order: row-level work first, then the two small DDL targets.
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dev-only (design: rollback is application-only). Uqc values, HsnRequiredFrom and the Reports.Tax permission (with its grants) are dropped. Re-running Up afterwards works.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.Sql("""
                DELETE FROM "RolePermissions" WHERE "PermissionId" IN (SELECT "Id" FROM "Permissions" WHERE "Code" = 'Reports.Tax');
                DELETE FROM "Permissions" WHERE "Code" = 'Reports.Tax';
                """);

            migrationBuilder.DropCheckConstraint(name: "CK_Units_Uqc_Format", table: "Units");
            migrationBuilder.DropColumn(name: "Uqc", table: "Units");
            migrationBuilder.DropColumn(name: "HsnRequiredFrom", table: "BusinessSettings");
        }
    }
}
