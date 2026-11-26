using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Phase 6a migration <c>AddMovementTypesAndReasonCode</c> on a legacy ledger written into the <c>AddSalesUnitSnapshots</c>
/// schema with every movement type (cancelled purchase as a negative 'Purchase', OpeningStock adjustment, an adjustment
/// decrease split over two batches, an aggregated legacy sale cancellation over two lines, an ambiguous sale and an
/// invoice with identical duplicate lines). Checks the relabels, ReasonCode and entered backfill (unambiguous rows filled,
/// ambiguous rows stay NULL), the V-block from the migration header incl. V9 for rows written after the migration and the
/// V10 coverage, guards P1-P3 (each raises and rolls back), Down (labels restored, schema equals the previous one), Down then
/// Up, and the idempotent script over a plain connection, run twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddMovementTypesAndReasonCodeMigrationTests(PostgresFixture postgres)
{
    private const string Previous = "20261106085008_AddSalesUnitSnapshots";
    private const string M8 = "20261109053202_AddMovementTypesAndReasonCode";

    private static Guid G(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:x12}");

    private const string FixtureSql = """
CREATE FUNCTION pg_temp.g(n int) RETURNS uuid LANGUAGE sql AS $$ SELECT ('00000000-0000-0000-0000-' || lpad(to_hex(n), 12, '0'))::uuid $$;

INSERT INTO "Units" ("Id", "Name", "ShortCode", "MeasurementCategory", "DecimalPlaces", "StandardFactor", "IsActive") VALUES
  (pg_temp.g(1), 'Piece', 'pc', 'Count', 0, NULL, true), (pg_temp.g(2), 'Box', 'box', 'Packaging', 0, NULL, true),
  (pg_temp.g(3), 'Kilogram', 'kg', 'Weight', 3, 1, true);
INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive") VALUES (pg_temp.g(10), 'GST', 5, true);
INSERT INTO "Roles" ("Id", "Name", "Description", "IsActive", "IsSystem") VALUES (pg_temp.g(11), 'Administrator', NULL, true, true);
INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username")
VALUES (pg_temp.g(12), now(), 'a@b.c', 'R', 'K', 'x', pg_temp.g(11), 'Active', 'admin');
INSERT INTO "Customers" ("Id", "Name", "IsActive", "IsWalkIn") VALUES (pg_temp.g(13), 'Walk-in', true, true);
INSERT INTO "Suppliers" ("Id", "Name", "IsActive") VALUES (pg_temp.g(14), 'Sup', true);
INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive") VALUES
  (pg_temp.g(20), 'PEN', 'Pen', NULL, pg_temp.g(1), pg_temp.g(10), 0, true), (pg_temp.g(21), 'RICE', 'Rice', NULL, pg_temp.g(3), pg_temp.g(10), 0, true);
INSERT INTO "ProductUnits" ("Id", "ProductId", "UnitId", "InnerProductUnitId", "InnerQuantity", "ConversionFactorToBase", "IsBaseUnit",
  "Label", "Barcode", "IsPurchasable", "IsSellable", "IsDefaultPurchaseUnit", "IsDefaultSalesUnit", "SortOrder", "IsActive") VALUES
  (pg_temp.g(30), pg_temp.g(20), pg_temp.g(1), NULL, 1, 1, true, NULL, NULL, true, true, false, false, 0, true),
  (pg_temp.g(31), pg_temp.g(20), pg_temp.g(2), pg_temp.g(30), 10, 10, false, NULL, NULL, true, true, false, false, 1, true),
  (pg_temp.g(32), pg_temp.g(21), pg_temp.g(3), NULL, 1, 1, true, NULL, NULL, true, true, false, false, 0, true);

-- Purchases: PO1 completed 5 Box; PO2 cancelled 5 pc (legacy: +5 Purchase and -5 Purchase)
INSERT INTO "PurchaseOrders" ("Id", "CreatedAt", "CreatedByUserId", "DiscountTotal", "GrandTotal", "OtherChargesTotal", "PaidAmount", "PaymentMethod", "PurchaseDate", "PurchaseNo", "Status", "Subtotal", "SupplierId", "TaxTotal") VALUES
  (pg_temp.g(40), now(), pg_temp.g(12), 0, 0, 0, 0, 'Cash', DATE '2026-09-01', 'PUR-0001', 'Completed', 0, pg_temp.g(14), 0),
  (pg_temp.g(41), now(), pg_temp.g(12), 0, 0, 0, 0, 'Cash', DATE '2026-09-02', 'PUR-0002', 'Cancelled', 0, pg_temp.g(14), 0);
INSERT INTO "PurchaseOrderLines" ("Id", "PurchaseOrderId", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "Qty", "ConversionFactorToBase", "BaseQty", "Rate", "Discount", "OtherCharges", "TaxId", "TaxRatePercent", "TaxableValue", "TaxAmount", "LineTotal", "UnitCost") VALUES
  (pg_temp.g(50), pg_temp.g(40), pg_temp.g(20), pg_temp.g(31), pg_temp.g(2), 'box', 5, 10, 50, 100, 0, 0, pg_temp.g(10), 5, 500, 25, 525, 10),
  (pg_temp.g(51), pg_temp.g(41), pg_temp.g(20), pg_temp.g(30), pg_temp.g(1), 'pc', 5, 1, 5, 10, 0, 0, pg_temp.g(10), 5, 50, 2.5, 52.5, 10);
INSERT INTO "PurchaseBatches" ("Id", "CreatedAt", "ProductId", "PurchaseOrderLineId", "PurchasedBaseQty", "ReceivedDate", "RemainingBaseQty", "StockAdjustmentId", "UnitCost") VALUES
  (pg_temp.g(60), now(), pg_temp.g(20), pg_temp.g(50), 50, DATE '2026-09-01', 10, NULL, 10),
  (pg_temp.g(61), now(), pg_temp.g(20), pg_temp.g(51), 5, DATE '2026-09-02', 0, NULL, 10);
INSERT INTO "PurchaseReturns" ("Id", "CreatedAt", "CreatedByUserId", "OriginalTotal", "PurchaseOrderId", "ReturnDate", "ReturnNo", "TaxTotal", "TaxableTotal", "Total") VALUES
  (pg_temp.g(70), now(), pg_temp.g(12), 0, pg_temp.g(40), DATE '2026-09-03', 'PRT-0001', 0, 0, 0);
INSERT INTO "PurchaseReturnLines" ("Id", "PurchaseReturnId", "PurchaseOrderLineId", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "Qty", "ConversionFactorToBase", "BaseQty", "CostRemoved", "TaxableValue", "TaxAmount", "LineTotal") VALUES
  (pg_temp.g(71), pg_temp.g(70), pg_temp.g(50), pg_temp.g(20), pg_temp.g(30), pg_temp.g(1), 'pc', 10, 1, 10, 100, 0, 0, 0);

-- Sales: S1 (1 box + 3 pc), S3 (1 box + 10 pc: ambiguous base qty), S2 cancelled (1 box + 3 pc; aggregated cancellation)
INSERT INTO "SalesInvoices" ("Id", "InvoiceNo", "CustomerId", "InvoiceDate", "PaymentMethod", "PaidAmount", "Subtotal", "TaxTotal", "DiscountTotal", "GrandTotal", "Status", "CreatedByUserId", "CreatedAt") VALUES
  (pg_temp.g(80), 'SAL-0001', pg_temp.g(13), DATE '2026-09-04', 'Cash', 0, 0, 0, 0, 0, 'Completed', pg_temp.g(12), now()),
  (pg_temp.g(81), 'SAL-0002', pg_temp.g(13), DATE '2026-09-05', 'Cash', 0, 0, 0, 0, 0, 'Cancelled', pg_temp.g(12), now()),
  (pg_temp.g(82), 'SAL-0003', pg_temp.g(13), DATE '2026-09-06', 'Cash', 0, 0, 0, 0, 0, 'Completed', pg_temp.g(12), now());
INSERT INTO "SalesInvoiceLines" ("Id", "SalesInvoiceId", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "Qty", "ConversionFactorToBase", "BaseQty", "UnitPrice", "TaxId", "TaxRatePercent", "Discount", "TaxableValue", "TaxAmount", "LineTotal", "CostOfGoodsSold") VALUES
  (pg_temp.g(90), pg_temp.g(80), pg_temp.g(20), pg_temp.g(31), pg_temp.g(2), 'box', 1, 10, 10, 50, pg_temp.g(10), 5, 0, 50, 2.5, 52.5, 100),
  (pg_temp.g(91), pg_temp.g(80), pg_temp.g(20), pg_temp.g(30), pg_temp.g(1), 'pc', 3, 1, 3, 6, pg_temp.g(10), 5, 0, 18, 0.9, 18.9, 30),
  (pg_temp.g(92), pg_temp.g(81), pg_temp.g(20), pg_temp.g(31), pg_temp.g(2), 'box', 1, 10, 10, 50, pg_temp.g(10), 5, 0, 50, 2.5, 52.5, 100),
  (pg_temp.g(93), pg_temp.g(81), pg_temp.g(20), pg_temp.g(30), pg_temp.g(1), 'pc', 3, 1, 3, 6, pg_temp.g(10), 5, 0, 18, 0.9, 18.9, 30),
  (pg_temp.g(94), pg_temp.g(82), pg_temp.g(20), pg_temp.g(31), pg_temp.g(2), 'box', 1, 10, 10, 50, pg_temp.g(10), 5, 0, 50, 2.5, 52.5, 100),
  (pg_temp.g(95), pg_temp.g(82), pg_temp.g(20), pg_temp.g(30), pg_temp.g(1), 'pc', 10, 1, 10, 5, pg_temp.g(10), 5, 0, 50, 2.5, 52.5, 100);
INSERT INTO "SalesReturns" ("Id", "ReturnNo", "SalesInvoiceId", "ReturnDate", "TaxableTotal", "TaxTotal", "Total", "CreatedByUserId", "CreatedAt") VALUES
  (pg_temp.g(100), 'SRT-0001', pg_temp.g(80), DATE '2026-09-07', 0, 0, 0, pg_temp.g(12), now());
INSERT INTO "SalesReturnLines" ("Id", "SalesReturnId", "SalesInvoiceLineId", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "Qty", "ConversionFactorToBase", "BaseQty", "TaxableValue", "TaxAmount", "LineTotal", "CostRestored") VALUES
  (pg_temp.g(101), pg_temp.g(100), pg_temp.g(91), pg_temp.g(20), pg_temp.g(30), pg_temp.g(1), 'pc', 3, 1, 3, 0, 0, 0, 30);

-- Rice adjustments: opening 100 kg (batch BR1), correction +50 (BR2), damage 120 kg across both, loss 5 kg from BR2
INSERT INTO "StockAdjustments" ("Id", "AdjustmentNo", "AfterQty", "BeforeQty", "ConversionFactorToBase", "CreatedAt", "Direction", "EnteredQuantity", "ProductId", "ProductUnitId", "QuantityBaseUnits", "Type", "UnitCode", "UnitId", "UserId") VALUES
  (pg_temp.g(110), 'ADJ-0001', 100, 0, 1, now(), 'Increase', 100, pg_temp.g(21), pg_temp.g(32), 100, 'OpeningStock', 'kg', pg_temp.g(3), pg_temp.g(12)),
  (pg_temp.g(111), 'ADJ-0002', 150, 100, 1, now(), 'Increase', 50, pg_temp.g(21), pg_temp.g(32), 50, 'Correction', 'kg', pg_temp.g(3), pg_temp.g(12)),
  (pg_temp.g(112), 'ADJ-0003', 30, 150, 1, now(), 'Decrease', 120, pg_temp.g(21), pg_temp.g(32), 120, 'Damage', 'kg', pg_temp.g(3), pg_temp.g(12)),
  (pg_temp.g(113), 'ADJ-0004', 25, 30, 1, now(), 'Decrease', 5, pg_temp.g(21), pg_temp.g(32), 5, 'Loss', 'kg', pg_temp.g(3), pg_temp.g(12));
INSERT INTO "PurchaseBatches" ("Id", "CreatedAt", "ProductId", "PurchaseOrderLineId", "PurchasedBaseQty", "ReceivedDate", "RemainingBaseQty", "StockAdjustmentId", "UnitCost") VALUES
  (pg_temp.g(62), now(), pg_temp.g(21), NULL, 100, DATE '2026-09-08', 0, pg_temp.g(110), 40),
  (pg_temp.g(63), now(), pg_temp.g(21), NULL, 50, DATE '2026-09-09', 25, pg_temp.g(111), 40);

INSERT INTO "StockItems" ("ProductId", "QuantityOnHand", "AverageCost") VALUES (pg_temp.g(20), 10, 10), (pg_temp.g(21), 25, 40);

-- Legacy ledger (pre-6a labels)
INSERT INTO "StockMovements" ("Id", "ProductId", "Type", "QuantityBaseUnits", "PurchaseBatchId", "ReferenceType", "ReferenceId", "ReferenceNo", "UserId", "OccurredAt", "Notes") VALUES
  (pg_temp.g(200), pg_temp.g(20), 'Purchase', 50, pg_temp.g(60), 'PurchaseOrder', pg_temp.g(40), 'PUR-0001', pg_temp.g(12), now(), NULL),
  (pg_temp.g(201), pg_temp.g(20), 'Purchase', 5, pg_temp.g(61), 'PurchaseOrder', pg_temp.g(41), 'PUR-0002', pg_temp.g(12), now(), NULL),
  (pg_temp.g(202), pg_temp.g(20), 'Purchase', -5, pg_temp.g(61), 'PurchaseOrder', pg_temp.g(41), 'PUR-0002', pg_temp.g(12), now(), 'Purchase cancelled'),
  (pg_temp.g(203), pg_temp.g(20), 'PurchaseReturn', -10, pg_temp.g(60), 'PurchaseReturn', pg_temp.g(70), 'PRT-0001', pg_temp.g(12), now(), NULL),
  (pg_temp.g(204), pg_temp.g(20), 'Sale', -10, NULL, 'SalesInvoice', pg_temp.g(80), 'SAL-0001', pg_temp.g(12), now(), NULL),
  (pg_temp.g(205), pg_temp.g(20), 'Sale', -3, NULL, 'SalesInvoice', pg_temp.g(80), 'SAL-0001', pg_temp.g(12), now(), NULL),
  (pg_temp.g(206), pg_temp.g(20), 'SaleReturn', 3, NULL, 'SalesReturn', pg_temp.g(100), 'SRT-0001', pg_temp.g(12), now(), NULL),
  (pg_temp.g(207), pg_temp.g(20), 'Sale', -10, NULL, 'SalesInvoice', pg_temp.g(81), 'SAL-0002', pg_temp.g(12), now(), NULL),
  (pg_temp.g(208), pg_temp.g(20), 'Sale', -3, NULL, 'SalesInvoice', pg_temp.g(81), 'SAL-0002', pg_temp.g(12), now(), NULL),
  (pg_temp.g(209), pg_temp.g(20), 'SaleCancellation', 13, NULL, 'SalesInvoice', pg_temp.g(81), 'SAL-0002', pg_temp.g(12), now(), 'Sale cancelled'),
  (pg_temp.g(210), pg_temp.g(20), 'Sale', -10, NULL, 'SalesInvoice', pg_temp.g(82), 'SAL-0003', pg_temp.g(12), now(), NULL),
  (pg_temp.g(211), pg_temp.g(20), 'Sale', -10, NULL, 'SalesInvoice', pg_temp.g(82), 'SAL-0003', pg_temp.g(12), now(), NULL),
  (pg_temp.g(220), pg_temp.g(21), 'Adjustment', 100, pg_temp.g(62), 'StockAdjustment', pg_temp.g(110), 'ADJ-0001', pg_temp.g(12), now(), 'OpeningStock'),
  (pg_temp.g(221), pg_temp.g(21), 'Adjustment', 50, pg_temp.g(63), 'StockAdjustment', pg_temp.g(111), 'ADJ-0002', pg_temp.g(12), now(), 'Correction'),
  (pg_temp.g(222), pg_temp.g(21), 'Adjustment', -100, pg_temp.g(62), 'StockAdjustment', pg_temp.g(112), 'ADJ-0003', pg_temp.g(12), now(), 'Damage'),
  (pg_temp.g(223), pg_temp.g(21), 'Adjustment', -20, pg_temp.g(63), 'StockAdjustment', pg_temp.g(112), 'ADJ-0003', pg_temp.g(12), now(), 'Damage'),
  (pg_temp.g(224), pg_temp.g(21), 'Adjustment', -5, pg_temp.g(63), 'StockAdjustment', pg_temp.g(113), 'ADJ-0004', pg_temp.g(12), now(), 'Loss');

-- S4: two IDENTICAL pc lines (identical duplicates are filled), pen stock drops by 4 (batch 60: 10 -> 6)
INSERT INTO "SalesInvoices" ("Id", "InvoiceNo", "CustomerId", "InvoiceDate", "PaymentMethod", "PaidAmount", "Subtotal", "TaxTotal", "DiscountTotal", "GrandTotal", "Status", "CreatedByUserId", "CreatedAt") VALUES
  (pg_temp.g(83), 'SAL-0004', pg_temp.g(13), DATE '2026-09-07', 'Cash', 0, 0, 0, 0, 0, 'Completed', pg_temp.g(12), now());
INSERT INTO "SalesInvoiceLines" ("Id", "SalesInvoiceId", "ProductId", "ProductUnitId", "UnitId", "UnitCode", "Qty", "ConversionFactorToBase", "BaseQty", "UnitPrice", "TaxId", "TaxRatePercent", "Discount", "TaxableValue", "TaxAmount", "LineTotal", "CostOfGoodsSold") VALUES
  (pg_temp.g(96), pg_temp.g(83), pg_temp.g(20), pg_temp.g(30), pg_temp.g(1), 'pc', 2, 1, 2, 6, pg_temp.g(10), 5, 0, 12, 0.6, 12.6, 20),
  (pg_temp.g(97), pg_temp.g(83), pg_temp.g(20), pg_temp.g(30), pg_temp.g(1), 'pc', 2, 1, 2, 6, pg_temp.g(10), 5, 0, 12, 0.6, 12.6, 20);
INSERT INTO "StockMovements" ("Id", "ProductId", "Type", "QuantityBaseUnits", "PurchaseBatchId", "ReferenceType", "ReferenceId", "ReferenceNo", "UserId", "OccurredAt", "Notes") VALUES
  (pg_temp.g(212), pg_temp.g(20), 'Sale', -2, NULL, 'SalesInvoice', pg_temp.g(83), 'SAL-0004', pg_temp.g(12), now(), NULL),
  (pg_temp.g(213), pg_temp.g(20), 'Sale', -2, NULL, 'SalesInvoice', pg_temp.g(83), 'SAL-0004', pg_temp.g(12), now(), NULL);
UPDATE "PurchaseBatches" SET "RemainingBaseQty" = 6 WHERE "Id" = pg_temp.g(60);
UPDATE "StockItems" SET "QuantityOnHand" = 6 WHERE "ProductId" = pg_temp.g(20);

""";

    /// <summary>Id → (Type, ReasonCode, EnteredUnitCode, EnteredQuantity, ConversionFactorSnapshot) expected after Up.</summary>
    private static readonly Dictionary<int, (string Type, string? Reason, string? Code, decimal? Qty, decimal? Factor)> Expected = new()
    {
        [200] = ("Purchase", null, "box", 5m, 10m),               // exact through batch -> purchase line
        [201] = ("Purchase", null, "pc", 5m, 1m),
        [202] = ("PurchaseCancellation", null, "pc", 5m, 1m),     // negative Purchase of a cancelled purchase is relabelled
        [203] = ("PurchaseReturn", null, "pc", 10m, 1m),          // the return's own unit, not the Box of the purchase line
        [204] = ("Sale", null, "box", 1m, 10m),
        [205] = ("Sale", null, "pc", 3m, 1m),
        [206] = ("SaleReturn", null, "pc", 3m, 1m),
        [207] = ("Sale", null, "box", 1m, 10m),
        [208] = ("Sale", null, "pc", 3m, 1m),
        [209] = ("SaleCancellation", null, null, null, null),      // aggregated legacy row over two lines (13) matches no single line
        [210] = ("Sale", null, null, null, null),                  // ambiguous: 10 = 1 box line or 10 pc line
        [211] = ("Sale", null, null, null, null),
        [212] = ("Sale", null, "pc", 2m, 1m),                      // identical duplicate lines agree, so they are filled
        [213] = ("Sale", null, "pc", 2m, 1m),
        [220] = ("OpeningStock", "OpeningStock", "kg", 100m, 1m),
        [221] = ("Adjustment", "Correction", "kg", 50m, 1m),
        [222] = ("Adjustment", "Damage", "kg", null, 1m),           // split over two batches: unit context only
        [223] = ("Adjustment", "Damage", "kg", null, 1m),
        [224] = ("Adjustment", "Loss", "kg", 5m, 1m),
    };

    private static async Task<TestDatabase> CreateLegacyDatabaseAsync(PostgresFixture postgres, string? extraSql = null)
    {
        var database = await postgres.CreateDatabaseAsync(migrate: false);
        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(Previous);
            Assert.DoesNotContain(M8, await db.Database.GetAppliedMigrationsAsync());
        }
        await ExecuteAsync(database, FixtureSql + (extraSql ?? ""));
        return database;
    }

    private static string Insert(int id, string type, string qty, string refType, int refId, string refNo) =>
        $"""
        INSERT INTO "StockMovements" ("Id", "ProductId", "Type", "QuantityBaseUnits", "ReferenceType", "ReferenceId", "ReferenceNo", "UserId", "OccurredAt")
        VALUES ('{G(id)}', '{G(20)}', '{type}', {qty}, '{refType}', '{G(refId)}', '{refNo}', '{G(12)}', now());
        """;

    private const string LedgerSql = """
        SELECT "Id"::text || '|' || "Type" || '|' || "QuantityBaseUnits"::text FROM "StockMovements" ORDER BY "Id"
        """;

    private const string RowHashSql = """SELECT md5(string_agg(m::text, '|' ORDER BY m."Id")) FROM "StockMovements" m""";

    /// <summary>V2-V8 of the migration header; each must return no rows (V1 = row count, V9 below).</summary>
    private static readonly Dictionary<string, string> VBlock = new()
    {
        ["V2 negative Purchase"] = """SELECT "Id" FROM "StockMovements" WHERE "Type" = 'Purchase' AND "QuantityBaseUnits" < 0""",
        ["V3 reason code missing"] = """SELECT "Id" FROM "StockMovements" WHERE "ReferenceType" = 'StockAdjustment' AND "ReasonCode" IS NULL""",
        ["V3 Adjustment with OpeningStock reason"] = """SELECT "Id" FROM "StockMovements" WHERE "Type" = 'Adjustment' AND "ReasonCode" = 'OpeningStock'""",
        ["V4 entered arithmetic"] = """SELECT "Id" FROM "StockMovements" WHERE "EnteredQuantity" IS NOT NULL AND "EnteredQuantity" * "ConversionFactorSnapshot" <> abs("QuantityBaseUnits")""",
        ["V5 all-or-none"] = """
            SELECT "Id" FROM "StockMovements" WHERE ("EnteredProductUnitId" IS NULL) <> ("EnteredUnitCode" IS NULL)
               OR ("EnteredProductUnitId" IS NULL) <> ("ConversionFactorSnapshot" IS NULL)
            """,
        ["V6 unit of another product"] = """SELECT m."Id" FROM "StockMovements" m JOIN "ProductUnits" pu ON pu."Id" = m."EnteredProductUnitId" WHERE pu."ProductId" <> m."ProductId" """,
    };

    private static async Task AssertVBlockEmptyAsync(TestDatabase database, long rowsBefore)
    {
        Assert.Equal(rowsBefore, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockMovements" """)); // V1
        foreach (var (name, sql) in VBlock) Assert.True((await QueryAsync(database, sql, r => r.GetGuid(0))).Count == 0, $"{name} returned rows.");
        await using var db = database.CreateDbContext();
        await StockInvariants.AssertHoldAsync(db); // V7 / V8
    }

    private static async Task AssertBackfilledAsync(TestDatabase database)
    {
        var rows = (await QueryAsync(database, """
            SELECT "Id", "Type", "ReasonCode", "EnteredUnitCode", "EnteredQuantity", "ConversionFactorSnapshot", "EnteredProductUnitId" FROM "StockMovements"
            """, r => (Id: r.GetGuid(0), Type: r.GetString(1), Reason: r.IsDBNull(2) ? null : r.GetString(2), Code: r.IsDBNull(3) ? null : r.GetString(3),
                Qty: r.IsDBNull(4) ? (decimal?)null : r.GetDecimal(4), Factor: r.IsDBNull(5) ? (decimal?)null : r.GetDecimal(5), Unit: r.IsDBNull(6) ? (Guid?)null : r.GetGuid(6))))
            .ToDictionary(r => r.Id);
        Assert.Equal(Expected.Count, rows.Count);
        foreach (var (n, e) in Expected)
        {
            var r = rows[G(n)];
            Assert.True((e.Type, e.Reason, e.Code, e.Qty, e.Factor) == (r.Type, r.Reason, r.Code, r.Qty, r.Factor), $"Movement {n}: expected {e}, got ({r.Type}, {r.Reason}, {r.Code}, {r.Qty}, {r.Factor}).");
            Assert.Equal(e.Code is null, r.Unit is null);
        }
        Assert.Equal(G(31), rows[G(200)].Unit);   // the Box row of the Pen
        Assert.Equal(G(30), rows[G(203)].Unit);   // the return's Piece row
        Assert.Equal(G(32), rows[G(220)].Unit);   // the Rice base row
    }

    [Fact]
    public async Task AddMovementTypesAndReasonCode_LegacyLedgerOfEveryType_RelabelsBackfillsAndVBlockEmpty()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var rowsBefore = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockMovements" """);
        Assert.Equal(Expected.Count, rowsBefore);
        await using (var check = database.CreateDbContext()) await StockInvariants.AssertHoldAsync(check); // the fixture itself reconciles

        await using (var db = database.CreateDbContext())
        {
            await db.MigrateToHeadAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
        }

        await AssertBackfilledAsync(database);
        await AssertVBlockEmptyAsync(database, rowsBefore);
        // V10 coverage per type: rows without a unit are exactly the legacy-ambiguous ones.
        var coverage = await QueryAsync(database, """
            SELECT "Type" || '=' || COUNT(*) FILTER (WHERE "EnteredProductUnitId" IS NULL) || '/' || COUNT(*) FROM "StockMovements" GROUP BY "Type" ORDER BY 1
            """, r => r.GetString(0));
        Assert.Equal(new[] { "Adjustment=0/4", "OpeningStock=0/1", "Purchase=0/2", "PurchaseCancellation=0/1", "PurchaseReturn=0/1", "Sale=2/8", "SaleCancellation=1/1", "SaleReturn=0/1" }, coverage);
        var constraints = (await QueryAsync(database, """
            SELECT c.conname FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid WHERE t.relname = 'StockMovements' AND c.conname LIKE '%Entered%' OR c.conname LIKE 'CK_StockMovements_%'
            """, r => r.GetString(0))).ToHashSet();
        Assert.Superset(new HashSet<string>
        {
            "CK_StockMovements_EnteredUnit_AllOrNone", "CK_StockMovements_EnteredQuantity_RequiresUnit", "CK_StockMovements_EnteredQuantity_Positive",
            "CK_StockMovements_Factor_Positive", "CK_StockMovements_UnitCode_NotEmpty", "CK_StockMovements_Entered_Arithmetic",
            "CK_StockMovements_ReasonCode", "CK_StockMovements_Type_Sign", "FK_StockMovements_ProductUnits_ProductId_EnteredProductUnitId",
        }, constraints);
        Assert.Contains("IX_StockMovements_ReferenceType_ReferenceId", await QueryAsync(database, """SELECT indexname FROM pg_indexes WHERE tablename = 'StockMovements'""", r => r.GetString(0)));
    }

    /// <summary>V9: after the migration the real services write the unit context on every new row (a pack-unit correction, then a
    /// pack decrease that spans two batches), and the post-deploy acceptance query returns zero.</summary>
    [Fact]
    public async Task AddMovementTypesAndReasonCode_NewWritesAfterMigration_V9ReturnsZeroAndSplitRowsKeepUnitOnly()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        await using (var db = database.CreateDbContext()) await db.MigrateToHeadAsync();
        var deployed = DateTimeOffset.UtcNow; // legacy rows were stamped now() by the fixture, before this instant

        await using (var db = database.CreateDbContext())
        {
            var service = new StockAdjustmentService(db);
            await service.CreateAdjustmentAsync(new CreateAdjustmentInput(G(20), G(31), AdjustmentType.Correction, AdjustmentDirection.Increase, 1m, 100m, null), G(12)); // +10 pc in 1 new batch
            await service.CreateAdjustmentAsync(new CreateAdjustmentInput(G(20), G(31), AdjustmentType.Damage, AdjustmentDirection.Decrease, 1m, null, null), G(12));   // -10: 6 from batch 60 + 4 from the new one
        }

        var v9 = await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "StockMovements" WHERE "OccurredAt" >= TIMESTAMPTZ '{deployed:O}' AND "EnteredProductUnitId" IS NULL""");
        Assert.Equal(0L, v9);
        await using var check = database.CreateDbContext();
        var fresh = await check.StockMovements.AsNoTracking().Where(m => m.OccurredAt >= deployed).OrderBy(m => m.OccurredAt).ToListAsync();
        Assert.Equal(3, fresh.Count);
        Assert.Equal((StockMovementType.Adjustment, AdjustmentType.Correction, 10m, (decimal?)1m), (fresh[0].Type, fresh[0].ReasonCode!.Value, fresh[0].QuantityBaseUnits, fresh[0].EnteredQuantity));
        Assert.All(fresh.Skip(1), m => Assert.Equal((G(31), "box", 10m, (decimal?)null, AdjustmentType.Damage), (m.EnteredProductUnitId!.Value, m.EnteredUnitCode, m.ConversionFactorSnapshot!.Value, m.EnteredQuantity, m.ReasonCode!.Value)));
        Assert.Equal(-10m, fresh.Skip(1).Sum(m => m.QuantityBaseUnits));
        await StockInvariants.AssertHoldAsync(check);
    }

    [Theory]
    [InlineData("P1", "Adjustment", "1", "StockAdjustment", 999, "run P1")]
    [InlineData("P1", "Adjustment", "1", "SalesInvoice", 80, "run P1")]
    [InlineData("P2", "Purchase", "-1", "PurchaseOrder", 40, "run P2")]   // purchase is Completed, not Cancelled
    [InlineData("P2", "Purchase", "-1", "SalesInvoice", 80, "run P2")]
    [InlineData("P3", "Sale", "1", "SalesInvoice", 80, "run P3")]
    [InlineData("P3", "SaleReturn", "-1", "SalesReturn", 100, "run P3")]
    [InlineData("P3", "SaleCancellation", "0", "SalesInvoice", 81, "run P3")]
    [InlineData("P3", "PurchaseReturn", "1", "PurchaseReturn", 70, "run P3")]
    [InlineData("P3", "Purchase", "0", "PurchaseOrder", 40, "run P3")]
    public async Task AddMovementTypesAndReasonCode_GuardViolation_RaisesAndRollsBackAtomically(string guard, string type, string qty, string refType, int refId, string expected)
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, Insert(300, type, qty, refType, refId, "BAD-1"));
        var before = await QueryAsync(database, LedgerSql, r => r.GetString(0));

        await using (var db = database.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.MigrateToHeadAsync());
            Assert.Contains(expected, ex.MessageText);
            Assert.Contains(guard, ex.MessageText); // the raising guard is named in the error
        }

        await using var check = database.CreateDbContext();
        Assert.DoesNotContain(M8, await check.Database.GetAppliedMigrationsAsync());
        Assert.DoesNotContain("ReasonCode", await ColumnsAsync(database, "StockMovements"));
        Assert.Equal(before, await QueryAsync(database, LedgerSql, r => r.GetString(0))); // nothing relabelled
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_StockMovements_ReferenceType_ReferenceId'"""));
    }

    [Fact]
    public async Task AddMovementTypesAndReasonCode_Down_RestoresLabelsAndTheSchemaOfThePreviousMigration()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var before = await QueryAsync(database, LedgerSql, r => r.GetString(0));
        await using (var db = database.CreateDbContext()) await db.MigrateToHeadAsync();
        Assert.NotEqual(before, await QueryAsync(database, LedgerSql, r => r.GetString(0))); // Up relabelled

        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(Previous);
            Assert.DoesNotContain(M8, await db.Database.GetAppliedMigrationsAsync());
        }

        Assert.Equal(before, await QueryAsync(database, LedgerSql, r => r.GetString(0))); // types, signs and quantities exactly as before
        Assert.Equal(new[] { "Adjustment", "Purchase", "PurchaseReturn", "Sale", "SaleCancellation", "SaleReturn" },
            await QueryAsync(database, """SELECT DISTINCT "Type" FROM "StockMovements" ORDER BY 1""", r => r.GetString(0)));
        await using var reference = await postgres.CreateDatabaseAsync(migrate: false);
        await using (var db = reference.CreateDbContext()) await db.GetService<IMigrator>().MigrateAsync(Previous);
        var expectedSchema = await SchemaSignatureAsync(reference);
        var actual = await SchemaSignatureAsync(database);
        Assert.Equal(Array.Empty<string>(), expectedSchema.Except(actual).Order());
        Assert.Equal(Array.Empty<string>(), actual.Except(expectedSchema).Order());
        await using var check = database.CreateDbContext();
        await StockInvariants.AssertHoldAsync(check);
    }

    [Fact]
    public async Task AddMovementTypesAndReasonCode_DownThenUpAgain_BackfillsIdentically()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var rowsBefore = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockMovements" """);
        await using (var db = database.CreateDbContext()) await db.MigrateToHeadAsync();
        var firstHash = await ScalarAsync<string>(database, RowHashSql);

        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(Previous);
            await db.MigrateToHeadAsync();
        }

        await AssertBackfilledAsync(database);
        await AssertVBlockEmptyAsync(database, rowsBefore);
        Assert.Equal(firstHash, await ScalarAsync<string>(database, RowHashSql));
    }

    /// <summary>Production path (D10): the idempotent script over a plain connection (EF wraps each statement in DO $EF$ ... END $EF$,
    /// the migration has nested DO blocks and temp tables); the second run changes nothing, row by row.</summary>
    [Fact]
    public async Task AddMovementTypesAndReasonCode_IdempotentScript_BackfillsAndSecondRunIsNoOp()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var rowsBefore = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "StockMovements" """);
        var script = await ScriptAsync(database);
        Assert.Contains("DO $EF$", script);
        Assert.Contains(M8, script);

        await ExecuteAsync(database, script);

        await using (var db = database.CreateDbContext())
        {
            Assert.Contains(M8, await db.Database.GetAppliedMigrationsAsync());
            // The script targets this migration: exactly the migrations after it (the multi-company ones) remain pending.
            Assert.Equal(db.Database.GetMigrations().Where(m => string.CompareOrdinal(m, M8) > 0), await db.Database.GetPendingMigrationsAsync());
        }
        await AssertBackfilledAsync(database);
        await AssertVBlockEmptyAsync(database, rowsBefore);
        var hash = await ScalarAsync<string>(database, RowHashSql);
        var history = await QueryAsync(database, """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1""", r => r.GetString(0));
        var schema = await SchemaSignatureAsync(database);

        await ExecuteAsync(database, script); // second run: every block is skipped

        Assert.Equal(hash, await ScalarAsync<string>(database, RowHashSql));
        Assert.Equal(history, await QueryAsync(database, """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1""", r => r.GetString(0)));
        Assert.Equal(schema, await SchemaSignatureAsync(database));
    }

    [Fact]
    public async Task AddMovementTypesAndReasonCode_IdempotentScriptWithAdjustmentWithoutAdjustment_RaisesThroughWrapperAndLeavesSchemaUnchanged()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, Insert(300, "Adjustment", "1", "StockAdjustment", 999, "BAD-1"));
        var script = await ScriptAsync(database);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, script));

        Assert.Contains("run P1", ex.MessageText);
        Assert.DoesNotContain("ReasonCode", await ColumnsAsync(database, "StockMovements"));
        await using var check = database.CreateDbContext();
        Assert.DoesNotContain(M8, await check.Database.GetAppliedMigrationsAsync());
    }

    private static async Task<string> ScriptAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        return db.GetService<IMigrator>().GenerateScript(Previous, M8, MigrationsSqlGenerationOptions.Idempotent);
    }

    /// <summary>Columns, constraints and indexes of StockMovements, one string per item.</summary>
    private static async Task<HashSet<string>> SchemaSignatureAsync(TestDatabase database)
    {
        var items = new HashSet<string>();
        foreach (var sql in new[]
        {
            """
            SELECT 'col ' || column_name || ' ' || data_type || COALESCE('(' || numeric_precision || ',' || numeric_scale || ')', '')
                   || COALESCE(' len ' || character_maximum_length, '') || ' null=' || is_nullable || ' default=' || COALESCE(column_default, '-')
            FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'StockMovements'
            """,
            """
            SELECT 'con ' || c.conname || ' ' || c.contype::text || ' del=' || c.confdeltype::text || ' ' || pg_get_constraintdef(c.oid)
            FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid WHERE t.relname = 'StockMovements'
            """,
            """SELECT 'idx ' || indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'StockMovements'""",
        })
        {
            foreach (var item in await QueryAsync(database, sql, r => r.GetString(0))) items.Add(item);
        }
        return items;
    }

    private static async Task ExecuteAsync(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<T>> QueryAsync<T>(TestDatabase database, string sql, Func<NpgsqlDataReader, T> map)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(map(reader));
        return rows;
    }

    private static async Task<HashSet<string>> ColumnsAsync(TestDatabase database, string table) =>
        (await QueryAsync(database, $"SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}'",
            r => r.GetString(0))).ToHashSet();
}
