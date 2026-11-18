using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStockItemsNonNegativeCheck : Migration
    {
        // FI-D25 (Phase 02 DB guard). Enforce step only: the guard REFUSES when negative rows exist and never modifies data.
        // Pre-deploy / fix list: scripts/sql/stock-items-negative-check.sql (must return no rows). Down drops the constraint (fully reversible).
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Phase 02 review: block writers between the guard count and ADD CONSTRAINT, so a row going negative in between still gets the
            // friendly guard message (the migration runs in one transaction; ADD CONSTRAINT then upgrades to ACCESS EXCLUSIVE).
            migrationBuilder.Sql("""LOCK TABLE "StockItems" IN SHARE ROW EXCLUSIVE MODE;""");

            migrationBuilder.Sql("""
                DO $g$
                DECLARE n bigint; sample text;
                BEGIN
                    SELECT COUNT(*) INTO n FROM "StockItems" WHERE "QuantityOnHand" < 0;
                    IF n > 0 THEN
                        SELECT string_agg(format('(company %s, location %s, product %s, qty %s)', x."CompanyId", x."StockLocationId", x."ProductId", x."QuantityOnHand"), '; ')
                        INTO sample FROM (
                            SELECT "CompanyId", "StockLocationId", "ProductId", "QuantityOnHand" FROM "StockItems"
                            WHERE "QuantityOnHand" < 0 ORDER BY "CompanyId", "StockLocationId", "ProductId" LIMIT 20) x;
                        RAISE EXCEPTION 'AddStockItemsNonNegativeCheck: % StockItems row(s) have a negative QuantityOnHand (first 20: %). Correct them through a reviewed stock adjustment, then re-run; see scripts/sql/stock-items-negative-check.sql.', n, sample;
                    END IF;
                END
                $g$;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockItems_QuantityOnHand_NonNegative",
                table: "StockItems",
                sql: "\"QuantityOnHand\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StockItems_QuantityOnHand_NonNegative",
                table: "StockItems");
        }
    }
}
