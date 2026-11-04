using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Phase 1 / M1 - Unit Master extension.
     * Type -> MeasurementCategory (rename, data kept), + DecimalPlaces, + StandardFactor, backfill, CHECKs.
     *
     * RELEASE NOTE: renaming Units.Type breaks the previous app build (it still reads/writes "Type").
     * Apply the idempotent script and deploy the new app back-to-back inside a maintenance window with
     * writes stopped; run V2 again once the new app is up.
     *
     * Validation SQL (release engineer runs after applying; V1-V3 must return no rows).
     *
     * -- V1 unmapped category (any value outside the new enum)
     * SELECT "Id", "Name", "ShortCode", "MeasurementCategory" FROM "Units"
     * WHERE "MeasurementCategory" NOT IN ('Count','Weight','Volume','Length','Packaging');
     *
     * -- V2 Packaging unit used as a product base unit
     * SELECT u."Id", u."ShortCode", p."Id" AS "ProductId", p."Sku" FROM "Units" u
     * JOIN "Products" p ON p."BaseUnitId" = u."Id"
     * WHERE u."MeasurementCategory" = 'Packaging';
     *
     * -- V3 Count/Packaging with non-zero decimals (also enforced by CK_Units_DecimalPlaces_Category)
     * SELECT "Id", "ShortCode", "MeasurementCategory", "DecimalPlaces" FROM "Units"
     * WHERE "MeasurementCategory" IN ('Count','Packaging') AND "DecimalPlaces" <> 0;
     *
     * -- V4 review list (not errors; needs a human decision in the Units screen):
     * --    container-like units kept as Count because a product uses them as base unit,
     * --    sub-units (g, gm, mg, ml) kept at 3 dp because a product uses them as base unit,
     * --    and measurable units without a StandardFactor.
     * SELECT u."Id", u."Name", u."ShortCode", u."MeasurementCategory", u."DecimalPlaces", u."StandardFactor",
     *        CASE WHEN u."MeasurementCategory" = 'Count' THEN 'container kept as Count (used as base unit)'
     *             WHEN u."StandardFactor" IS NULL THEN 'measurable unit without StandardFactor'
     *             ELSE 'sub-unit kept at 3 dp because it is a base unit' END AS "Reason"
     * FROM "Units" u
     * WHERE (u."MeasurementCategory" = 'Count'
     *        AND (lower(u."ShortCode") IN ('box','hctn','fctn','ctn','carton','bag','pack','pkt','case','crate','bottle','jar','tin','can')
     *             OR lower(u."Name") ~ '(box|carton|bag|pack|case|crate|bottle|jar|tin|can)'))
     *    OR (u."MeasurementCategory" IN ('Weight','Volume','Length') AND u."StandardFactor" IS NULL)
     *    OR (lower(u."ShortCode") IN ('g','gm','mg','ml') AND u."DecimalPlaces" <> 0
     *        AND EXISTS (SELECT 1 FROM "Products" p WHERE p."BaseUnitId" = u."Id"))
     * ORDER BY "Reason", u."ShortCode";
     *
     * -- V5 row count: run before and after; the two numbers must be equal
     * SELECT COUNT(*) AS "UnitsRowCount" FROM "Units";
     *
     * -- V6 case-insensitive duplicates (run BEFORE the script; must return no rows, otherwise the
     * --    migration aborts when creating UX_Units_Name_Lower / UX_Units_ShortCode_Lower; resolve manually)
     * SELECT 'Name' AS "Column", lower("Name") AS "Value", COUNT(*) FROM "Units" GROUP BY lower("Name") HAVING COUNT(*) > 1
     * UNION ALL
     * SELECT 'ShortCode', lower("ShortCode"), COUNT(*) FROM "Units" GROUP BY lower("ShortCode") HAVING COUNT(*) > 1;
     *
     * Down() is lossy: 'Packaging' and 'Length' go back to 'Other'; rows that were 'Other' and were
     * mapped to 'Count' stay 'Count'. Rely on the pre-deploy snapshot for an exact restore.
     */
    /// <inheritdoc />
    public partial class ExtendUnitMaster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Type",
                table: "Units",
                newName: "MeasurementCategory");

            migrationBuilder.AddColumn<short>(
                name: "DecimalPlaces",
                table: "Units",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<decimal>(
                name: "StandardFactor",
                table: "Units",
                type: "numeric(28,10)",
                nullable: true);

            // Backfill (before enforcing the CHECKs).
            // 1. Legacy 'Other': base unit of some product -> Count, otherwise a container -> Packaging.
            migrationBuilder.Sql("""
                UPDATE "Units" u SET "MeasurementCategory" = CASE WHEN EXISTS (SELECT 1 FROM "Products" p WHERE p."BaseUnitId" = u."Id") THEN 'Count' ELSE 'Packaging' END WHERE u."MeasurementCategory" = 'Other';
                """);

            // 2. Known container codes seeded as Count -> Packaging, unless used as a base unit (left for review, V4).
            migrationBuilder.Sql("""
                UPDATE "Units" u SET "MeasurementCategory" = 'Packaging' WHERE u."MeasurementCategory" = 'Count' AND lower(u."ShortCode") IN ('box','hctn','fctn') AND NOT EXISTS (SELECT 1 FROM "Products" p WHERE p."BaseUnitId" = u."Id");
                """);

            // 3. Decimals: measurable units 3, discrete units 0.
            migrationBuilder.Sql("""
                UPDATE "Units" SET "DecimalPlaces" = CASE WHEN "MeasurementCategory" IN ('Weight','Volume','Length') THEN 3 ELSE 0 END;
                """);

            // 3b. Sub-units (factor < 1) are entered whole: g, gm, mg, ml -> 0 dp, unless a product uses the
            //     unit as its base (kept at 3; listed in V4 for review).
            migrationBuilder.Sql("""
                UPDATE "Units" u SET "DecimalPlaces" = 0 WHERE lower(u."ShortCode") IN ('g','gm','mg','ml') AND NOT EXISTS (SELECT 1 FROM "Products" p WHERE p."BaseUnitId" = u."Id");
                """);

            // 4. Standard factors for well-known weight / volume codes (relative to kg / L).
            migrationBuilder.Sql("""
                UPDATE "Units" SET "StandardFactor" = CASE lower("ShortCode") WHEN 'kg' THEN 1 WHEN 'g' THEN 0.001 WHEN 'gm' THEN 0.001 WHEN 'mg' THEN 0.000001 END WHERE "MeasurementCategory" = 'Weight' AND lower("ShortCode") IN ('kg','g','gm','mg');
                """);

            migrationBuilder.Sql("""
                UPDATE "Units" SET "StandardFactor" = CASE lower("ShortCode") WHEN 'litre' THEN 1 WHEN 'l' THEN 1 WHEN 'ltr' THEN 1 WHEN 'ml' THEN 0.001 END WHERE "MeasurementCategory" = 'Volume' AND lower("ShortCode") IN ('litre','l','ltr','ml');
                """);

            // Enforce.
            // Case-insensitive uniqueness: fail loudly (no data change) if legacy rows collide; see V6.
            migrationBuilder.Sql("""
                DO $ux$
                DECLARE
                    dup_names text;
                    dup_codes text;
                BEGIN
                    SELECT string_agg(v, ', ') INTO dup_names FROM (SELECT lower("Name") AS v FROM "Units" GROUP BY 1 HAVING COUNT(*) > 1) d;
                    SELECT string_agg(v, ', ') INTO dup_codes FROM (SELECT lower("ShortCode") AS v FROM "Units" GROUP BY 1 HAVING COUNT(*) > 1) d;
                    IF dup_names IS NOT NULL OR dup_codes IS NOT NULL THEN
                        RAISE EXCEPTION 'ExtendUnitMaster: case-insensitive duplicate units must be resolved before migrating. Names: [%]. ShortCodes: [%].',
                            COALESCE(dup_names, ''), COALESCE(dup_codes, '');
                    END IF;
                END
                $ux$;
                """);

            // Expression indexes (not modelled by EF; IX_Units_Name / IX_Units_ShortCode stay as mapped).
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "UX_Units_Name_Lower" ON "Units" (lower("Name"));""");
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "UX_Units_ShortCode_Lower" ON "Units" (lower("ShortCode"));""");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Units_MeasurementCategory_Domain",
                table: "Units",
                sql: "\"MeasurementCategory\" IN ('Count','Weight','Volume','Length','Packaging')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Units_DecimalPlaces_Range",
                table: "Units",
                sql: "\"DecimalPlaces\" BETWEEN 0 AND 3");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Units_DecimalPlaces_Category",
                table: "Units",
                sql: "\"MeasurementCategory\" NOT IN ('Count','Packaging') OR \"DecimalPlaces\" = 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Units_StandardFactor_Category",
                table: "Units",
                sql: "\"StandardFactor\" IS NULL OR (\"MeasurementCategory\" IN ('Weight','Volume','Length') AND \"StandardFactor\" > 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "UX_Units_ShortCode_Lower";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "UX_Units_Name_Lower";""");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Units_MeasurementCategory_Domain",
                table: "Units");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Units_DecimalPlaces_Category",
                table: "Units");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Units_DecimalPlaces_Range",
                table: "Units");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Units_StandardFactor_Category",
                table: "Units");

            // Lossy: categories that did not exist before collapse back to 'Other'.
            migrationBuilder.Sql("""
                UPDATE "Units" SET "MeasurementCategory" = 'Other' WHERE "MeasurementCategory" IN ('Packaging','Length');
                """);

            migrationBuilder.DropColumn(
                name: "DecimalPlaces",
                table: "Units");

            migrationBuilder.DropColumn(
                name: "StandardFactor",
                table: "Units");

            migrationBuilder.RenameColumn(
                name: "MeasurementCategory",
                table: "Units",
                newName: "Type");
        }
    }
}
