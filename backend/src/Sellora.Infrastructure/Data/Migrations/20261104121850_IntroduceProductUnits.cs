using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.Infrastructure.Data.Migrations
{
    /*
     * Phase 2 / M2-M5 - Product unit hierarchy (one migration, four ordered sections; runs in one transaction).
     *   M2 structure : ProductSellingModes -> ProductUnits (RENAME, Ids preserved), PK/FK/index renamed, new columns
     *                  (UnitId, InnerProductUnitId, InnerQuantity, IsBaseUnit, Barcode, IsPurchasable, IsSellable,
     *                  IsDefaultPurchaseUnit, IsDefaultSalesUnit, SortOrder), Label nullable,
     *                  ConversionFactorToBase numeric(18,3) -> numeric(28,10) (table rewrite; small table).
     *   M3 backfill  : Label -> UnitId by exact case-insensitive Name/ShortCode match only (never by parsing),
     *                  one base row per product (first factor-1 row, else a new base row), unmatched labels become
     *                  generated Packaging units 'PKG-nnn', flat hierarchy (InnerQuantity = old factor over the base),
     *                  SortOrder; in-transaction assertions raise and roll back on any inconsistency.
     *   M4 enforce   : UnitId NOT NULL, AK (ProductId, Id), FK Units, composite inner FK, Products FK Cascade ->
     *                  Restrict, partial unique indexes, CHECKs.
     *   M5 references: PurchaseOrderLines/SalesInvoiceLines.SellingModeId and SellingPrices.ProductSellingModeId ->
     *                  ProductUnitId (RENAME, FK/index renamed); SellingPrices FK Cascade -> Restrict.
     * Line factor snapshots stay numeric(18,3) (widening is M6).
     *
     * RELEASE NOTE: the table/column renames break the previous app build (it still reads "ProductSellingModes" /
     * "SellingModeId"), and the API contracts change in the same release. Maintenance window, as in Phase 1:
     *   1. take a snapshot / Neon branch of the database; 2. run the P-block on it and record P11;
     *   3. apply the idempotent script; 4. run the V-block (V1-V6 must return no rows, V7 totals must match);
     *   5. deploy the API and the SWA back-to-back; 6. smoke test (product edit, pricing grid, POS catalog,
     *   purchase and sale detail pages).
     * ReportsController selling value may change for products whose fractional-factor row was priced (WP2b).
     * Rehearse on a Neon branch of prod first. Startup auto-migrate must stay off in production (D10).
     *
     * ---------------------------------------------------------------------------------------------------------
     * P-block: profiling, read-only, run on a prod copy BEFORE the release (uses the OLD names).
     *
     * -- P1 modes per product
     * SELECT n, COUNT(*) FROM (SELECT p."Id", COUNT(m."Id") n FROM "Products" p
     *   LEFT JOIN "ProductSellingModes" m ON m."ProductId" = p."Id" GROUP BY 1) x GROUP BY n ORDER BY n;
     * -- P2 products without a factor-1 mode (a new base row, not sellable/purchasable, will be inserted)
     * SELECT p."Sku", string_agg(m."Label" || '=' || m."ConversionFactorToBase", ', ') FROM "Products" p
     *   LEFT JOIN "ProductSellingModes" m ON m."ProductId" = p."Id" GROUP BY p."Id", p."Sku"
     *   HAVING COUNT(*) FILTER (WHERE m."ConversionFactorToBase" = 1) = 0;
     * -- P3 products with more than one factor-1 mode (the extras become generated Packaging rows, InnerQuantity 1)
     * SELECT p."Sku", string_agg(m."Label" || '=' || m."ConversionFactorToBase", ', ') FROM "Products" p
     *   LEFT JOIN "ProductSellingModes" m ON m."ProductId" = p."Id" GROUP BY p."Id", p."Sku"
     *   HAVING COUNT(*) FILTER (WHERE m."ConversionFactorToBase" = 1) > 1;
     * -- P4 distinct labels and exact-match preview
     * SELECT lower(btrim(m."Label")) k, COUNT(*), MIN(m."ConversionFactorToBase"), MAX(m."ConversionFactorToBase"),
     *        string_agg(DISTINCT u."ShortCode" || '/' || u."MeasurementCategory", ',')
     * FROM "ProductSellingModes" m LEFT JOIN "Units" u
     *   ON lower(u."Name") = lower(btrim(m."Label")) OR lower(u."ShortCode") = lower(btrim(m."Label"))
     * GROUP BY 1 ORDER BY 2 DESC;
     * -- P5 factor <= 0 (blocks the migration), factor < 1, non-integer factor on a 0-dp base (review)
     * SELECT p."Sku", m."Label", m."ConversionFactorToBase", bu."ShortCode", bu."DecimalPlaces"
     * FROM "ProductSellingModes" m JOIN "Products" p ON p."Id" = m."ProductId" JOIN "Units" bu ON bu."Id" = p."BaseUnitId"
     * WHERE m."ConversionFactorToBase" <= 0 OR m."ConversionFactorToBase" < 1
     *    OR (m."ConversionFactorToBase" * power(10, bu."DecimalPlaces")) <> trunc(m."ConversionFactorToBase" * power(10, bu."DecimalPlaces"));
     * -- P6 usage per mode
     * SELECT m."Id", m."Label",
     *   (SELECT COUNT(*) FROM "PurchaseOrderLines" l WHERE l."SellingModeId" = m."Id") po,
     *   (SELECT COUNT(*) FROM "SalesInvoiceLines" l WHERE l."SellingModeId" = m."Id") si,
     *   (SELECT COUNT(*) FROM "SellingPrices" s WHERE s."ProductSellingModeId" = m."Id") prices
     * FROM "ProductSellingModes" m;
     * -- P7 line product differs from mode product (pre-existing corruption; must be empty, fix before release)
     * SELECT 'PO', l."Id" FROM "PurchaseOrderLines" l JOIN "ProductSellingModes" m ON m."Id" = l."SellingModeId" WHERE m."ProductId" <> l."ProductId"
     * UNION ALL
     * SELECT 'SI', l."Id" FROM "SalesInvoiceLines" l JOIN "ProductSellingModes" m ON m."Id" = l."SellingModeId" WHERE m."ProductId" <> l."ProductId";
     * -- P8 Packaging unit used as base unit (Phase 1 V2 re-check; must be empty)
     * SELECT u."Id", u."ShortCode", p."Id" AS "ProductId", p."Sku" FROM "Units" u
     * JOIN "Products" p ON p."BaseUnitId" = u."Id" WHERE u."MeasurementCategory" = 'Packaging';
     * -- P9 same label twice within a product
     * SELECT "ProductId", lower(btrim("Label")), COUNT(*) FROM "ProductSellingModes" GROUP BY 1, 2 HAVING COUNT(*) > 1;
     * -- P10 more than one open price per mode (info; existing bug, not fixed here)
     * SELECT "ProductSellingModeId", COUNT(*) FROM "SellingPrices" WHERE "EffectiveTo" IS NULL GROUP BY 1 HAVING COUNT(*) > 1;
     * -- P11 counts to record (compare in V7)
     * SELECT (SELECT COUNT(*) FROM "ProductSellingModes") modes, (SELECT COUNT(*) FROM "SellingPrices") prices,
     *        (SELECT COUNT(*) FROM "PurchaseOrderLines") po_lines, (SELECT COUNT(*) FROM "SalesInvoiceLines") si_lines,
     *        (SELECT COUNT(*) FROM "Units") units;
     *
     * ---------------------------------------------------------------------------------------------------------
     * V-block: run AFTER applying. V1-V6 must return no rows; V7 compares totals; V8 is a review list.
     *
     * -- V1 not exactly one base row
     * SELECT p."Id" FROM "Products" p WHERE (SELECT COUNT(*) FROM "ProductUnits" x WHERE x."ProductId" = p."Id" AND x."IsBaseUnit") <> 1;
     * -- V2 base row unit differs from the product base unit
     * SELECT x."Id" FROM "ProductUnits" x JOIN "Products" p ON p."Id" = x."ProductId" WHERE x."IsBaseUnit" AND x."UnitId" <> p."BaseUnitId";
     * -- V3 factor parity along the chain
     * SELECT x."Id" FROM "ProductUnits" x JOIN "ProductUnits" i ON i."Id" = x."InnerProductUnitId"
     * WHERE i."ProductId" <> x."ProductId" OR x."ConversionFactorToBase" <> x."InnerQuantity" * i."ConversionFactorToBase";
     * -- V4 duplicate active unit per product
     * SELECT "ProductId", "UnitId" FROM "ProductUnits" WHERE "IsActive" GROUP BY 1, 2 HAVING COUNT(*) > 1;
     * -- V5 line/price references resolve and belong to the same product
     * SELECT 'PO', l."Id" FROM "PurchaseOrderLines" l LEFT JOIN "ProductUnits" x ON x."Id" = l."ProductUnitId" WHERE x."Id" IS NULL OR x."ProductId" <> l."ProductId"
     * UNION ALL SELECT 'SI', l."Id" FROM "SalesInvoiceLines" l LEFT JOIN "ProductUnits" x ON x."Id" = l."ProductUnitId" WHERE x."Id" IS NULL OR x."ProductId" <> l."ProductId"
     * UNION ALL SELECT 'SP', s."Id" FROM "SellingPrices" s LEFT JOIN "ProductUnits" x ON x."Id" = s."ProductUnitId" WHERE x."Id" IS NULL;
     * -- V6 stock invariants and line arithmetic (unchanged by this migration)
     * SELECT s."ProductId" FROM "StockItems" s
     * LEFT JOIN (SELECT "ProductId", SUM("QuantityBaseUnits") q FROM "StockMovements" GROUP BY 1) m USING ("ProductId")
     * WHERE s."QuantityOnHand" <> COALESCE(m.q, 0);
     * SELECT s."ProductId" FROM "StockItems" s
     * LEFT JOIN (SELECT "ProductId", SUM("RemainingBaseQty") q FROM "PurchaseBatches" GROUP BY 1) b USING ("ProductId")
     * WHERE s."QuantityOnHand" <> COALESCE(b.q, 0);
     * SELECT "Id" FROM "SalesInvoiceLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty";
     * SELECT "Id" FROM "PurchaseOrderLines" WHERE "Qty" * "ConversionFactorToBase" <> "BaseQty";
     * -- V7 totals: units = P11.modes + P2 product count; prices / po_lines / si_lines = P11; Units = P11.units + generated
     * SELECT (SELECT COUNT(*) FROM "ProductUnits") units, (SELECT COUNT(*) FROM "SellingPrices") prices,
     *        (SELECT COUNT(*) FROM "PurchaseOrderLines") po_lines, (SELECT COUNT(*) FROM "SalesInvoiceLines") si_lines,
     *        (SELECT COUNT(*) FROM "Units") all_units, (SELECT COUNT(*) FROM "Units" WHERE "ShortCode" ~ '^PKG-') generated_units;
     * -- V8 review list (not errors)
     * SELECT p."Sku", x."Label", u."ShortCode", u."Name", x."InnerQuantity", x."ConversionFactorToBase",
     *   CASE WHEN u."ShortCode" ~ '^PKG-' THEN 'generated packaging unit'
     *        WHEN NOT x."IsBaseUnit" AND x."ConversionFactorToBase" = 1 THEN 'extra factor-1 row (duplicate of base)'
     *        WHEN x."IsBaseUnit" AND NOT x."IsSellable" AND NOT x."IsPurchasable" THEN 'base row added/reactivated, not sellable'
     *        ELSE 'non-integer factor on whole-number base' END AS reason
     * FROM "ProductUnits" x JOIN "Products" p ON p."Id" = x."ProductId" JOIN "Units" u ON u."Id" = x."UnitId"
     * JOIN "Units" bu ON bu."Id" = p."BaseUnitId"
     * WHERE u."ShortCode" ~ '^PKG-' OR (NOT x."IsBaseUnit" AND x."ConversionFactorToBase" = 1)
     *    OR (x."IsBaseUnit" AND NOT x."IsSellable" AND NOT x."IsPurchasable")
     *    OR x."ConversionFactorToBase" * power(10, bu."DecimalPlaces") <> trunc(x."ConversionFactorToBase" * power(10, bu."DecimalPlaces"))
     * ORDER BY reason, p."Sku";
     *
     * ---------------------------------------------------------------------------------------------------------
     * Down() is lossy; the pre-deploy snapshot is the real rollback. It restores the old schema and names, refills
     * NULL labels from the unit name, deactivates base rows that are neither sellable nor purchasable (inserted or
     * reactivated by M3), and refuses (raises) if any factor has more than 3 decimals instead of rounding it.
     * Generated 'PKG-*' Units and inserted base rows remain; hierarchy information is lost.
     */
    /// <inheritdoc />
    public partial class IntroduceProductUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ===== M2 structure =====
            migrationBuilder.RenameTable(
                name: "ProductSellingModes",
                newName: "ProductUnits");

            migrationBuilder.Sql("""ALTER TABLE "ProductUnits" RENAME CONSTRAINT "PK_ProductSellingModes" TO "PK_ProductUnits";""");
            migrationBuilder.Sql("""ALTER TABLE "ProductUnits" RENAME CONSTRAINT "FK_ProductSellingModes_Products_ProductId" TO "FK_ProductUnits_Products_ProductId";""");

            migrationBuilder.RenameIndex(
                name: "IX_ProductSellingModes_ProductId",
                table: "ProductUnits",
                newName: "IX_ProductUnits_ProductId");

            migrationBuilder.AddColumn<Guid>(
                name: "UnitId",
                table: "ProductUnits",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InnerProductUnitId",
                table: "ProductUnits",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InnerQuantity",
                table: "ProductUnits",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AddColumn<bool>(
                name: "IsBaseUnit",
                table: "ProductUnits",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Barcode",
                table: "ProductUnits",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPurchasable",
                table: "ProductUnits",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSellable",
                table: "ProductUnits",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDefaultPurchaseUnit",
                table: "ProductUnits",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDefaultSalesUnit",
                table: "ProductUnits",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<short>(
                name: "SortOrder",
                table: "ProductUnits",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AlterColumn<string>(
                name: "Label",
                table: "ProductUnits",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(60)",
                oldMaxLength: 60);

            // Widening: rewrites the (small) table; values are unchanged.
            migrationBuilder.AlterColumn<decimal>(
                name: "ConversionFactorToBase",
                table: "ProductUnits",
                type: "numeric(28,10)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,3)");

            // ===== M3 backfill =====
            // 3.0 guards + parity snapshot (temp tables live for this migration's transaction and are dropped in 3.10).
            migrationBuilder.Sql("""
                DO $g$ BEGIN
                    IF EXISTS (SELECT 1 FROM "ProductUnits" WHERE "ConversionFactorToBase" <= 0) THEN
                        RAISE EXCEPTION 'IntroduceProductUnits: modes with factor <= 0 exist (run P5).';
                    END IF;
                END $g$;
                CREATE TEMP TABLE p2_pre AS SELECT "Id", "ConversionFactorToBase" AS factor FROM "ProductUnits";
                CREATE TEMP TABLE p2_counts AS SELECT (SELECT COUNT(*) FROM "SellingPrices") AS prices,
                    (SELECT COUNT(*) FROM "PurchaseOrderLines") AS po, (SELECT COUNT(*) FROM "SalesInvoiceLines") AS si;
                """);

            // 3.1 working map.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE p2_map AS
                SELECT m."Id", m."ProductId", m."IsActive", m."ConversionFactorToBase" AS factor,
                       lower(btrim(m."Label")) AS label_key, p."BaseUnitId" AS base_unit_id,
                       bu."MeasurementCategory" AS base_cat, bu."StandardFactor" AS base_sf,
                       lower(btrim(m."Label")) IN (lower(bu."Name"), lower(bu."ShortCode")) AS label_is_base,
                       false AS is_base, NULL::uuid AS unit_id, NULL::text AS resolution
                FROM "ProductUnits" m
                JOIN "Products" p ON p."Id" = m."ProductId"
                JOIN "Units" bu ON bu."Id" = p."BaseUnitId";
                """);

            // 3.2 base = first factor-1 row per product (label equals the base unit > active > Id).
            migrationBuilder.Sql("""
                UPDATE p2_map t SET is_base = true, unit_id = t.base_unit_id, resolution = 'base'
                FROM (SELECT "Id", row_number() OVER (PARTITION BY "ProductId" ORDER BY label_is_base DESC, "IsActive" DESC, "Id") AS rn
                      FROM p2_map WHERE factor = 1) r
                WHERE r."Id" = t."Id" AND r.rn = 1;
                """);

            // 3.3 exact (case-insensitive) Name/ShortCode match: active unit, category-compatible (Packaging wraps
            //     anything; Weight/Volume/Length = base category; Count only over a Count base), StandardFactor
            //     consistent with the stored factor, never the base unit itself.
            migrationBuilder.Sql("""
                UPDATE p2_map t SET unit_id = c.unit_id, resolution = 'matched'
                FROM (SELECT DISTINCT ON (m."Id") m."Id", u."Id" AS unit_id
                      FROM p2_map m
                      JOIN "Units" u ON u."IsActive"
                        AND (lower(u."Name") = m.label_key OR lower(u."ShortCode") = m.label_key)
                        AND u."Id" <> m.base_unit_id
                        AND (u."MeasurementCategory" = 'Packaging'
                             OR (u."MeasurementCategory" IN ('Weight','Volume','Length') AND u."MeasurementCategory" = m.base_cat)
                             OR (u."MeasurementCategory" = 'Count' AND m.base_cat = 'Count'))
                        AND (u."StandardFactor" IS NULL OR m.base_sf IS NULL OR u."StandardFactor" / m.base_sf = m.factor)
                      WHERE NOT m.is_base
                      ORDER BY m."Id", (lower(u."Name") = m.label_key) DESC, u."Id") c
                WHERE c."Id" = t."Id";
                """);

            // 3.4 (ProductId, UnitId) conflicts: keep the first (active > Id); the others fall through to generated units.
            migrationBuilder.Sql("""
                UPDATE p2_map t SET unit_id = NULL, resolution = NULL
                FROM (SELECT "Id", row_number() OVER (PARTITION BY "ProductId", unit_id ORDER BY "IsActive" DESC, "Id") AS rn
                      FROM p2_map WHERE resolution = 'matched') d
                WHERE d."Id" = t."Id" AND d.rn > 1;
                """);

            // 3.5 generated Packaging units: one per (label, n-th occurrence of that label within a product), shared
            //     across products. Code PKG-nnn continues after any existing pkg-<digits> code. Name = label only when no
            //     unit has that label as its Name or ShortCode (so an extra "kg"/"g" row never yields a look-alike unit);
            //     otherwise "label [PKG-nnn]".
            migrationBuilder.Sql("""
                CREATE TEMP TABLE p2_rank AS
                SELECT "Id", label_key, row_number() OVER (PARTITION BY "ProductId", label_key ORDER BY "IsActive" DESC, "Id") AS rn
                FROM p2_map WHERE unit_id IS NULL;

                CREATE TEMP TABLE p2_gen AS
                SELECT r.label_key, r.rn, (array_agg(btrim(pu."Label") ORDER BY pu."Id"))[1] AS label,
                       NULL::uuid AS unit_id, NULL::text AS code, NULL::text AS name
                FROM p2_rank r JOIN "ProductUnits" pu ON pu."Id" = r."Id"
                GROUP BY r.label_key, r.rn;

                WITH b AS (SELECT COALESCE(MAX(substring(lower("ShortCode") FROM '^pkg-([0-9]+)$')::int), 0) AS n0 FROM "Units"),
                     n AS (SELECT label_key, rn, row_number() OVER (ORDER BY label_key, rn) + (SELECT n0 FROM b) AS seq FROM p2_gen)
                UPDATE p2_gen g SET unit_id = gen_random_uuid(),
                       code = 'PKG-' || CASE WHEN n.seq < 1000 THEN lpad(n.seq::text, 3, '0') ELSE n.seq::text END
                FROM n WHERE n.label_key = g.label_key AND n.rn = g.rn;

                UPDATE p2_gen g SET name = CASE
                    WHEN g.rn = 1 AND COALESCE(g.label, '') <> ''
                         AND NOT EXISTS (SELECT 1 FROM "Units" u WHERE lower(u."Name") = g.label_key OR lower(u."ShortCode") = g.label_key)
                    THEN left(g.label, 60)
                    ELSE left(COALESCE(NULLIF(g.label, ''), 'Unit'), 47) || ' [' || g.code || ']' END;

                INSERT INTO "Units" ("Id", "Name", "ShortCode", "MeasurementCategory", "DecimalPlaces", "StandardFactor", "IsActive")
                SELECT unit_id, name, code, 'Packaging', 0, NULL, true FROM p2_gen;

                UPDATE p2_map t SET unit_id = g.unit_id, resolution = 'generated'
                FROM p2_rank r JOIN p2_gen g ON g.label_key = r.label_key AND g.rn = r.rn
                WHERE t."Id" = r."Id";
                """);

            // 3.6 base rows. An inactive factor-1 row becomes an active base row that is neither sellable nor
            //     purchasable, which keeps today's POS/purchase behaviour (CK_ProductUnits_Base_Active).
            migrationBuilder.Sql("""
                UPDATE "ProductUnits" pu SET "UnitId" = m.unit_id, "IsBaseUnit" = true, "InnerProductUnitId" = NULL,
                       "InnerQuantity" = 1, "ConversionFactorToBase" = 1, "IsActive" = true,
                       "IsPurchasable" = m."IsActive", "IsSellable" = m."IsActive"
                FROM p2_map m WHERE m."Id" = pu."Id" AND m.is_base;
                """);

            // 3.7 products without any factor-1 mode: new base row (no history; neither sellable nor purchasable).
            migrationBuilder.Sql("""
                INSERT INTO "ProductUnits" ("Id", "ProductId", "UnitId", "Label", "ConversionFactorToBase", "InnerProductUnitId",
                    "InnerQuantity", "IsBaseUnit", "IsActive", "IsPurchasable", "IsSellable", "IsDefaultPurchaseUnit",
                    "IsDefaultSalesUnit", "SortOrder", "Barcode")
                SELECT gen_random_uuid(), p."Id", p."BaseUnitId", NULL, 1, NULL, 1, true, true, false, false, false, false, 0, NULL
                FROM "Products" p
                WHERE NOT EXISTS (SELECT 1 FROM p2_map m WHERE m."ProductId" = p."Id" AND m.is_base);
                """);

            // 3.8 flat hierarchy: every other row = old factor x base row (stored factor unchanged).
            migrationBuilder.Sql("""
                UPDATE "ProductUnits" pu SET "UnitId" = m.unit_id, "IsBaseUnit" = false,
                       "InnerProductUnitId" = b."Id", "InnerQuantity" = m.factor
                FROM p2_map m
                JOIN "ProductUnits" b ON b."ProductId" = m."ProductId" AND b."IsBaseUnit"
                WHERE m."Id" = pu."Id" AND NOT m.is_base;
                """);

            // 3.9 SortOrder: base first, then by factor.
            migrationBuilder.Sql("""
                UPDATE "ProductUnits" pu SET "SortOrder" = s.rn - 1
                FROM (SELECT "Id", row_number() OVER (PARTITION BY "ProductId" ORDER BY "IsBaseUnit" DESC, "ConversionFactorToBase", "Id") AS rn
                      FROM "ProductUnits") s
                WHERE s."Id" = pu."Id";
                """);

            // 3.10 in-transaction assertions (any failure raises and rolls the whole migration back), then cleanup.
            migrationBuilder.Sql("""
                DO $a$ DECLARE cnt int; BEGIN
                    SELECT COUNT(*) INTO cnt FROM "ProductUnits" WHERE "UnitId" IS NULL;
                    IF cnt > 0 THEN RAISE EXCEPTION 'IntroduceProductUnits: % rows without UnitId', cnt; END IF;

                    SELECT COUNT(*) INTO cnt FROM "Products" p
                    WHERE (SELECT COUNT(*) FROM "ProductUnits" x WHERE x."ProductId" = p."Id" AND x."IsBaseUnit") <> 1;
                    IF cnt > 0 THEN RAISE EXCEPTION 'IntroduceProductUnits: % products without exactly one base row', cnt; END IF;

                    SELECT COUNT(*) INTO cnt FROM p2_pre s LEFT JOIN "ProductUnits" x ON x."Id" = s."Id"
                    WHERE x."Id" IS NULL OR x."ConversionFactorToBase" <> s.factor;
                    IF cnt > 0 THEN RAISE EXCEPTION 'IntroduceProductUnits: % rows lost or changed factor', cnt; END IF;

                    SELECT COUNT(*) INTO cnt FROM "ProductUnits" x JOIN "ProductUnits" i ON i."Id" = x."InnerProductUnitId"
                    WHERE i."ProductId" <> x."ProductId" OR x."ConversionFactorToBase" <> x."InnerQuantity" * i."ConversionFactorToBase";
                    IF cnt > 0 THEN RAISE EXCEPTION 'IntroduceProductUnits: % rows with broken inner/factor', cnt; END IF;

                    IF (SELECT prices FROM p2_counts) <> (SELECT COUNT(*) FROM "SellingPrices")
                       OR (SELECT po FROM p2_counts) <> (SELECT COUNT(*) FROM "PurchaseOrderLines")
                       OR (SELECT si FROM p2_counts) <> (SELECT COUNT(*) FROM "SalesInvoiceLines") THEN
                        RAISE EXCEPTION 'IntroduceProductUnits: row counts changed';
                    END IF;
                END $a$;
                DROP TABLE p2_pre, p2_counts, p2_map, p2_rank, p2_gen;
                """);

            // ===== M4 enforce =====
            migrationBuilder.AlterColumn<Guid>(
                name: "UnitId",
                table: "ProductUnits",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ProductUnits_ProductId_Id",
                table: "ProductUnits",
                columns: new[] { "ProductId", "Id" });

            // The AK's (ProductId, Id) index covers the ProductId FK lookups.
            migrationBuilder.DropIndex(
                name: "IX_ProductUnits_ProductId",
                table: "ProductUnits");

            migrationBuilder.DropForeignKey(
                name: "FK_ProductUnits_Products_ProductId",
                table: "ProductUnits");

            migrationBuilder.AddForeignKey(
                name: "FK_ProductUnits_Products_ProductId",
                table: "ProductUnits",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProductUnits_Units_UnitId",
                table: "ProductUnits",
                column: "UnitId",
                principalTable: "Units",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Composite: an inner unit must belong to the same product (MATCH SIMPLE: not checked while inner is NULL).
            migrationBuilder.AddForeignKey(
                name: "FK_ProductUnits_ProductUnits_ProductId_InnerProductUnitId",
                table: "ProductUnits",
                columns: new[] { "ProductId", "InnerProductUnitId" },
                principalTable: "ProductUnits",
                principalColumns: new[] { "ProductId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_ProductId_InnerProductUnitId",
                table: "ProductUnits",
                columns: new[] { "ProductId", "InnerProductUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_UnitId",
                table: "ProductUnits",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_ProductId_UnitId",
                table: "ProductUnits",
                columns: new[] { "ProductId", "UnitId" },
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_ProductId_Base",
                table: "ProductUnits",
                column: "ProductId",
                unique: true,
                filter: "\"IsBaseUnit\"");

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_ProductId_DefaultPurchase",
                table: "ProductUnits",
                column: "ProductId",
                unique: true,
                filter: "\"IsDefaultPurchaseUnit\"");

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_ProductId_DefaultSales",
                table: "ProductUnits",
                column: "ProductId",
                unique: true,
                filter: "\"IsDefaultSalesUnit\"");

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_Barcode",
                table: "ProductUnits",
                column: "Barcode",
                unique: true,
                filter: "\"Barcode\" IS NOT NULL AND \"IsActive\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductUnits_Base_NoInner",
                table: "ProductUnits",
                sql: "\"IsBaseUnit\" = (\"InnerProductUnitId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductUnits_InnerQuantity_Positive",
                table: "ProductUnits",
                sql: "\"InnerQuantity\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductUnits_Factor_Positive",
                table: "ProductUnits",
                sql: "\"ConversionFactorToBase\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductUnits_Base_Identity",
                table: "ProductUnits",
                sql: "NOT \"IsBaseUnit\" OR (\"InnerQuantity\" = 1 AND \"ConversionFactorToBase\" = 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductUnits_Base_Active",
                table: "ProductUnits",
                sql: "NOT \"IsBaseUnit\" OR \"IsActive\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductUnits_Inner_NotSelf",
                table: "ProductUnits",
                sql: "\"InnerProductUnitId\" IS NULL OR \"InnerProductUnitId\" <> \"Id\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductUnits_DefaultPurchase",
                table: "ProductUnits",
                sql: "NOT \"IsDefaultPurchaseUnit\" OR (\"IsPurchasable\" AND \"IsActive\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductUnits_DefaultSales",
                table: "ProductUnits",
                sql: "NOT \"IsDefaultSalesUnit\" OR (\"IsSellable\" AND \"IsActive\")");

            // ===== M5 rename references =====
            // PurchaseOrderLines / SalesInvoiceLines: column, FK (Restrict, unchanged) and index renamed in place.
            migrationBuilder.RenameColumn(
                name: "SellingModeId",
                table: "PurchaseOrderLines",
                newName: "ProductUnitId");

            migrationBuilder.Sql("""ALTER TABLE "PurchaseOrderLines" RENAME CONSTRAINT "FK_PurchaseOrderLines_ProductSellingModes_SellingModeId" TO "FK_PurchaseOrderLines_ProductUnits_ProductUnitId";""");

            migrationBuilder.RenameIndex(
                name: "IX_PurchaseOrderLines_SellingModeId",
                table: "PurchaseOrderLines",
                newName: "IX_PurchaseOrderLines_ProductUnitId");

            migrationBuilder.RenameColumn(
                name: "SellingModeId",
                table: "SalesInvoiceLines",
                newName: "ProductUnitId");

            migrationBuilder.Sql("""ALTER TABLE "SalesInvoiceLines" RENAME CONSTRAINT "FK_SalesInvoiceLines_ProductSellingModes_SellingModeId" TO "FK_SalesInvoiceLines_ProductUnits_ProductUnitId";""");

            migrationBuilder.RenameIndex(
                name: "IX_SalesInvoiceLines_SellingModeId",
                table: "SalesInvoiceLines",
                newName: "IX_SalesInvoiceLines_ProductUnitId");

            // SellingPrices: Cascade -> Restrict (price history must never be deleted silently).
            migrationBuilder.DropForeignKey(
                name: "FK_SellingPrices_ProductSellingModes_ProductSellingModeId",
                table: "SellingPrices");

            migrationBuilder.RenameColumn(
                name: "ProductSellingModeId",
                table: "SellingPrices",
                newName: "ProductUnitId");

            migrationBuilder.RenameIndex(
                name: "IX_SellingPrices_ProductSellingModeId_EffectiveFrom",
                table: "SellingPrices",
                newName: "IX_SellingPrices_ProductUnitId_EffectiveFrom");

            migrationBuilder.AddForeignKey(
                name: "FK_SellingPrices_ProductUnits_ProductUnitId",
                table: "SellingPrices",
                column: "ProductUnitId",
                principalTable: "ProductUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ===== M5 reverse =====
            migrationBuilder.DropForeignKey(
                name: "FK_SellingPrices_ProductUnits_ProductUnitId",
                table: "SellingPrices");

            migrationBuilder.RenameIndex(
                name: "IX_SellingPrices_ProductUnitId_EffectiveFrom",
                table: "SellingPrices",
                newName: "IX_SellingPrices_ProductSellingModeId_EffectiveFrom");

            migrationBuilder.RenameColumn(
                name: "ProductUnitId",
                table: "SellingPrices",
                newName: "ProductSellingModeId");

            migrationBuilder.AddForeignKey(
                name: "FK_SellingPrices_ProductSellingModes_ProductSellingModeId",
                table: "SellingPrices",
                column: "ProductSellingModeId",
                principalTable: "ProductUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.RenameIndex(
                name: "IX_SalesInvoiceLines_ProductUnitId",
                table: "SalesInvoiceLines",
                newName: "IX_SalesInvoiceLines_SellingModeId");

            migrationBuilder.Sql("""ALTER TABLE "SalesInvoiceLines" RENAME CONSTRAINT "FK_SalesInvoiceLines_ProductUnits_ProductUnitId" TO "FK_SalesInvoiceLines_ProductSellingModes_SellingModeId";""");

            migrationBuilder.RenameColumn(
                name: "ProductUnitId",
                table: "SalesInvoiceLines",
                newName: "SellingModeId");

            migrationBuilder.RenameIndex(
                name: "IX_PurchaseOrderLines_ProductUnitId",
                table: "PurchaseOrderLines",
                newName: "IX_PurchaseOrderLines_SellingModeId");

            migrationBuilder.Sql("""ALTER TABLE "PurchaseOrderLines" RENAME CONSTRAINT "FK_PurchaseOrderLines_ProductUnits_ProductUnitId" TO "FK_PurchaseOrderLines_ProductSellingModes_SellingModeId";""");

            migrationBuilder.RenameColumn(
                name: "ProductUnitId",
                table: "PurchaseOrderLines",
                newName: "SellingModeId");

            // ===== M4 reverse =====
            migrationBuilder.DropCheckConstraint(name: "CK_ProductUnits_Base_NoInner", table: "ProductUnits");
            migrationBuilder.DropCheckConstraint(name: "CK_ProductUnits_InnerQuantity_Positive", table: "ProductUnits");
            migrationBuilder.DropCheckConstraint(name: "CK_ProductUnits_Factor_Positive", table: "ProductUnits");
            migrationBuilder.DropCheckConstraint(name: "CK_ProductUnits_Base_Identity", table: "ProductUnits");
            migrationBuilder.DropCheckConstraint(name: "CK_ProductUnits_Base_Active", table: "ProductUnits");
            migrationBuilder.DropCheckConstraint(name: "CK_ProductUnits_Inner_NotSelf", table: "ProductUnits");
            migrationBuilder.DropCheckConstraint(name: "CK_ProductUnits_DefaultPurchase", table: "ProductUnits");
            migrationBuilder.DropCheckConstraint(name: "CK_ProductUnits_DefaultSales", table: "ProductUnits");

            migrationBuilder.DropIndex(name: "IX_ProductUnits_Barcode", table: "ProductUnits");
            migrationBuilder.DropIndex(name: "IX_ProductUnits_ProductId_DefaultSales", table: "ProductUnits");
            migrationBuilder.DropIndex(name: "IX_ProductUnits_ProductId_DefaultPurchase", table: "ProductUnits");
            migrationBuilder.DropIndex(name: "IX_ProductUnits_ProductId_Base", table: "ProductUnits");
            migrationBuilder.DropIndex(name: "IX_ProductUnits_ProductId_UnitId", table: "ProductUnits");
            migrationBuilder.DropIndex(name: "IX_ProductUnits_UnitId", table: "ProductUnits");

            migrationBuilder.DropForeignKey(
                name: "FK_ProductUnits_ProductUnits_ProductId_InnerProductUnitId",
                table: "ProductUnits");

            migrationBuilder.DropIndex(name: "IX_ProductUnits_ProductId_InnerProductUnitId", table: "ProductUnits");

            migrationBuilder.DropForeignKey(
                name: "FK_ProductUnits_Units_UnitId",
                table: "ProductUnits");

            migrationBuilder.DropForeignKey(
                name: "FK_ProductUnits_Products_ProductId",
                table: "ProductUnits");

            migrationBuilder.AddForeignKey(
                name: "FK_ProductUnits_Products_ProductId",
                table: "ProductUnits",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_ProductId",
                table: "ProductUnits",
                column: "ProductId");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ProductUnits_ProductId_Id",
                table: "ProductUnits");

            // ===== M3 reverse (data, lossy) =====
            // Old Label is NOT NULL: fall back to the unit name. Base rows that M3 inserted or reactivated (neither
            // sellable nor purchasable) go back to inactive so the old app keeps hiding them.
            migrationBuilder.Sql("""
                UPDATE "ProductUnits" pu SET "Label" = left(u."Name", 60) FROM "Units" u WHERE u."Id" = pu."UnitId" AND pu."Label" IS NULL;
                UPDATE "ProductUnits" SET "IsActive" = false WHERE "IsBaseUnit" AND NOT "IsSellable" AND NOT "IsPurchasable";
                """);

            // numeric(28,10) -> numeric(18,3) would round silently; refuse instead (restore the snapshot).
            migrationBuilder.Sql("""
                DO $d$ BEGIN
                    IF EXISTS (SELECT 1 FROM "ProductUnits" WHERE "ConversionFactorToBase" <> round("ConversionFactorToBase", 3)) THEN
                        RAISE EXCEPTION 'IntroduceProductUnits Down: factors with more than 3 decimals exist; restore the pre-deploy snapshot instead.';
                    END IF;
                END $d$;
                """);

            // ===== M2 reverse =====
            migrationBuilder.DropColumn(name: "UnitId", table: "ProductUnits");
            migrationBuilder.DropColumn(name: "InnerProductUnitId", table: "ProductUnits");
            migrationBuilder.DropColumn(name: "InnerQuantity", table: "ProductUnits");
            migrationBuilder.DropColumn(name: "IsBaseUnit", table: "ProductUnits");
            migrationBuilder.DropColumn(name: "Barcode", table: "ProductUnits");
            migrationBuilder.DropColumn(name: "IsPurchasable", table: "ProductUnits");
            migrationBuilder.DropColumn(name: "IsSellable", table: "ProductUnits");
            migrationBuilder.DropColumn(name: "IsDefaultPurchaseUnit", table: "ProductUnits");
            migrationBuilder.DropColumn(name: "IsDefaultSalesUnit", table: "ProductUnits");
            migrationBuilder.DropColumn(name: "SortOrder", table: "ProductUnits");

            migrationBuilder.AlterColumn<string>(
                name: "Label",
                table: "ProductUnits",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(60)",
                oldMaxLength: 60,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "ConversionFactorToBase",
                table: "ProductUnits",
                type: "numeric(18,3)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(28,10)");

            migrationBuilder.RenameIndex(
                name: "IX_ProductUnits_ProductId",
                table: "ProductUnits",
                newName: "IX_ProductSellingModes_ProductId");

            migrationBuilder.Sql("""ALTER TABLE "ProductUnits" RENAME CONSTRAINT "FK_ProductUnits_Products_ProductId" TO "FK_ProductSellingModes_Products_ProductId";""");
            migrationBuilder.Sql("""ALTER TABLE "ProductUnits" RENAME CONSTRAINT "PK_ProductUnits" TO "PK_ProductSellingModes";""");

            migrationBuilder.RenameTable(
                name: "ProductUnits",
                newName: "ProductSellingModes");
        }
    }
}
