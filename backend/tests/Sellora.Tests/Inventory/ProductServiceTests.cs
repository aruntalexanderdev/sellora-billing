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
        return new SelloraDbContext(options);
    }

    private sealed record Fixture(SelloraDbContext Db, ProductService Service, Product Product, ProductSellingMode Bag, ProductSellingMode LooseKg, Unit Unit, Tax Tax);

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
        var bag = new ProductSellingMode { Id = Guid.NewGuid(), ProductId = product.Id, Label = "10 kg bag", ConversionFactorToBase = 10m };
        var looseKg = new ProductSellingMode { Id = Guid.NewGuid(), ProductId = product.Id, Label = "Loose kg", ConversionFactorToBase = 1m };
        product.SellingModes.Add(bag);
        product.SellingModes.Add(looseKg);

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
                ProductId = product.Id, Product = product, SellingModeId = bag.Id, SellingMode = bag,
                Qty = 30, ConversionFactorToBase = 10, BaseQty = 300, Rate = 580,
                TaxId = tax.Id, Tax = tax, TaxRatePercent = 5, TaxableValue = 17400, TaxAmount = 870, LineTotal = 18270, UnitCost = 58,
            };
            purchase.Lines.Add(line);
            db.Add(purchase);
        }

        await db.SaveChangesAsync();

        return new Fixture(db, new ProductService(db), product, bag, looseKg, unit, tax);
    }

    private static UpsertProductInput BuildInput(Fixture f, params SellingModeInputItem[] modes) => new(
        f.Product.Sku, f.Product.Name, f.Product.Category, f.Unit.Id, f.Tax.Id, f.Product.ReorderLevel, modes);

    [Fact]
    public async Task UpdateProductAsync_ChangingBaseUnit_IsBlockedOnceStockHistoryExists()
    {
        var f = await SeedAsync();
        var gram = new Unit { Id = Guid.NewGuid(), Name = "Gram", ShortCode = "g", MeasurementCategory = MeasurementCategory.Weight, DecimalPlaces = 0, StandardFactor = 0.001m };
        f.Db.Units.Add(gram);
        f.Db.PurchaseBatches.Add(new PurchaseBatch { Id = Guid.NewGuid(), ProductId = f.Product.Id, PurchasedBaseQty = 10, RemainingBaseQty = 10, UnitCost = 5 });
        await f.Db.SaveChangesAsync();

        var input = BuildInput(f, new SellingModeInputItem(f.Bag.Id, f.Bag.Label, f.Bag.ConversionFactorToBase), new SellingModeInputItem(f.LooseKg.Id, f.LooseKg.Label, f.LooseKg.ConversionFactorToBase)) with { BaseUnitId = gram.Id };

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

        var input = BuildInput(f, new SellingModeInputItem(f.Bag.Id, f.Bag.Label, f.Bag.ConversionFactorToBase), new SellingModeInputItem(f.LooseKg.Id, f.LooseKg.Label, f.LooseKg.ConversionFactorToBase)) with { BaseUnitId = gram.Id };

        var updated = await f.Service.UpdateProductAsync(f.Product.Id, input);

        Assert.Equal(gram.Id, updated!.BaseUnitId);
    }

    [Fact]
    public async Task UpdateProductAsync_RemovingModeReferencedByPurchaseOrderLine_DeactivatesInsteadOfDeleting()
    {
        var f = await SeedAsync(withPurchaseOrderLineOnBag: true);

        // Submit only the loose-kg mode, dropping the referenced bag mode.
        var input = BuildInput(f, new SellingModeInputItem(f.LooseKg.Id, f.LooseKg.Label, f.LooseKg.ConversionFactorToBase));

        var updated = await f.Service.UpdateProductAsync(f.Product.Id, input);

        Assert.NotNull(updated);
        var bagInDb = await f.Db.ProductSellingModes.SingleAsync(m => m.Id == f.Bag.Id);
        Assert.False(bagInDb.IsActive);

        var line = await f.Db.PurchaseOrderLines.SingleAsync();
        Assert.Equal(f.Bag.Id, line.SellingModeId);
    }

    [Fact]
    public async Task UpdateProductAsync_RemovingModeWithNoReferences_DeletesIt()
    {
        var f = await SeedAsync();

        var input = BuildInput(f, new SellingModeInputItem(f.Bag.Id, f.Bag.Label, f.Bag.ConversionFactorToBase));

        await f.Service.UpdateProductAsync(f.Product.Id, input);

        Assert.False(await f.Db.ProductSellingModes.AnyAsync(m => m.Id == f.LooseKg.Id));
        Assert.True(await f.Db.ProductSellingModes.AnyAsync(m => m.Id == f.Bag.Id));
    }

    [Fact]
    public async Task UpdateProductAsync_EditingExistingMode_PreservesId()
    {
        var f = await SeedAsync();

        var input = BuildInput(f,
            new SellingModeInputItem(f.Bag.Id, "12 kg bag", 12m),
            new SellingModeInputItem(f.LooseKg.Id, f.LooseKg.Label, f.LooseKg.ConversionFactorToBase));

        await f.Service.UpdateProductAsync(f.Product.Id, input);

        var bagInDb = await f.Db.ProductSellingModes.SingleAsync(m => m.Id == f.Bag.Id);
        Assert.Equal("12 kg bag", bagInDb.Label);
        Assert.Equal(12m, bagInDb.ConversionFactorToBase);
    }

    [Fact]
    public async Task UpdateProductAsync_AddingNewMode_CreatesIt()
    {
        var f = await SeedAsync();

        var input = BuildInput(f,
            new SellingModeInputItem(f.Bag.Id, f.Bag.Label, f.Bag.ConversionFactorToBase),
            new SellingModeInputItem(f.LooseKg.Id, f.LooseKg.Label, f.LooseKg.ConversionFactorToBase),
            new SellingModeInputItem(null, "25 kg sack", 25m));

        var updated = await f.Service.UpdateProductAsync(f.Product.Id, input);

        Assert.NotNull(updated);
        var modes = await f.Db.ProductSellingModes.Where(m => m.ProductId == f.Product.Id).ToListAsync();
        Assert.Equal(3, modes.Count);
        Assert.Contains(modes, m => m.Label == "25 kg sack" && m.Id != f.Bag.Id && m.Id != f.LooseKg.Id);
    }

    [Fact]
    public async Task UpdateProductAsync_ReaddingPreviouslyDeactivatedMode_Reactivates()
    {
        var f = await SeedAsync(withPurchaseOrderLineOnBag: true);

        await f.Service.UpdateProductAsync(f.Product.Id,
            BuildInput(f, new SellingModeInputItem(f.LooseKg.Id, f.LooseKg.Label, f.LooseKg.ConversionFactorToBase)));
        Assert.False((await f.Db.ProductSellingModes.SingleAsync(m => m.Id == f.Bag.Id)).IsActive);

        await f.Service.UpdateProductAsync(f.Product.Id,
            BuildInput(f,
                new SellingModeInputItem(f.Bag.Id, f.Bag.Label, f.Bag.ConversionFactorToBase),
                new SellingModeInputItem(f.LooseKg.Id, f.LooseKg.Label, f.LooseKg.ConversionFactorToBase)));

        Assert.True((await f.Db.ProductSellingModes.SingleAsync(m => m.Id == f.Bag.Id)).IsActive);
    }

    [Fact]
    public async Task UpdateProductAsync_UnknownModeId_ThrowsDomainValidationException()
    {
        var f = await SeedAsync();

        var input = BuildInput(f, new SellingModeInputItem(Guid.NewGuid(), "Ghost", 1m));

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.UpdateProductAsync(f.Product.Id, input));
    }

    // ---------- Base unit rules (Phase 1: Unit Master) ----------

    private static UpsertProductInput NewProductInput(Fixture f, Guid baseUnitId) => new(
        "NEW-SKU", "New product", "Test", baseUnitId, f.Tax.Id, 0m, [new SellingModeInputItem(null, "Each", 1m)]);

    private static UpsertProductInput UnchangedInput(Fixture f) => BuildInput(f,
        new SellingModeInputItem(f.Bag.Id, f.Bag.Label, f.Bag.ConversionFactorToBase),
        new SellingModeInputItem(f.LooseKg.Id, f.LooseKg.Label, f.LooseKg.ConversionFactorToBase));

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
}
