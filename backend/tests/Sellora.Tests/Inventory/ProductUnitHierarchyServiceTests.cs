using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;
using V = Sellora.Domain.Inventory.ProductUnitConfigurationValidator;

namespace Sellora.Tests.Inventory;

/// <summary>
/// <see cref="ProductService"/> unit-hierarchy flows (Phase 2 design §5.2–§5.3) on EF InMemory: create, upsert with
/// preserved ids, immutability of rows referenced by lines or prices (and everything they contain), deactivate-and-
/// replace, omitted-row handling, base-unit change rules, barcodes, and preview/save parity. Row locks and partial
/// unique indexes are exercised against PostgreSQL in Sellora.IntegrationTests.
/// </summary>
public class ProductUnitHierarchyServiceTests
{
    private sealed record Fixture(
        SelloraDbContext Db, ProductService Service, Tax Tax, User User, Supplier Supplier, Customer Customer,
        Unit Pc, Unit Box, Unit HCtn, Unit FCtn, Unit Kg, Unit Gram, Unit Bag);

    private static async Task<Fixture> SeedAsync()
    {
        var db = TestDb.NewInMemory();
        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin",
            Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 12%", RatePercent = 12m };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var (pc, box, hctn, fctn, kg, g, bag) = (TestUnits.Piece(), TestUnits.Box(), TestUnits.HalfCarton(), TestUnits.FullCarton(),
            TestUnits.Kilogram(), TestUnits.Gram(), TestUnits.Bag());
        db.AddRange(role, user, tax, inr, supplier, customer, pc, box, hctn, fctn, kg, g, bag);
        await db.SaveChangesAsync();
        return new Fixture(db, new ProductService(db), tax, user, supplier, customer, pc, box, hctn, fctn, kg, g, bag);
    }

    private static ProductUnitInputItem Row(Guid unitId, Guid? innerUnitId, decimal? qty, Guid? id = null, string? label = null, string? barcode = null,
        bool active = true, bool sellable = true, bool purchasable = true, bool defaultSales = false, bool defaultPurchase = false) =>
        new(id, unitId, innerUnitId, qty, label, barcode, purchasable, sellable, defaultPurchase, defaultSales, active);

    private static UpsertProductInput Input(Fixture f, string sku, Guid baseUnitId, params ProductUnitInputItem[] units) =>
        new(sku, "Ball Pen", "Stationery", baseUnitId, f.Tax.Id, 200m, units);

    private static ProductUnitInputItem[] PenRows(Fixture f) =>
    [
        Row(f.Pc.Id, null, 1m),
        Row(f.Box.Id, f.Pc.Id, 10m),
        Row(f.HCtn.Id, f.Box.Id, 5m),
        Row(f.FCtn.Id, f.HCtn.Id, 2m),
    ];

    private static Task<Product> CreatePenAsync(Fixture f, string sku = "STN-PEN") =>
        f.Service.CreateProductAsync(Input(f, sku, f.Pc.Id, PenRows(f)));

    private static async Task<List<ProductUnit>> StoredAsync(Fixture f, Guid productId) =>
        await f.Db.ProductUnits.AsNoTracking().Where(u => u.ProductId == productId).OrderBy(u => u.SortOrder).ToListAsync();

    /// <summary>The stored rows as the editor would resubmit them (ids kept, inner named by its UnitId), keyed by unit.</summary>
    private static async Task<Dictionary<Guid, ProductUnitInputItem>> CurrentAsync(Fixture f, Guid productId, bool activeOnly = true)
    {
        var rows = await StoredAsync(f, productId);
        var byId = rows.ToDictionary(r => r.Id);
        return rows.Where(r => !activeOnly || r.IsActive).ToDictionary(r => r.UnitId, r => new ProductUnitInputItem(
            r.Id, r.UnitId, r.InnerProductUnitId is { } i ? byId[i].UnitId : null, r.InnerQuantity, r.Label, r.Barcode,
            r.IsPurchasable, r.IsSellable, r.IsDefaultPurchaseUnit, r.IsDefaultSalesUnit, r.IsActive));
    }

    private static Task<Product?> SaveAsync(Fixture f, Product product, params ProductUnitInputItem[] units) =>
        f.Service.UpdateProductAsync(product.Id, Input(f, product.Sku, product.BaseUnitId, units));

    private static async Task<ProductUnit> UnitRowAsync(Fixture f, Guid productId, Guid unitId) =>
        await f.Db.ProductUnits.AsNoTracking().SingleAsync(u => u.ProductId == productId && u.UnitId == unitId && u.IsActive);

    public enum LockKind { PurchaseLine, SaleLine, Price }

    private static async Task LockAsync(Fixture f, ProductUnit row, LockKind kind)
    {
        switch (kind)
        {
            case LockKind.PurchaseLine:
                var purchase = new PurchaseOrder
                {
                    Id = Guid.NewGuid(), PurchaseNo = "PUR-" + Guid.NewGuid().ToString("N")[..6], SupplierId = f.Supplier.Id, CurrencyId = f.Supplier.CurrencyId,
                    PurchaseDate = new DateOnly(2026, 9, 1), PaymentMethod = PaymentMethod.Cash, CreatedByUserId = f.User.Id,
                };
                purchase.Lines.Add(new PurchaseOrderLine
                {
                    Id = Guid.NewGuid(), PurchaseOrderId = purchase.Id, ProductId = row.ProductId, ProductUnitId = row.Id,
                    Qty = 1, ConversionFactorToBase = row.ConversionFactorToBase, BaseQty = row.ConversionFactorToBase, Rate = 10,
                    TaxId = f.Tax.Id, TaxRatePercent = 12, TaxableValue = 10, TaxAmount = 1.2m, LineTotal = 11.2m, UnitCost = 10 / row.ConversionFactorToBase,
                });
                f.Db.Add(purchase);
                break;
            case LockKind.SaleLine:
                var sale = new SalesInvoice
                {
                    Id = Guid.NewGuid(), InvoiceNo = "INV-" + Guid.NewGuid().ToString("N")[..6], CustomerId = f.Customer.Id,
                    InvoiceDate = new DateOnly(2026, 9, 18), PaymentMethod = PaymentMethod.Cash, CreatedByUserId = f.User.Id,
                };
                sale.Lines.Add(new SalesInvoiceLine
                {
                    Id = Guid.NewGuid(), SalesInvoiceId = sale.Id, ProductId = row.ProductId, ProductUnitId = row.Id,
                    Qty = 1, ConversionFactorToBase = row.ConversionFactorToBase, BaseQty = row.ConversionFactorToBase, UnitPrice = 20,
                    TaxId = f.Tax.Id, TaxRatePercent = 12, TaxableValue = 20, TaxAmount = 2.4m, LineTotal = 22.4m,
                });
                f.Db.Add(sale);
                break;
            default:
                f.Db.SellingPrices.Add(new SellingPrice { Id = Guid.NewGuid(), ProductUnitId = row.Id, Price = 99m, CreatedByUserId = f.User.Id });
                break;
        }
        await f.Db.SaveChangesAsync();
    }

    private static async Task<DomainValidationException> AssertRejectedAsync(Func<Task> act, string field, string? message = null)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(act);
        Assert.True(ex.Errors.Any(e => e.Field == field && (message is null || e.Message == message)),
            $"Expected {field}{(message is null ? "" : $" '{message}'")}. Errors: " + string.Join(" | ", ex.Errors.Select(e => $"{e.Field}: {e.Message}")));
        return ex;
    }

    // ------------------------------------------------------------------ create

    [Fact]
    public async Task CreateProductAsync_Pen_StoresChainedHierarchyWithFactors1_10_50_100()
    {
        var f = await SeedAsync();

        var pen = await CreatePenAsync(f);

        var rows = await StoredAsync(f, pen.Id);
        Assert.Equal(new[] { f.Pc.Id, f.Box.Id, f.HCtn.Id, f.FCtn.Id }, rows.Select(r => r.UnitId));
        Assert.Equal(new[] { 1m, 10m, 50m, 100m }, rows.Select(r => r.ConversionFactorToBase));
        Assert.Equal(new short[] { 0, 1, 2, 3 }, rows.Select(r => r.SortOrder));
        Assert.Equal(new Guid?[] { null, rows[0].Id, rows[1].Id, rows[2].Id }, rows.Select(r => r.InnerProductUnitId));
        Assert.Equal(new[] { true, false, false, false }, rows.Select(r => r.IsBaseUnit));
        Assert.All(rows, r => Assert.True(r.IsActive));
        // The returned entity is fully loaded (units with their Unit) for the controller.
        Assert.Equal(4, pen.Units.Count);
        Assert.All(pen.Units, u => Assert.NotNull(u.Unit));
    }

    [Fact]
    public async Task CreateProductAsync_PenSubmittedTopDown_SameHierarchy()
    {
        var f = await SeedAsync();

        var pen = await f.Service.CreateProductAsync(Input(f, "STN-PEN", f.Pc.Id, PenRows(f).Reverse().ToArray()));

        Assert.Equal(new[] { 1m, 10m, 50m, 100m }, (await StoredAsync(f, pen.Id)).Select(r => r.ConversionFactorToBase));
    }

    [Fact]
    public async Task CreateProductAsync_SeveralRuleViolations_RaisesAllErrorsTogetherAndSavesNothing()
    {
        var f = await SeedAsync();

        var ex = await AssertRejectedAsync(() => f.Service.CreateProductAsync(Input(f, "STN-PEN", f.Pc.Id,
            Row(f.Pc.Id, null, 1m, barcode: "89 01"),
            Row(f.Box.Id, f.HCtn.Id, 2m),
            Row(f.HCtn.Id, f.Box.Id, 5m))), "Units[0].Barcode", "Barcode cannot contain spaces.");

        Assert.Contains(ex.Errors, e => e.Field == "Units[1].InnerUnitId");
        Assert.Contains(ex.Errors, e => e.Field == "Units[2].InnerUnitId");
        Assert.Equal(ex.Errors[0].Field, ex.Field);
        Assert.False(await f.Db.Products.AnyAsync());
        Assert.False(await f.Db.ProductUnits.AnyAsync());
    }

    [Fact]
    public async Task CreateProductAsync_RiceKgWithGramAndBag_StoresFractionalFactor()
    {
        var f = await SeedAsync();

        var rice = await f.Service.CreateProductAsync(Input(f, "RICE", f.Kg.Id,
            Row(f.Kg.Id, null, null, label: "Loose kg"),
            Row(f.Gram.Id, f.Kg.Id, 0.001m, sellable: false, purchasable: false),
            Row(f.Bag.Id, f.Kg.Id, 25m, label: "25 kg bag", defaultPurchase: true)));

        var rows = await StoredAsync(f, rice.Id);
        Assert.Equal(new[] { 1m, 0.001m, 25m }, rows.Select(r => r.ConversionFactorToBase));
        Assert.Equal(1m, rows[0].InnerQuantity); // base quantity null is stored as 1
        Assert.True(rows[2].IsDefaultPurchaseUnit);
    }

    // ------------------------------------------------------------------ update: identity

    [Fact]
    public async Task UpdateProductAsync_ResubmittedUnchanged_PreservesIdsAndFactors()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var before = await StoredAsync(f, pen.Id);
        var current = await CurrentAsync(f, pen.Id);

        await SaveAsync(f, pen, current.Values.Reverse().ToArray());

        var after = await StoredAsync(f, pen.Id);
        Assert.Equal(before.Select(r => (r.Id, r.UnitId, r.InnerProductUnitId, r.ConversionFactorToBase, r.SortOrder)),
            after.Select(r => (r.Id, r.UnitId, r.InnerProductUnitId, r.ConversionFactorToBase, r.SortOrder)));
    }

    [Fact]
    public async Task UpdateProductAsync_BaseRowWithoutId_MapsOntoStoredBaseRow()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var stored = await StoredAsync(f, pen.Id);
        var current = await CurrentAsync(f, pen.Id);

        await SaveAsync(f, pen, current[f.Pc.Id] with { Id = null, Label = "Single pen" }, current[f.Box.Id], current[f.HCtn.Id], current[f.FCtn.Id]);

        var after = await StoredAsync(f, pen.Id);
        Assert.Equal(stored.Select(r => r.Id).Order(), after.Select(r => r.Id).Order());
        Assert.Equal("Single pen", after.Single(r => r.IsBaseUnit).Label);
    }

    [Fact]
    public async Task UpdateProductAsync_UnlockedBoxQuantityChanged_RecomputesFactorsUpTheChain()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var c = await CurrentAsync(f, pen.Id);

        await SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id] with { InnerQuantity = 12m }, c[f.HCtn.Id], c[f.FCtn.Id]);

        var rows = await StoredAsync(f, pen.Id);
        Assert.Equal(new[] { 1m, 12m, 60m, 120m }, rows.Select(r => r.ConversionFactorToBase));
        Assert.Equal(c[f.Box.Id].Id, rows[1].Id);
    }

    [Fact]
    public async Task UpdateProductAsync_SameRowIdTwice_RejectedOnUnits()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var c = await CurrentAsync(f, pen.Id);

        await AssertRejectedAsync(() => SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id], c[f.Box.Id] with { UnitId = f.Bag.Id }, c[f.HCtn.Id], c[f.FCtn.Id]),
            "Units", "The same unit row was submitted twice.");
    }

    [Fact]
    public async Task UpdateProductAsync_RowIdOfAnotherProduct_RejectedOnUnits()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var other = await CreatePenAsync(f, "STN-PEN-2");
        var c = await CurrentAsync(f, pen.Id);
        var foreignBox = (await CurrentAsync(f, other.Id))[f.Box.Id];

        await AssertRejectedAsync(() => SaveAsync(f, pen, c[f.Pc.Id], foreignBox, c[f.HCtn.Id], c[f.FCtn.Id]),
            "Units", "One of the units no longer exists on this product. Reload and try again.");
    }

    // ------------------------------------------------------------------ immutability (R8) and lock closure

    [Theory]
    [InlineData(LockKind.PurchaseLine, ProductUnitUsage.Transactions)]
    [InlineData(LockKind.SaleLine, ProductUnitUsage.Transactions)]
    [InlineData(LockKind.Price, ProductUnitUsage.Prices)]
    public async Task UpdateProductAsync_LockedBoxQuantityChanged_RejectedAndNothingChanges(LockKind kind, string reason)
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var box = await UnitRowAsync(f, pen.Id, f.Box.Id);
        await LockAsync(f, box, kind);
        var c = await CurrentAsync(f, pen.Id);

        Assert.Equal(reason, (await ProductUnitUsage.GetLockReasonsAsync(f.Db, await StoredAsync(f, pen.Id), default))[box.Id]);
        await AssertRejectedAsync(() => SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id] with { InnerQuantity = 20m }, c[f.HCtn.Id], c[f.FCtn.Id]),
            "Units[1].InnerQuantity", V.LockedMessage);

        f.Db.ChangeTracker.Clear();
        Assert.Equal(new[] { 1m, 10m, 50m, 100m }, (await StoredAsync(f, pen.Id)).Select(r => r.ConversionFactorToBase));
    }

    [Fact]
    public async Task UpdateProductAsync_LockedBoxUnitChanged_RejectedOnUnitId()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        await LockAsync(f, await UnitRowAsync(f, pen.Id, f.Box.Id), LockKind.SaleLine);
        var c = await CurrentAsync(f, pen.Id);

        await AssertRejectedAsync(() => SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id] with { UnitId = f.Bag.Id }, c[f.HCtn.Id] with { InnerUnitId = f.Bag.Id }, c[f.FCtn.Id]),
            "Units[1].UnitId", V.LockedMessage);
    }

    [Fact]
    public async Task UpdateProductAsync_LockedHalfCartonReParented_RejectedOnInnerUnitId()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        await LockAsync(f, await UnitRowAsync(f, pen.Id, f.HCtn.Id), LockKind.PurchaseLine);
        var c = await CurrentAsync(f, pen.Id);

        await AssertRejectedAsync(() => SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id], c[f.HCtn.Id] with { InnerUnitId = f.Pc.Id, InnerQuantity = 50m }, c[f.FCtn.Id]),
            "Units[2].InnerUnitId", V.LockedMessage);
    }

    [Fact]
    public async Task UpdateProductAsync_BoxUnderLockedHalfCarton_IsLockedByClosure()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var hctn = await UnitRowAsync(f, pen.Id, f.HCtn.Id);
        await LockAsync(f, hctn, LockKind.PurchaseLine);
        var c = await CurrentAsync(f, pen.Id);

        var locks = await ProductUnitUsage.GetLockReasonsAsync(f.Db, await StoredAsync(f, pen.Id), default);
        Assert.Equal(ProductUnitUsage.Transactions, locks[hctn.Id]);
        Assert.Equal(ProductUnitUsage.ContainedInLockedUnit, locks[c[f.Box.Id].Id!.Value]);
        Assert.Equal(ProductUnitUsage.ContainedInLockedUnit, locks[c[f.Pc.Id].Id!.Value]);
        Assert.False(locks.ContainsKey(c[f.FCtn.Id].Id!.Value));

        await AssertRejectedAsync(() => SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id] with { InnerQuantity = 12m }, c[f.HCtn.Id], c[f.FCtn.Id]),
            "Units[1].InnerQuantity", V.LockedMessage);
    }

    [Fact]
    public async Task UpdateProductAsync_UnlockedRowAboveLockedOne_MayStillChange()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        await LockAsync(f, await UnitRowAsync(f, pen.Id, f.Box.Id), LockKind.Price);
        var c = await CurrentAsync(f, pen.Id);

        await SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id], c[f.HCtn.Id] with { InnerQuantity = 6m }, c[f.FCtn.Id]);

        Assert.Equal(new[] { 1m, 10m, 60m, 120m }, (await StoredAsync(f, pen.Id)).Select(r => r.ConversionFactorToBase));
    }

    [Fact]
    public async Task GetLockReasonsAsync_RowWithLinesAndPrice_TransactionsWin()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var box = await UnitRowAsync(f, pen.Id, f.Box.Id);
        var fctn = await UnitRowAsync(f, pen.Id, f.FCtn.Id);
        await LockAsync(f, box, LockKind.Price);
        await LockAsync(f, box, LockKind.SaleLine);
        await LockAsync(f, fctn, LockKind.Price);

        var locks = await ProductUnitUsage.GetLockReasonsAsync(f.Db, await StoredAsync(f, pen.Id), default);

        Assert.Equal(ProductUnitUsage.Transactions, locks[box.Id]);  // direct: transactions > prices
        Assert.Equal(ProductUnitUsage.Prices, locks[fctn.Id]);
        Assert.Equal(ProductUnitUsage.ContainedInLockedUnit, locks[(await UnitRowAsync(f, pen.Id, f.HCtn.Id)).Id]); // closure from FCtn
        Assert.Equal(4, locks.Count);
    }

    [Fact]
    public async Task UpdateProductAsync_LockedRowMutableAttributes_Saved()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        await LockAsync(f, await UnitRowAsync(f, pen.Id, f.Box.Id), LockKind.SaleLine);
        var c = await CurrentAsync(f, pen.Id);

        await SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id] with { Label = "Box of 10", Barcode = "8901000000010", IsSellable = false, IsDefaultPurchaseUnit = true },
            c[f.HCtn.Id], c[f.FCtn.Id]);

        var box = await f.Db.ProductUnits.AsNoTracking().SingleAsync(u => u.Id == c[f.Box.Id].Id);
        Assert.Equal(("Box of 10", "8901000000010", false, true, 10m), (box.Label, box.Barcode, box.IsSellable, box.IsDefaultPurchaseUnit, box.ConversionFactorToBase));
    }

    // ------------------------------------------------------------------ deactivate and replace

    [Fact]
    public async Task UpdateProductAsync_LockedBox10ReplacedByBox20_OldRowInactiveNewRowActiveAndParentsRePointed()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var oldBox = await UnitRowAsync(f, pen.Id, f.Box.Id);
        await LockAsync(f, oldBox, LockKind.Price);
        var c = await CurrentAsync(f, pen.Id);

        await SaveAsync(f, pen,
            c[f.Pc.Id],
            c[f.Box.Id] with { IsActive = false },
            Row(f.Box.Id, f.Pc.Id, 20m, label: "Box of 20"),
            c[f.HCtn.Id],
            c[f.FCtn.Id]);

        var boxes = await f.Db.ProductUnits.AsNoTracking().Where(u => u.ProductId == pen.Id && u.UnitId == f.Box.Id).ToListAsync();
        Assert.Equal(2, boxes.Count);
        var old = Assert.Single(boxes, b => b.Id == oldBox.Id);
        var replacement = Assert.Single(boxes, b => b.Id != oldBox.Id);
        Assert.Equal((false, 10m), (old.IsActive, old.ConversionFactorToBase));
        Assert.Equal((true, 20m, "Box of 20"), (replacement.IsActive, replacement.ConversionFactorToBase, replacement.Label));
        var hctn = await UnitRowAsync(f, pen.Id, f.HCtn.Id);
        Assert.Equal((replacement.Id, 100m), (hctn.InnerProductUnitId, hctn.ConversionFactorToBase));
        Assert.Equal(200m, (await UnitRowAsync(f, pen.Id, f.FCtn.Id)).ConversionFactorToBase);
        // The price stays on the 10-pack it was set for.
        Assert.Equal(oldBox.Id, (await f.Db.SellingPrices.SingleAsync()).ProductUnitId);
    }

    [Fact]
    public async Task UpdateProductAsync_ReplacementWithoutDeactivatingOld_RejectedAsDuplicateActiveUnit()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        await LockAsync(f, await UnitRowAsync(f, pen.Id, f.Box.Id), LockKind.Price);
        var c = await CurrentAsync(f, pen.Id);

        await AssertRejectedAsync(() => SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id], Row(f.Box.Id, f.Pc.Id, 20m), c[f.HCtn.Id], c[f.FCtn.Id]),
            "Units[2].UnitId", "Box is already used by another active row of this product.");
    }

    // ------------------------------------------------------------------ omitted rows

    [Fact]
    public async Task UpdateProductAsync_OmittedLockedRow_KeptInactive()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var fctn = await UnitRowAsync(f, pen.Id, f.FCtn.Id);
        await LockAsync(f, fctn, LockKind.Price);
        var c = await CurrentAsync(f, pen.Id);

        await SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id], c[f.HCtn.Id]);

        var stored = await f.Db.ProductUnits.AsNoTracking().SingleAsync(u => u.Id == fctn.Id);
        Assert.Equal((false, 100m, (Guid?)c[f.HCtn.Id].Id), (stored.IsActive, stored.ConversionFactorToBase, stored.InnerProductUnitId));
    }

    [Fact]
    public async Task UpdateProductAsync_OmittedLockedRowAndItsUnlockedInner_BothKeptInactive()
    {
        // FCtn is priced, so its contents (HCtn, Box) are locked by closure: omitting all three keeps all three.
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        await LockAsync(f, await UnitRowAsync(f, pen.Id, f.FCtn.Id), LockKind.Price);
        var c = await CurrentAsync(f, pen.Id);

        await SaveAsync(f, pen, c[f.Pc.Id]);

        var rows = await StoredAsync(f, pen.Id);
        Assert.Equal(4, rows.Count);
        Assert.Equal(new[] { true, false, false, false }, rows.Select(r => r.IsActive));
        Assert.Equal(new[] { 1m, 10m, 50m, 100m }, rows.Select(r => r.ConversionFactorToBase));
    }

    [Fact]
    public async Task UpdateProductAsync_OmittedUnlockedRows_Deleted()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var c = await CurrentAsync(f, pen.Id);

        await SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id]);

        var rows = await StoredAsync(f, pen.Id);
        Assert.Equal(new[] { f.Pc.Id, f.Box.Id }, rows.Select(r => r.UnitId));
    }

    [Fact]
    public async Task UpdateProductAsync_OmittedRowStillContainedInKeptUnlockedRow_RejectedOnContainingRow()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var c = await CurrentAsync(f, pen.Id);

        // Box omitted, Half Carton (index 1) still "contains 5 Box".
        await AssertRejectedAsync(() => SaveAsync(f, pen, c[f.Pc.Id], c[f.HCtn.Id], c[f.FCtn.Id]),
            "Units[1].InnerUnitId", "Box is being removed, but this unit still contains it; keep it or choose another contained unit.");

        f.Db.ChangeTracker.Clear();
        Assert.Equal(4, (await StoredAsync(f, pen.Id)).Count);
    }

    [Fact]
    public async Task UpdateProductAsync_OmittedRowContainedInKeptInactiveRow_KeptInactive()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var c = await CurrentAsync(f, pen.Id);

        await SaveAsync(f, pen, c[f.Pc.Id], c[f.HCtn.Id] with { IsActive = false }, c[f.FCtn.Id] with { IsActive = false });

        var rows = await StoredAsync(f, pen.Id);
        Assert.Equal(4, rows.Count);
        Assert.False(rows.Single(r => r.UnitId == f.Box.Id).IsActive);
    }

    // ------------------------------------------------------------------ reactivation

    [Fact]
    public async Task UpdateProductAsync_ReactivatingRowWhoseUnitWasDeactivated_RejectedOnUnitId()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var c = await CurrentAsync(f, pen.Id);
        await SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id], c[f.HCtn.Id], c[f.FCtn.Id] with { IsActive = false });
        // Allowed: only an inactive product unit still uses Full Carton.
        await new UnitService(f.Db).SetActiveAsync(f.FCtn.Id, false);

        await AssertRejectedAsync(() => SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id], c[f.HCtn.Id], c[f.FCtn.Id] with { IsActive = true }),
            "Units[3].UnitId", "Full Carton is inactive in the Unit Master; activate it or choose another unit.");
    }

    [Fact]
    public async Task UpdateProductAsync_ReactivatingDeactivatedRow_RestoresIt()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var c = await CurrentAsync(f, pen.Id);
        await SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id], c[f.HCtn.Id], c[f.FCtn.Id] with { IsActive = false });

        await SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id], c[f.HCtn.Id], c[f.FCtn.Id]);

        Assert.True((await f.Db.ProductUnits.AsNoTracking().SingleAsync(u => u.Id == c[f.FCtn.Id].Id)).IsActive);
    }

    // ------------------------------------------------------------------ base unit (R9)

    private static async Task<Product> CreateRiceAsync(Fixture f) =>
        await f.Service.CreateProductAsync(Input(f, "RICE", f.Kg.Id, Row(f.Kg.Id, null, 1m), Row(f.Bag.Id, f.Kg.Id, 10m)));

    private static UpsertProductInput RiceToGram(Fixture f, Product rice, Dictionary<Guid, ProductUnitInputItem> c) =>
        Input(f, rice.Sku, f.Gram.Id, c[f.Kg.Id] with { UnitId = f.Gram.Id }, c[f.Bag.Id] with { InnerUnitId = f.Gram.Id, InnerQuantity = 10_000m });

    [Fact]
    public async Task UpdateProductAsync_BaseChangeWhenAnyRowIsLocked_RejectedOnBaseUnitId()
    {
        var f = await SeedAsync();
        var rice = await CreateRiceAsync(f);
        await LockAsync(f, await UnitRowAsync(f, rice.Id, f.Bag.Id), LockKind.Price);
        var c = await CurrentAsync(f, rice.Id);

        var ex = await AssertRejectedAsync(() => f.Service.UpdateProductAsync(rice.Id, RiceToGram(f, rice, c)), "BaseUnitId", V.BaseChangeWithLockedUnitsMessage);
        Assert.Equal("BaseUnitId", ex.Field); // product-level error first
    }

    [Fact]
    public async Task UpdateProductAsync_BaseChangeWithStockHistory_RejectedOnBaseUnitId()
    {
        var f = await SeedAsync();
        var rice = await CreateRiceAsync(f);
        f.Db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(), ProductId = rice.Id, Type = StockMovementType.Adjustment, QuantityBaseUnits = 1, ReferenceType = "Adjustment",
            ReferenceNo = "ADJ-1", UserId = f.User.Id,
        });
        await f.Db.SaveChangesAsync();
        var c = await CurrentAsync(f, rice.Id);

        await AssertRejectedAsync(() => f.Service.UpdateProductAsync(rice.Id, RiceToGram(f, rice, c)), "BaseUnitId", V.BaseChangeWithHistoryMessage);
    }

    [Fact]
    public async Task UpdateProductAsync_BaseChangeWithoutHistoryOrLocks_KeepsBaseRowIdAndRescalesOtherRows()
    {
        var f = await SeedAsync();
        var rice = await CreateRiceAsync(f);
        var c = await CurrentAsync(f, rice.Id);

        var updated = await f.Service.UpdateProductAsync(rice.Id, RiceToGram(f, rice, c));

        Assert.Equal(f.Gram.Id, updated!.BaseUnitId);
        var baseRow = await f.Db.ProductUnits.AsNoTracking().SingleAsync(u => u.ProductId == rice.Id && u.IsBaseUnit);
        Assert.Equal((c[f.Kg.Id].Id!.Value, f.Gram.Id), (baseRow.Id, baseRow.UnitId));
        Assert.Equal(10_000m, (await UnitRowAsync(f, rice.Id, f.Bag.Id)).ConversionFactorToBase);
    }

    // ------------------------------------------------------------------ barcodes and defaults

    [Fact]
    public async Task UpdateProductAsync_BarcodeOfAnotherProductsActiveRow_RejectedOnBarcode()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        await f.Service.CreateProductAsync(Input(f, "OTHER", f.Pc.Id, Row(f.Pc.Id, null, 1m, barcode: "8901234567890")));
        var c = await CurrentAsync(f, pen.Id);

        await AssertRejectedAsync(() => SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id] with { Barcode = "8901234567890" }, c[f.HCtn.Id], c[f.FCtn.Id]),
            "Units[1].Barcode", "This barcode is already used by another product.");
    }

    [Fact]
    public async Task UpdateProductAsync_BarcodeOfAnotherProductsInactiveRow_Allowed()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        await f.Service.CreateProductAsync(Input(f, "OTHER", f.Pc.Id,
            Row(f.Pc.Id, null, 1m), Row(f.Box.Id, f.Pc.Id, 12m, barcode: "8901234567890", active: false)));
        var c = await CurrentAsync(f, pen.Id);

        await SaveAsync(f, pen, c[f.Pc.Id], c[f.Box.Id] with { Barcode = "8901234567890" }, c[f.HCtn.Id], c[f.FCtn.Id]);

        Assert.Equal("8901234567890", (await UnitRowAsync(f, pen.Id, f.Box.Id)).Barcode);
    }

    [Fact]
    public async Task UpdateProductAsync_BarcodeAndDefaultMovedBetweenRows_Saved()
    {
        var f = await SeedAsync();
        var pen = await f.Service.CreateProductAsync(Input(f, "STN-PEN", f.Pc.Id,
            Row(f.Pc.Id, null, 1m, barcode: "111", defaultSales: true), Row(f.Box.Id, f.Pc.Id, 10m, barcode: "222")));
        var c = await CurrentAsync(f, pen.Id);

        await SaveAsync(f, pen, c[f.Pc.Id] with { Barcode = "222", IsDefaultSalesUnit = false }, c[f.Box.Id] with { Barcode = "111", IsDefaultSalesUnit = true });

        var rows = await StoredAsync(f, pen.Id);
        Assert.Equal(new[] { ("222", false), ("111", true) }, rows.Select(r => (r.Barcode!, r.IsDefaultSalesUnit)));
    }

    [Fact]
    public async Task UpdateProductAsync_BlankLabelAndBarcode_StoredAsNullAndTrimmed()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var c = await CurrentAsync(f, pen.Id);

        await SaveAsync(f, pen, c[f.Pc.Id] with { Label = "   ", Barcode = " 890 " }, c[f.Box.Id] with { Label = "  Box of 10  " }, c[f.HCtn.Id], c[f.FCtn.Id]);

        var rows = await StoredAsync(f, pen.Id);
        Assert.Equal((null, "890"), (rows[0].Label, rows[0].Barcode));
        Assert.Equal("Box of 10", rows[1].Label);
    }

    // ------------------------------------------------------------------ preview

    [Fact]
    public async Task PreviewUnitsAsync_NewPen_ReturnsChainsWithoutSaving()
    {
        var f = await SeedAsync();

        var preview = await f.Service.PreviewUnitsAsync(null, f.Pc.Id, PenRows(f));

        Assert.NotNull(preview);
        Assert.True(preview!.IsValid);
        Assert.Empty(preview.Errors);
        Assert.Equal(new[] { "1 Piece", "1 Box = 10 Piece", "1 Half Carton = 5 Box = 50 Piece", "1 Full Carton = 2 Half Carton = 10 Box = 100 Piece" },
            preview.Rows.Select(r => r.Chain));
        Assert.Equal(new[] { 0, 1, 2, 3 }, preview.Rows.Select(r => r.Index));
        Assert.Equal(new[] { "pc", "box", "hctn", "fctn" }, preview.Rows.Select(r => r.UnitCode));
        Assert.False(await f.Db.ProductUnits.AnyAsync());
    }

    [Fact]
    public async Task PreviewUnitsAsync_UnknownProduct_ReturnsNull()
    {
        var f = await SeedAsync();

        Assert.Null(await f.Service.PreviewUnitsAsync(Guid.NewGuid(), f.Pc.Id, PenRows(f)));
    }

    [Fact]
    public async Task PreviewUnitsAsync_ExistingProduct_ShowsLockedRowsAndWarnings()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        await LockAsync(f, await UnitRowAsync(f, pen.Id, f.Box.Id), LockKind.SaleLine);
        var c = await CurrentAsync(f, pen.Id);
        var pack = TestUnits.New("Pack", "pk", MeasurementCategory.Packaging, 0, null);
        f.Db.Units.Add(pack);
        await f.Db.SaveChangesAsync();

        var preview = await f.Service.PreviewUnitsAsync(pen.Id, f.Pc.Id,
            [c[f.Pc.Id], c[f.Box.Id], c[f.HCtn.Id], c[f.FCtn.Id], Row(pack.Id, f.Pc.Id, 10m)]);

        Assert.True(preview!.IsValid);
        Assert.Equal(new[] { true, true, false, false, false }, preview.Rows.OrderBy(r => r.Index).Select(r => r.IsLocked));
        Assert.Contains(preview.Warnings, w => w.Field == "Units[4].InnerQuantity");
        Assert.Equal(4, (await StoredAsync(f, pen.Id)).Count);
    }

    [Fact]
    public async Task PreviewUnitsAsync_InvalidUpdate_ReportsExactlyTheErrorsSaveRaises()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        await f.Service.CreateProductAsync(Input(f, "OTHER", f.Pc.Id, Row(f.Pc.Id, null, 1m, barcode: "999")));
        await LockAsync(f, await UnitRowAsync(f, pen.Id, f.Box.Id), LockKind.PurchaseLine);
        var c = await CurrentAsync(f, pen.Id);
        ProductUnitInputItem[] rows =
        [
            c[f.Pc.Id] with { Barcode = "999" },                  // used by another product
            c[f.Box.Id] with { InnerQuantity = 12m },             // locked
            c[f.HCtn.Id] with { InnerUnitId = f.FCtn.Id },        // cycle with FCtn
            c[f.FCtn.Id],
            Row(f.Bag.Id, f.Pc.Id, 2.5m),                         // R5
        ];

        var preview = await f.Service.PreviewUnitsAsync(pen.Id, f.Pc.Id, rows);
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => SaveAsync(f, pen, rows));

        Assert.False(preview!.IsValid);
        Assert.True(preview.Errors.Count >= 4, string.Join(" | ", preview.Errors));
        Assert.Equal(ex.Errors.OrderBy(e => e.Field).ThenBy(e => e.Message), preview.Errors.OrderBy(e => e.Field).ThenBy(e => e.Message));
        Assert.Contains(preview.Errors, e => e.Field == "Units[0].Barcode");
        Assert.Contains(preview.Errors, e => e.Field == "Units[1].InnerQuantity" && e.Message == V.LockedMessage);
        Assert.Contains(preview.Errors, e => e.Field == "Units[2].InnerUnitId");
        Assert.Contains(preview.Errors, e => e.Field == "Units[4].InnerQuantity");
    }

    [Fact]
    public async Task PreviewUnitsAsync_OmittedInnerAndUnknownRowId_SameErrorsAsSave()
    {
        var f = await SeedAsync();
        var pen = await CreatePenAsync(f);
        var c = await CurrentAsync(f, pen.Id);
        ProductUnitInputItem[] rows = [c[f.Pc.Id], c[f.HCtn.Id], c[f.FCtn.Id], Row(f.Bag.Id, f.Pc.Id, 3m, id: Guid.NewGuid())];

        var preview = await f.Service.PreviewUnitsAsync(pen.Id, f.Pc.Id, rows);
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => SaveAsync(f, pen, rows));

        Assert.Equal(ex.Errors.OrderBy(e => e.Field).ThenBy(e => e.Message), preview!.Errors.OrderBy(e => e.Field).ThenBy(e => e.Message));
        Assert.Contains(preview.Errors, e => e.Field == "Units");
        Assert.Contains(preview.Errors, e => e.Field == "Units[1].InnerUnitId");
    }

    [Fact]
    public async Task PreviewUnitsAsync_BaseChangeWithLockedRow_SameBaseUnitIdErrorAsSave()
    {
        var f = await SeedAsync();
        var rice = await CreateRiceAsync(f);
        await LockAsync(f, await UnitRowAsync(f, rice.Id, f.Bag.Id), LockKind.Price);
        var c = await CurrentAsync(f, rice.Id);
        var input = RiceToGram(f, rice, c);

        var preview = await f.Service.PreviewUnitsAsync(rice.Id, f.Gram.Id, input.Units);

        Assert.Contains(preview!.Errors, e => e.Field == "BaseUnitId" && e.Message == V.BaseChangeWithLockedUnitsMessage);
    }
    // ------------------------------------------------------------------ strict revalidation (review M1) via preview, and limits (m3)

    private static async Task AssertPreviewEqualsSaveAsync(Fixture f, Guid productId, UpsertProductInput input, params string[] expectedFields)
    {
        var preview = await f.Service.PreviewUnitsAsync(productId, input.BaseUnitId, input.Units);
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.UpdateProductAsync(productId, input));

        Assert.False(preview!.IsValid);
        Assert.Equal(ex.Errors.OrderBy(e => e.Field).ThenBy(e => e.Message), preview.Errors.OrderBy(e => e.Field).ThenBy(e => e.Message));
        Assert.All(expectedFields, field => Assert.Contains(preview.Errors, e => e.Field == field));
    }

    [Fact]
    public async Task PreviewUnitsAsync_BaseKgToPieceWithUnchangedGramRow_StrictErrorsSameAsSave()
    {
        var f = await SeedAsync();
        var spice = await f.Service.CreateProductAsync(Input(f, "SPC", f.Kg.Id, Row(f.Kg.Id, null, 1m), Row(f.Gram.Id, f.Kg.Id, 0.001m)));
        var c = await CurrentAsync(f, spice.Id);

        await AssertPreviewEqualsSaveAsync(f, spice.Id,
            Input(f, "SPC", f.Pc.Id, c[f.Kg.Id] with { UnitId = f.Pc.Id }, c[f.Gram.Id] with { InnerUnitId = f.Pc.Id }),
            "Units[1].UnitId", "Units[1].InnerQuantity");
    }

    [Fact]
    public async Task PreviewUnitsAsync_BasePieceToKgWithUnchangedDozenRow_StrictErrorSameAsSave()
    {
        var f = await SeedAsync();
        var dozen = TestUnits.New("Dozen", "dz", MeasurementCategory.Count, 0, null);
        f.Db.Units.Add(dozen);
        await f.Db.SaveChangesAsync();
        var eggs = await f.Service.CreateProductAsync(Input(f, "EGG", f.Pc.Id, Row(f.Pc.Id, null, 1m), Row(dozen.Id, f.Pc.Id, 12m)));
        var c = await CurrentAsync(f, eggs.Id);

        await AssertPreviewEqualsSaveAsync(f, eggs.Id,
            Input(f, "EGG", f.Kg.Id, c[f.Pc.Id] with { UnitId = f.Kg.Id }, c[dozen.Id] with { InnerUnitId = f.Kg.Id }),
            "Units[1].UnitId");
    }

    [Fact]
    public async Task PreviewUnitsAsync_InnerRowUnitChanged_OuterUnchangedRowCheckedStrictlySameAsSave()
    {
        // mg base; Gram = 1000 mg; Kilogram = 1000 Gram. Changing the Gram row to Decagram makes the untouched Kilogram
        // row claim 1 kg = 1000 dag, which the standard factors contradict.
        var f = await SeedAsync();
        var mg = TestUnits.New("Milligram", "mg", MeasurementCategory.Weight, 0, 0.000001m);
        var dag = TestUnits.New("Decagram", "dag", MeasurementCategory.Weight, 0, 0.01m);
        f.Db.Units.AddRange(mg, dag);
        await f.Db.SaveChangesAsync();
        var saffron = await f.Service.CreateProductAsync(Input(f, "SAF", mg.Id,
            Row(mg.Id, null, 1m), Row(f.Gram.Id, mg.Id, 1000m), Row(f.Kg.Id, f.Gram.Id, 1000m)));
        var c = await CurrentAsync(f, saffron.Id);

        await AssertPreviewEqualsSaveAsync(f, saffron.Id,
            Input(f, "SAF", mg.Id, c[mg.Id], c[f.Gram.Id] with { UnitId = dag.Id }, c[f.Kg.Id] with { InnerUnitId = dag.Id }),
            "Units[2].InnerQuantity");
    }

    [Fact]
    public async Task PreviewUnitsAsync_UntouchedLegacyRow_ValidWithWarning()
    {
        var f = await SeedAsync();
        var pack = TestUnits.New("Pack", "pk", MeasurementCategory.Packaging, 0, null);
        var pen = new ProductBuilder().WithSku("LEGACY").WithBaseUnit(f.Pc).WithTax(f.Tax)
            .WithUnits(p => TestProductUnits.Pack(p, pack, TestProductUnits.Base(p), 2.5m)).Build();
        f.Db.AddRange(pack, pen);
        await f.Db.SaveChangesAsync();
        var c = await CurrentAsync(f, pen.Id);

        var preview = await f.Service.PreviewUnitsAsync(pen.Id, f.Pc.Id, [c[f.Pc.Id], c[pack.Id] with { Label = "Pack of 2.5" }]);

        Assert.True(preview!.IsValid, string.Join(" | ", preview.Errors));
        Assert.Contains(preview.Warnings, w => w.Field == "Units[1].InnerQuantity");
    }

    [Fact]
    public async Task BaseChangeKgToGramWithPackagingBag_StillAcceptedByPreviewAndSave()
    {
        // The Bag row's factor stays whole on the new 0-dp base (25 kg = 25000 g), so the strict re-check passes.
        var f = await SeedAsync();
        var rice = await f.Service.CreateProductAsync(Input(f, "RICE25", f.Kg.Id, Row(f.Kg.Id, null, 1m), Row(f.Bag.Id, f.Kg.Id, 25m)));
        var c = await CurrentAsync(f, rice.Id);
        var input = Input(f, "RICE25", f.Gram.Id, c[f.Kg.Id] with { UnitId = f.Gram.Id }, c[f.Bag.Id] with { InnerUnitId = f.Gram.Id, InnerQuantity = 25_000m });

        var preview = await f.Service.PreviewUnitsAsync(rice.Id, f.Gram.Id, input.Units);
        Assert.True(preview!.IsValid, string.Join(" | ", preview.Errors));
        Assert.Equal("1 Bag = 25000 Gram", preview.Rows.Single(r => r.UnitId == f.Bag.Id).Chain);

        var updated = await f.Service.UpdateProductAsync(rice.Id, input);

        Assert.Equal(f.Gram.Id, updated!.BaseUnitId);
        Assert.Equal(25_000m, (await UnitRowAsync(f, rice.Id, f.Bag.Id)).ConversionFactorToBase);
    }

    [Fact]
    public async Task PreviewUnitsAsync_MoreThan50Units_InvalidOnUnitsAndUnknownProductStillNull()
    {
        var f = await SeedAsync();
        var rows = Enumerable.Range(0, V.MaxUnits).Select(_ => Row(Guid.NewGuid(), f.Pc.Id, 2m)).Prepend(Row(f.Pc.Id, null, 1m)).ToArray();

        var preview = await f.Service.PreviewUnitsAsync(null, f.Pc.Id, rows);

        Assert.False(preview!.IsValid);
        Assert.Equal(new FieldError("Units", V.TooManyUnitsMessage), Assert.Single(preview.Errors));
        Assert.Null(await f.Service.PreviewUnitsAsync(Guid.NewGuid(), f.Pc.Id, rows));
    }

    [Fact]
    public async Task CreateProductAsync_MoreThan50Units_RejectedOnUnits_ExactlyFiftyAccepted()
    {
        var f = await SeedAsync();
        var units = Enumerable.Range(1, V.MaxUnits - 1).Select(i => TestUnits.New($"Pack {i}", $"p{i}", MeasurementCategory.Packaging, 0, null)).ToList();
        f.Db.Units.AddRange(units);
        await f.Db.SaveChangesAsync();
        var fifty = units.Select((u, i) => Row(u.Id, f.Pc.Id, i + 2m)).Prepend(Row(f.Pc.Id, null, 1m)).ToArray();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            f.Service.CreateProductAsync(Input(f, "TOO-MANY", f.Pc.Id, fifty.Append(Row(f.Box.Id, f.Pc.Id, 500m)).ToArray())));
        var created = await f.Service.CreateProductAsync(Input(f, "FIFTY", f.Pc.Id, fifty));

        Assert.Equal(("Units", V.TooManyUnitsMessage), (ex.Field, ex.Message));
        Assert.Equal(V.MaxUnits, (await StoredAsync(f, created.Id)).Count);
    }
}
