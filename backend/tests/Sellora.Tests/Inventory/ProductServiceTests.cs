using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Inventory;

public class ProductServiceTests
{
    private static SelloraDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return Sellora.Tests.TestData.TestCompany.NewDb(options);
    }

    private sealed record Fixture(SelloraDbContext Db, ProductService Service, Product Product, ProductUnit Bag, ProductUnit LooseKg, Unit Unit, Tax Tax);

    private static async Task<Fixture> SeedAsync(bool withPurchaseOrderLineOnBag = false)
    {
        var db = NewDb();

        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin",
            Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Kilogram", ShortCode = "kg", MeasurementCategory = MeasurementCategory.Weight, DecimalPlaces = 3, StandardFactor = 1m };
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 5%", RatePercent = 5m };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var product = new Product
        {
            Id = Guid.NewGuid(), Sku = "RICE-BP10", Name = "Ponni Rice", BaseUnitId = unit.Id, BaseUnit = unit,
            TaxId = tax.Id, Tax = tax, ReorderLevel = 100,
        };
        var looseKg = TestProductUnits.Base(product, "Loose kg");
        var bag = TestProductUnits.Pack(product, TestUnits.Bag(), looseKg, 10m, "10 kg bag");

        db.AddRange(role, user, unit, tax, inr, supplier, product);

        if (withPurchaseOrderLineOnBag)
        {
            var purchase = new PurchaseOrder
            {
                Id = Guid.NewGuid(), PurchaseNo = "PUR-0001", SupplierId = supplier.Id, Supplier = supplier,
                PurchaseDate = new DateOnly(2026, 9, 1), PaymentMethod = PaymentMethod.Cash, CreatedByUserId = user.Id, CreatedByUser = user, CurrencyId = inr.Id,
            };
            var line = new PurchaseOrderLine
            {
                Id = Guid.NewGuid(), PurchaseOrderId = purchase.Id, PurchaseOrder = purchase,
                ProductId = product.Id, Product = product, ProductUnitId = bag.Id, ProductUnit = bag,
                Qty = 30, ConversionFactorToBase = 10, BaseQty = 300, Rate = 580,
                TaxId = tax.Id, Tax = tax, TaxRatePercent = 5, TaxableValue = 17400, TaxAmount = 870, LineTotal = 18270, UnitCost = 58,
            };
            purchase.Lines.Add(line);
            db.Add(purchase);
        }

        await db.SaveChangesAsync();

        return new Fixture(db, new ProductService(db), product, bag, looseKg, unit, tax);
    }

    private static UpsertProductInput BuildInput(Fixture f, params ProductUnitInputItem[] units) => new(
        f.Product.Sku, f.Product.Name, f.Product.Category, f.Unit.Id, f.Tax.Id, f.Product.ReorderLevel, units);

    /// <summary>The stored base row (Loose kg), optionally re-pointed at another base unit.</summary>
    private static ProductUnitInputItem BaseRow(Fixture f, Guid? unitId = null) =>
        new(f.LooseKg.Id, unitId ?? f.Unit.Id, null, 1m, f.LooseKg.Label, null, true, true, false, false, true);

    /// <summary>The stored bag row: "1 Bag contains <paramref name="qty"/> of the base unit".</summary>
    private static ProductUnitInputItem BagRow(Fixture f, decimal qty = 10m, string? label = null, Guid? innerUnitId = null) =>
        new(f.Bag.Id, f.Bag.UnitId, innerUnitId ?? f.Unit.Id, qty, label ?? f.Bag.Label, null, true, true, false, false, true);

    [Fact]
    public async Task UpdateProductAsync_ChangingBaseUnit_IsBlockedOnceStockHistoryExists()
    {
        var f = await SeedAsync();
        var gram = new Unit { Id = Guid.NewGuid(), Name = "Gram", ShortCode = "g", MeasurementCategory = MeasurementCategory.Weight, DecimalPlaces = 0, StandardFactor = 0.001m };
        f.Db.Units.Add(gram);
        f.Db.PurchaseBatches.Add(new PurchaseBatch { Id = Guid.NewGuid(), ProductId = f.Product.Id, PurchasedBaseQty = 10, RemainingBaseQty = 10, UnitCost = 5 });
        await f.Db.SaveChangesAsync();

        var input = BuildInput(f, BagRow(f, innerUnitId: gram.Id), BaseRow(f, gram.Id)) with { BaseUnitId = gram.Id };

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.UpdateProductAsync(f.Product.Id, input));
        Assert.Equal(f.Unit.Id, (await f.Db.Products.SingleAsync()).BaseUnitId);
    }

    [Fact]
    public async Task UpdateProductAsync_ChangingBaseUnit_IsAllowedWhenNoStockHistory()
    {
        var f = await SeedAsync();
        var gram = new Unit { Id = Guid.NewGuid(), Name = "Gram", ShortCode = "g", MeasurementCategory = MeasurementCategory.Weight, DecimalPlaces = 0, StandardFactor = 0.001m };
        f.Db.Units.Add(gram);
        await f.Db.SaveChangesAsync();

        var input = BuildInput(f, BagRow(f, innerUnitId: gram.Id), BaseRow(f, gram.Id)) with { BaseUnitId = gram.Id };

        var updated = await f.Service.UpdateProductAsync(f.Product.Id, input);

        Assert.Equal(gram.Id, updated!.BaseUnitId);
    }

    [Fact]
    public async Task UpdateProductAsync_RemovingUnitReferencedByPurchaseOrderLine_DeactivatesInsteadOfDeleting()
    {
        var f = await SeedAsync(withPurchaseOrderLineOnBag: true);

        // Submit only the loose-kg base row, dropping the referenced bag row.
        var input = BuildInput(f, BaseRow(f));

        var updated = await f.Service.UpdateProductAsync(f.Product.Id, input);

        Assert.NotNull(updated);
        var bagInDb = await f.Db.ProductUnits.SingleAsync(m => m.Id == f.Bag.Id);
        Assert.False(bagInDb.IsActive);

        var line = await f.Db.PurchaseOrderLines.SingleAsync();
        Assert.Equal(f.Bag.Id, line.ProductUnitId);
    }

    [Fact]
    public async Task UpdateProductAsync_RemovingUnitWithNoReferences_DeletesIt()
    {
        var f = await SeedAsync();

        // Phase 2: the base row (Loose kg) can never be removed, so the unreferenced non-base row is dropped instead.
        var input = BuildInput(f, BaseRow(f));

        await f.Service.UpdateProductAsync(f.Product.Id, input);

        Assert.False(await f.Db.ProductUnits.AnyAsync(m => m.Id == f.Bag.Id));
        Assert.True(await f.Db.ProductUnits.AnyAsync(m => m.Id == f.LooseKg.Id));
    }

    [Fact]
    public async Task UpdateProductAsync_EditingExistingUnit_PreservesId()
    {
        var f = await SeedAsync();

        var input = BuildInput(f,
            BagRow(f, 12m, "12 kg bag"),
            BaseRow(f));

        await f.Service.UpdateProductAsync(f.Product.Id, input);

        var bagInDb = await f.Db.ProductUnits.SingleAsync(m => m.Id == f.Bag.Id);
        Assert.Equal("12 kg bag", bagInDb.Label);
        Assert.Equal(12m, bagInDb.ConversionFactorToBase);
    }

    [Fact]
    public async Task UpdateProductAsync_AddingNewUnit_CreatesItOverItsInnerUnit()
    {
        var f = await SeedAsync();
        var sack = TestUnits.New("Sack", "sack", MeasurementCategory.Packaging, 0, null);
        f.Db.Units.Add(sack);
        await f.Db.SaveChangesAsync();

        // "1 Sack contains 5 Bag": the new row names its inner by the bag row's UnitId.
        var input = BuildInput(f,
            BagRow(f),
            BaseRow(f),
            new ProductUnitInputItem(null, sack.Id, f.Bag.UnitId, 5m, "50 kg sack", null, true, true, false, false, true));

        var updated = await f.Service.UpdateProductAsync(f.Product.Id, input);

        Assert.NotNull(updated);
        var units = await f.Db.ProductUnits.AsNoTracking().Where(m => m.ProductId == f.Product.Id).ToListAsync();
        Assert.Equal(3, units.Count);
        var added = Assert.Single(units, m => m.Id != f.Bag.Id && m.Id != f.LooseKg.Id);
        Assert.Equal((sack.Id, (Guid?)f.Bag.Id, 5m, 50m, false, "50 kg sack"),
            (added.UnitId, added.InnerProductUnitId, added.InnerQuantity, added.ConversionFactorToBase, added.IsBaseUnit, added.Label));
        Assert.Equal(new short[] { 0, 1, 2 }, new[] { f.LooseKg.Id, f.Bag.Id, added.Id }.Select(id => units.Single(u => u.Id == id).SortOrder));
    }

    [Fact]
    public async Task UpdateProductAsync_ReaddingPreviouslyDeactivatedUnit_Reactivates()
    {
        var f = await SeedAsync(withPurchaseOrderLineOnBag: true);

        await f.Service.UpdateProductAsync(f.Product.Id,
            BuildInput(f, BaseRow(f)));
        Assert.False((await f.Db.ProductUnits.SingleAsync(m => m.Id == f.Bag.Id)).IsActive);

        await f.Service.UpdateProductAsync(f.Product.Id,
            BuildInput(f, BagRow(f), BaseRow(f)));

        Assert.True((await f.Db.ProductUnits.SingleAsync(m => m.Id == f.Bag.Id)).IsActive);
    }

    [Fact]
    public async Task UpdateProductAsync_UnknownProductUnitId_ThrowsDomainValidationException()
    {
        var f = await SeedAsync();

        var input = BuildInput(f, BaseRow(f), new ProductUnitInputItem(Guid.NewGuid(), f.Bag.UnitId, f.Unit.Id, 10m, "Ghost", null, true, true, false, false, true));

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.UpdateProductAsync(f.Product.Id, input));
    }

    // ---------- Base unit rules (Phase 1: Unit Master) ----------

    private static UpsertProductInput NewProductInput(Fixture f, Guid baseUnitId) => new(
        "NEW-SKU", "New product", "Test", baseUnitId, f.Tax.Id, 0m, [new ProductUnitInputItem(null, baseUnitId, null, 1m, "Each", null, true, true, false, false, true)]);

    private static UpsertProductInput UnchangedInput(Fixture f) => BuildInput(f, BagRow(f), BaseRow(f));

    private static async Task<Unit> AddUnitAsync(Fixture f, Unit unit)
    {
        f.Db.Units.Add(unit);
        await f.Db.SaveChangesAsync();
        return unit;
    }

    [Fact]
    public async Task CreateProductAsync_PackagingBaseUnit_RejectedOnBaseUnitId()
    {
        var f = await SeedAsync();
        var box = await AddUnitAsync(f, TestUnits.Box());

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateProductAsync(NewProductInput(f, box.Id)));

        Assert.Equal(nameof(UpsertProductInput.BaseUnitId), ex.Field);
        Assert.Equal(1, await f.Db.Products.CountAsync());
    }

    [Fact]
    public async Task CreateProductAsync_ActiveCountBaseUnit_Created()
    {
        var f = await SeedAsync();
        var piece = await AddUnitAsync(f, TestUnits.Piece());

        var product = await f.Service.CreateProductAsync(NewProductInput(f, piece.Id));

        Assert.Equal(piece.Id, product.BaseUnitId);
    }

    [Fact]
    public async Task CreateProductAsync_InactiveBaseUnit_RejectedOnBaseUnitId()
    {
        var f = await SeedAsync();
        var piece = await AddUnitAsync(f, TestUnits.Piece().Inactive());

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateProductAsync(NewProductInput(f, piece.Id)));

        Assert.Equal(nameof(UpsertProductInput.BaseUnitId), ex.Field);
        Assert.Equal(1, await f.Db.Products.CountAsync());
    }

    [Fact]
    public async Task CreateProductAsync_UnknownBaseUnit_RejectedOnBaseUnitId()
    {
        var f = await SeedAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateProductAsync(NewProductInput(f, Guid.NewGuid())));

        Assert.Equal(nameof(UpsertProductInput.BaseUnitId), ex.Field);
    }

    [Fact]
    public async Task UpdateProductAsync_ChangingToPackagingBaseUnit_RejectedOnBaseUnitId()
    {
        var f = await SeedAsync();
        var box = await AddUnitAsync(f, TestUnits.Box());

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Service.UpdateProductAsync(f.Product.Id, UnchangedInput(f) with { BaseUnitId = box.Id }));

        Assert.Equal(nameof(UpsertProductInput.BaseUnitId), ex.Field);
        Assert.Equal(f.Unit.Id, (await f.Db.Products.AsNoTracking().SingleAsync()).BaseUnitId);
    }

    [Fact]
    public async Task UpdateProductAsync_KeepingLegacyPackagingBaseUnit_RejectedOnBaseUnitId()
    {
        // A product whose base unit is (still) a Packaging unit cannot be saved until its base is fixed.
        var f = await SeedAsync();
        f.Unit.MeasurementCategory = MeasurementCategory.Packaging;
        f.Unit.DecimalPlaces = 0;
        f.Unit.StandardFactor = null;
        await f.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.UpdateProductAsync(f.Product.Id, UnchangedInput(f)));

        Assert.Equal(nameof(UpsertProductInput.BaseUnitId), ex.Field);
    }

    [Fact]
    public async Task UpdateProductAsync_ChangingToInactiveBaseUnit_RejectedOnBaseUnitId()
    {
        var f = await SeedAsync();
        var gram = await AddUnitAsync(f, TestUnits.Gram().Inactive());

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Service.UpdateProductAsync(f.Product.Id, UnchangedInput(f) with { BaseUnitId = gram.Id }));

        Assert.Equal(nameof(UpsertProductInput.BaseUnitId), ex.Field);
        Assert.Equal(f.Unit.Id, (await f.Db.Products.AsNoTracking().SingleAsync()).BaseUnitId);
    }

    [Fact]
    public async Task UpdateProductAsync_KeepingAlreadyInactiveBaseUnit_Allowed()
    {
        var f = await SeedAsync();
        f.Unit.IsActive = false;
        await f.Db.SaveChangesAsync();

        var updated = await f.Service.UpdateProductAsync(f.Product.Id, UnchangedInput(f) with { Name = "Ponni Rice (old stock)" });

        Assert.NotNull(updated);
        Assert.Equal(f.Unit.Id, updated!.BaseUnitId);
        Assert.Equal("Ponni Rice (old stock)", updated.Name);
    }

    [Fact]
    public async Task UpdateProductAsync_MissingProduct_ReturnsNull()
    {
        var f = await SeedAsync();

        Assert.Null(await f.Service.UpdateProductAsync(Guid.NewGuid(), UnchangedInput(f)));
    }

    [Fact]
    public async Task UpdateProductAsync_MissingProductWithInvalidBaseUnit_ReturnsNullNotValidationError()
    {
        var f = await SeedAsync();
        var box = await AddUnitAsync(f, TestUnits.Box());

        Assert.Null(await f.Service.UpdateProductAsync(Guid.NewGuid(), UnchangedInput(f) with { BaseUnitId = box.Id }));
    }

    [Fact]
    public async Task DeleteProductAsync_WithoutHistory_RemovesProductUnitsPricesAndZeroStock_AndAudits()
    {
        var f = await SeedAsync();
        f.Db.SellingPrices.Add(new SellingPrice { Id = Guid.NewGuid(), ProductUnitId = f.Bag.Id, Price = 600, EffectiveFrom = DateTimeOffset.UtcNow, CreatedByUserId = Guid.NewGuid() });
        f.Db.StockItems.Add(new StockItem { ProductId = f.Product.Id, StockLocationId = Guid.NewGuid(), QuantityOnHand = 0 });
        await f.Db.SaveChangesAsync();
        var userId = Guid.NewGuid();

        Assert.True(await f.Service.DeleteProductAsync(f.Product.Id, userId, "trace-1"));

        Assert.Empty(await f.Db.Products.ToListAsync());
        Assert.Empty(await f.Db.ProductUnits.ToListAsync());
        Assert.Empty(await f.Db.SellingPrices.ToListAsync());
        Assert.Empty(await f.Db.StockItems.ToListAsync());
        var audit = Assert.Single(await f.Db.AuditEvents.ToListAsync());
        Assert.Equal(AuditActions.ProductDeleted, audit.Action);
        Assert.Equal(f.Product.Id, audit.TargetId);
        Assert.Equal(userId, audit.ActorUserId);
        Assert.Contains("RICE-BP10", audit.Before);
    }

    [Fact]
    public async Task DeleteProductAsync_UsedInPurchase_IsRefused_AndNothingChanges()
    {
        var f = await SeedAsync(withPurchaseOrderLineOnBag: true);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.DeleteProductAsync(f.Product.Id, Guid.NewGuid(), null));

        Assert.Contains(ex.Errors, e => e.Field == "Product" && e.Message.Contains("purchases"));
        Assert.Single(await f.Db.Products.ToListAsync());
        Assert.Equal(2, await f.Db.ProductUnits.CountAsync());
        Assert.Empty(await f.Db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task DeleteProductAsync_WithStockOnHand_IsRefused()
    {
        var f = await SeedAsync();
        f.Db.StockItems.Add(new StockItem { ProductId = f.Product.Id, StockLocationId = Guid.NewGuid(), QuantityOnHand = 5 });
        await f.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.DeleteProductAsync(f.Product.Id, Guid.NewGuid(), null));

        Assert.Contains(ex.Errors, e => e.Message.Contains("stock on hand"));
        Assert.Single(await f.Db.Products.ToListAsync());
    }

    [Fact]
    public async Task DeleteProductAsync_MissingProduct_ReturnsFalse()
    {
        var f = await SeedAsync();

        Assert.False(await f.Service.DeleteProductAsync(Guid.NewGuid(), Guid.NewGuid(), null));
        Assert.Single(await f.Db.Products.ToListAsync());
    }

    [Fact]
    public async Task GetProductHistoryAsync_Batch_FlagsOnlyProductsWithHistory_WithTheirLabels()
    {
        var f = await SeedAsync(withPurchaseOrderLineOnBag: true);
        var clean = new Product { Id = Guid.NewGuid(), Sku = "SALT-1", Name = "Salt", BaseUnitId = f.Unit.Id, TaxId = f.Tax.Id };
        f.Db.Products.Add(clean);
        f.Db.StockItems.Add(new StockItem { ProductId = clean.Id, StockLocationId = Guid.NewGuid(), QuantityOnHand = 0 });
        await f.Db.SaveChangesAsync();

        var history = await ProductUnitUsage.GetProductHistoryAsync(f.Db, [f.Product.Id, clean.Id], CancellationToken.None);

        Assert.Equal(["purchases"], history[f.Product.Id]);
        Assert.False(history.ContainsKey(clean.Id)); // zero stock is not history
        Assert.Empty(await ProductUnitUsage.GetProductHistoryAsync(f.Db, clean.Id, CancellationToken.None));
    }
}
