using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.TestData;

/// <summary>
/// Shared support for the four multi-company (Phase 1) migration tests: a legacy dataset written into the pre-multi-company
/// schema (<see cref="Legacy"/>, the schema of <c>AddMovementTypesAndReasonCode</c>), whole-schema signatures, and small
/// SQL helpers. Tests for later migrations carry the legacy rows forward through the earlier ones with the real migrator.
/// </summary>
public static class CompanyMigrations
{
    /// <summary>Schema the legacy rows are written into (their ledger labels are the pre-6a ones); <see cref="Legacy"/> follows via the real migration.</summary>
    public const string WrittenAt = "20261106085008_AddSalesUnitSnapshots";
    public const string Legacy = "20261109053202_AddMovementTypesAndReasonCode";
    public const string M1 = "20261109085553_AddCompaniesAndMemberships";
    public const string M2 = "20261109085651_AddCompanyIdToCompanyTables";
    public const string M3 = "20261109085822_ScopeUniquesAndCompositeKeysToCompany";
    public const string M4 = "20261109085910_AddDocumentSequences";
    // Phase 2 (branches)
    public const string B1 = "20261109105800_AddBranches";
    public const string B2 = "20261109105814_AddBranchIdToDocuments";
    public const string B3 = "20261109105822_ScopeDocumentSequencesToBranches";
    // Phase 3 (stock locations)
    public const string B4 = "20261110015257_AddStockLocations";
    // Phase 4 (warehouses and vehicles)
    public const string B5 = "20261110043527_AddWarehousesAndVehicles";
    // Phase 5 (user branch allocation)
    public const string B6 = "20261110054716_AddUserBranchAssignments";
    // Phase 6 (platform administrators and company governance)
    public const string B7 = "20261110080545_AddPlatformAdminAndGovernance";
    // Phase 7 (scoped RBAC)
    public const string B8 = "20261110093408_AddScopedRoles";
    // Phase 8a (stock location dimension)
    public const string B9 = "20261110114731_AddStockLocationDimension";
    public const string DefaultStore = "0c0a1f00-0000-4000-8000-000000000003";
    public const string DefaultTransit = "0c0a1f00-0000-4000-8000-000000000004";
    public const string OtherTransitSql = $"md5('location:transit:' || '{OtherCompany}')::uuid";
    public const string DefaultBranch = "0c0a1f00-0000-4000-8000-000000000002";
    public const string OtherMainSql = $"md5('branch:' || '{OtherCompany}')::uuid";
    public const string DefaultCompany = "0c0a1f00-0000-4000-8000-000000000001";
    public const string OtherCompany = "0c0a1f00-0000-4000-8000-0000000000b2";

    /// <summary>The 21 company-owned tables carrying <c>CompanyId</c> after M2 (BusinessSettings gets it in M1; DocumentSequences in M4).</summary>
    public static readonly string[] CompanyTables =
    [
        "Taxes", "ExchangeRates", "Suppliers", "Customers", "ExpenseCategories", "Expenses", "FinancialAccounts", "FinancialTransactions",
        "Products", "ProductUnits", "PurchaseOrders", "PurchaseReturns", "PurchaseBatches", "SalesInvoices", "SalesReturns",
        "StockAdjustments", "StockItems", "StockMovements", "IdempotencyKeys", "BusinessSettings",
    ];

    public const string FixtureSql = """
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

-- Multi-company additions: business profile, expense/account masters, document-number variety for the M4 sequence seeding
-- (gap PUR-0007, non-numeric LEGACY-ABC, two invoice prefixes SAL-/INV-, expenses with a gap).
INSERT INTO "BusinessSettings" ("Id", "BusinessName", "InvoicePrefix") VALUES (pg_temp.g(400), 'Acme Retail', 'SAL');
INSERT INTO "ExpenseCategories" ("Id", "Name", "IsActive") VALUES (pg_temp.g(401), 'Rent', true);
INSERT INTO "FinancialAccounts" ("Id", "Name", "Kind", "Reference", "IsActive", "CreatedByUserId", "CreatedAt") VALUES (pg_temp.g(402), 'Till', 'Cash', NULL, true, pg_temp.g(12), now());
INSERT INTO "Expenses" ("Id", "ExpenseNo", "CategoryId", "ExpenseDate", "CurrencyId", "Amount", "ExchangeRateToBase", "BaseAmount", "PaymentMethod", "Status", "CreatedByUserId", "CreatedAt") VALUES
  (pg_temp.g(403), 'EXP-0001', pg_temp.g(401), DATE '2026-09-10', '7c1a5b9e-3d24-4f8a-9e61-2b0f4a8d5c01', 100, 1, 100, 'Cash', 'Active', pg_temp.g(12), now()),
  (pg_temp.g(404), 'EXP-0003', pg_temp.g(401), DATE '2026-09-11', '7c1a5b9e-3d24-4f8a-9e61-2b0f4a8d5c01', 50, 1, 50, 'Cash', 'Active', pg_temp.g(12), now());
INSERT INTO "PurchaseOrders" ("Id", "CreatedAt", "CreatedByUserId", "DiscountTotal", "GrandTotal", "OtherChargesTotal", "PaidAmount", "PaymentMethod", "PurchaseDate", "PurchaseNo", "Status", "Subtotal", "SupplierId", "TaxTotal") VALUES
  (pg_temp.g(405), now(), pg_temp.g(12), 0, 0, 0, 0, 'Cash', DATE '2026-09-12', 'PUR-0007', 'Cancelled', 0, pg_temp.g(14), 0),
  (pg_temp.g(406), now(), pg_temp.g(12), 0, 0, 0, 0, 'Cash', DATE '2026-09-12', 'LEGACY-ABC', 'Cancelled', 0, pg_temp.g(14), 0);
INSERT INTO "SalesInvoices" ("Id", "InvoiceNo", "CustomerId", "InvoiceDate", "PaymentMethod", "PaidAmount", "Subtotal", "TaxTotal", "DiscountTotal", "GrandTotal", "Status", "CreatedByUserId", "CreatedAt") VALUES
  (pg_temp.g(407), 'INV-0010', pg_temp.g(13), DATE '2026-09-13', 'Cash', 0, 0, 0, 0, 0, 'Cancelled', pg_temp.g(12), now());
""";

    /// <summary>
    /// A second company with a supplier, a purchase, an expense category, an expense, an account and its own counters, written at the
    /// Phase 1 head (M4) so the Phase 2 migrations must give it its own Main branch and keep its numbers apart from the default company's.
    /// </summary>
    public const string SecondCompanyAtM4Sql = $"""
        INSERT INTO "Companies" ("Id", "Code", "Name", "IsActive", "CreatedAt") VALUES ('{OtherCompany}', 'B', 'Company B', true, now());
        INSERT INTO "Suppliers" ("Id", "Name", "IsActive", "CompanyId") VALUES ('00000000-0000-0000-0000-0000000b0014', 'Sup B', true, '{OtherCompany}');
        INSERT INTO "PurchaseOrders" ("Id", "CreatedAt", "CreatedByUserId", "DiscountTotal", "GrandTotal", "OtherChargesTotal", "PaidAmount", "PaymentMethod", "PurchaseDate", "PurchaseNo", "Status", "Subtotal", "SupplierId", "TaxTotal", "CompanyId")
        VALUES ('00000000-0000-0000-0000-0000000b0040', now(), '00000000-0000-0000-0000-00000000000c', 0, 0, 0, 0, 'Cash', DATE '2026-10-01', 'PUR-0042', 'Cancelled', 0, '00000000-0000-0000-0000-0000000b0014', 0, '{OtherCompany}');
        INSERT INTO "ExpenseCategories" ("Id", "Name", "IsActive", "CompanyId") VALUES ('00000000-0000-0000-0000-0000000b0401', 'Rent B', true, '{OtherCompany}');
        INSERT INTO "Expenses" ("Id", "ExpenseNo", "CategoryId", "ExpenseDate", "CurrencyId", "Amount", "ExchangeRateToBase", "BaseAmount", "PaymentMethod", "Status", "CreatedByUserId", "CreatedAt", "CompanyId")
        VALUES ('00000000-0000-0000-0000-0000000b0403', 'EXP-0002', '00000000-0000-0000-0000-0000000b0401', DATE '2026-10-02', '7c1a5b9e-3d24-4f8a-9e61-2b0f4a8d5c01', 10, 1, 10, 'Cash', 'Active', '00000000-0000-0000-0000-00000000000c', now(), '{OtherCompany}');
        INSERT INTO "FinancialAccounts" ("Id", "Name", "Kind", "Reference", "IsActive", "CreatedByUserId", "CreatedAt", "CompanyId")
        VALUES ('00000000-0000-0000-0000-0000000b0402', 'Till B', 'Cash', NULL, true, '00000000-0000-0000-0000-00000000000c', now(), '{OtherCompany}');
        INSERT INTO "DocumentSequences" ("Id", "CompanyId", "BranchId", "Series", "NextValue", "UpdatedAt") VALUES
          (gen_random_uuid(), '{OtherCompany}', NULL, 'PUR', 43, now()), (gen_random_uuid(), '{OtherCompany}', NULL, 'INV', 7, now()),
          (gen_random_uuid(), '{OtherCompany}', NULL, 'PRT', 1, now()), (gen_random_uuid(), '{OtherCompany}', NULL, 'SRT', 1, now()),
          (gen_random_uuid(), '{OtherCompany}', NULL, 'ADJ', 1, now()), (gen_random_uuid(), '{OtherCompany}', NULL, 'EXP', 3, now());
        """;

    /// <summary>Legacy data plus <see cref="SecondCompanyAtM4Sql"/> (written at M4), then carried forward to <paramref name="previous"/> by the real migrations.</summary>
    public static async Task<TestDatabase> CreateWithSecondCompanyAtAsync(PostgresFixture postgres, string previous)
    {
        var database = await CreateAtAsync(postgres, M4);
        await ExecuteAsync(database, SecondCompanyAtM4Sql);
        if (previous != M4) await MigrateToAsync(database, previous);
        return database;
    }

    /// <summary>Headers that must carry a BranchId after B2.</summary>
    public static readonly string[] BranchHeaders = ["PurchaseOrders", "PurchaseReturns", "SalesInvoices", "SalesReturns", "StockAdjustments"];

    /// <summary>Tables carrying <c>StockLocationId</c> after B9 (Phase 8a); PurchaseBatches carries <c>OriginStockLocationId</c> instead.</summary>
    public static readonly string[] StockLocationTables = ["StockItems", "StockMovements", "PurchaseOrders", "PurchaseReturns", "SalesInvoices", "SalesReturns", "StockAdjustments"];

    /// <summary>Optional-branch tables (NULL = company level).</summary>
    public static readonly string[] OptionalBranchTables = ["Expenses", "FinancialAccounts"];

    /// <summary>A database migrated to <paramref name="previous"/> (default: the pre-multi-company schema) holding <see cref="FixtureSql"/>
    /// (written at the legacy schema, then carried forward by the real migrations up to <paramref name="previous"/>).</summary>
    public static async Task<TestDatabase> CreateAtAsync(PostgresFixture postgres, string? previous = null, string? extraLegacySql = null)
    {
        var database = await postgres.CreateDatabaseAsync(migrate: false);
        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(WrittenAt);
        }
        await ExecuteAsync(database, FixtureSql + (extraLegacySql ?? ""));
        await MigrateToAsync(database, Legacy);
        if (previous is not null && previous != Legacy)
        {
            await using var db = database.CreateDbContext();
            await db.GetService<IMigrator>().MigrateAsync(previous);
        }
        return database;
    }

    /// <summary>
    /// For fixtures that seed a head-schema database with raw SQL written before multi-company: gives every company table a
    /// DEFAULT of the default company so those INSERTs keep their exact column lists. Test databases only (the production
    /// schema has no default: every writer must state its company).
    /// </summary>
    public static async Task DefaultRawInsertsToDefaultCompanyAsync(TestDatabase database)
    {
        var sql = string.Concat(CompanyTables.Select(t => $"ALTER TABLE \"{t}\" ALTER COLUMN \"CompanyId\" SET DEFAULT '{DefaultCompany}';\n"))
                  // Phase 2: raw header INSERTs written before branches land on the default company's Main.
                  + string.Concat(BranchHeaders.Select(t => $"ALTER TABLE \"{t}\" ALTER COLUMN \"BranchId\" SET DEFAULT '{DefaultBranch}';\n"))
                  // Phase 8a: raw stock rows / headers written before the location dimension land on the default company's Main store.
                  + string.Concat(StockLocationTables.Select(t => $"ALTER TABLE \"{t}\" ALTER COLUMN \"StockLocationId\" SET DEFAULT '{DefaultStore}';\n"))
                  + $"ALTER TABLE \"PurchaseBatches\" ALTER COLUMN \"OriginStockLocationId\" SET DEFAULT '{DefaultStore}';\n";
        await ExecuteAsync(database, sql);
    }

    public static async Task MigrateToAsync(TestDatabase database, string? target = null)
    {
        await using var db = database.CreateDbContext();
        if (target is null) await db.Database.MigrateAsync();
        else await db.GetService<IMigrator>().MigrateAsync(target);
    }

    public static async Task<string> ScriptAsync(TestDatabase database, string from, string to)
    {
        await using var db = database.CreateDbContext();
        return db.GetService<IMigrator>().GenerateScript(from, to, MigrationsSqlGenerationOptions.Idempotent);
    }

    /// <summary>Row counts of every table that exists, used to prove a migration neither loses nor adds legacy rows.</summary>
    public static async Task<List<string>> RowCountsAsync(TestDatabase database, params string[] tables)
    {
        var rows = new List<string>();
        foreach (var table in tables)
        {
            rows.Add(table + "=" + await ScalarAsync<long>(database, $"SELECT COUNT(*) FROM \"{table}\""));
        }
        return rows;
    }

    public static readonly string[] CountedTables =
    [
        "Users", "BusinessSettings", "Taxes", "Suppliers", "Customers", "ExpenseCategories", "Expenses", "FinancialAccounts", "Products",
        "ProductUnits", "PurchaseOrders", "PurchaseOrderLines", "PurchaseReturns", "PurchaseBatches", "SalesInvoices", "SalesInvoiceLines",
        "SalesReturns", "StockAdjustments", "StockItems", "StockMovements",
    ];

    /// <summary>Every column, constraint and index of every public table (except migration history): one string each.</summary>
    public static async Task<HashSet<string>> SchemaSignatureAsync(TestDatabase database)
    {
        var items = new HashSet<string>();
        foreach (var sql in new[]
        {
            """
            SELECT 'col ' || table_name || '.' || column_name || ' ' || data_type || COALESCE('(' || numeric_precision || ',' || numeric_scale || ')', '')
                   || COALESCE(' len ' || character_maximum_length, '') || ' null=' || is_nullable || ' default=' || COALESCE(column_default, '-')
            FROM information_schema.columns WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
            """,
            """
            SELECT 'con ' || t.relname || '.' || c.conname || ' ' || c.contype::text || ' del=' || c.confdeltype::text || ' ' || pg_get_constraintdef(c.oid)
            FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = 'public' AND t.relname <> '__EFMigrationsHistory'
            """,
            """SELECT 'idx ' || tablename || '.' || indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory'""",
        })
        {
            foreach (var item in await QueryAsync(database, sql, r => r.GetString(0))) items.Add(item);
        }
        return items;
    }

    /// <summary>The schema a freshly created database has at <paramref name="migration"/> (reference for Down checks).</summary>
    public static async Task<HashSet<string>> ReferenceSchemaAsync(PostgresFixture postgres, string migration)
    {
        await using var reference = await postgres.CreateDatabaseAsync(migrate: false);
        await MigrateToAsync(reference, migration);
        return await SchemaSignatureAsync(reference);
    }

    public static async Task<List<string>> AppliedAsync(TestDatabase database) =>
        await QueryAsync(database, """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1""", r => r.GetString(0));

    public static async Task ExecuteAsync(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<List<T>> QueryAsync<T>(TestDatabase database, string sql, Func<NpgsqlDataReader, T> map)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(map(reader));
        return rows;
    }

    /// <summary>Runs <paramref name="action"/> and asserts it raises a PostgreSQL exception whose message contains <paramref name="expected"/>.</summary>
    public static async Task<PostgresException> AssertRaisesAsync(Func<Task> action, string expected)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Contains(expected, ex.MessageText);
        return ex;
    }

    /// <summary>The first (migration-wide) test of the V-block shared by M2-M4: every company table is stamped with the default company.</summary>
    public static async Task AssertAllRowsOnDefaultCompanyAsync(TestDatabase database)
    {
        foreach (var table in CompanyTables)
        {
            var bad = await ScalarAsync<long>(database, $"SELECT COUNT(*) FROM \"{table}\" WHERE \"CompanyId\" IS NULL OR \"CompanyId\" <> '{DefaultCompany}'");
            Assert.True(bad == 0, $"{table}: {bad} row(s) not on the default company (V2).");
        }
    }
}
