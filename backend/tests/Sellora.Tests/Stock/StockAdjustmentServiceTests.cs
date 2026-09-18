using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.Stock;

public class StockAdjustmentServiceTests
{
    private static SelloraDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SelloraDbContext(options);
    }

    private sealed record Fixture(SelloraDbContext Db, StockAdjustmentService Service, Product Product, Guid UserId);

    private static async Task<Fixture> SeedAsync()
    {
        var db = NewDb();
        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin",
            Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Kilogram", ShortCode = "kg", Type = UnitType.Weight };
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 5%", RatePercent = 5m };
        var product = new Product
        {
            Id = Guid.NewGuid(), Sku = "RICE-BP10", Name = "Ponni Rice", BaseUnitId = unit.Id, BaseUnit = unit,
            TaxId = tax.Id, Tax = tax, ReorderLevel = 100,
        };

        db.AddRange(role, user, unit, tax, product);
        await db.SaveChangesAsync();

        return new Fixture(db, new StockAdjustmentService(db), product, user.Id);
    }

    private static async Task SeedOpeningStockAsync(Fixture f, decimal qty, decimal unitCost)
    {
        await f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, qty, unitCost, "Initial stock"),
            f.UserId);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_OpeningStockIncrease_CreatesBatchAndUpdatesStockItem()
    {
        var f = await SeedAsync();

        var adjustment = await f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 200, 55m, "Initial stock"),
            f.UserId);

        Assert.Equal("ADJ-0001", adjustment.AdjustmentNo);
        Assert.Equal(0, adjustment.BeforeQty);
        Assert.Equal(200, adjustment.AfterQty);

        var stockItem = await f.Db.StockItems.SingleAsync(s => s.ProductId == f.Product.Id);
        Assert.Equal(200m, stockItem.QuantityOnHand);
        Assert.Equal(55m, stockItem.AverageCost);

        var batch = await f.Db.PurchaseBatches.SingleAsync(b => b.ProductId == f.Product.Id);
        Assert.Null(batch.PurchaseOrderLineId);
        Assert.Equal(adjustment.Id, batch.StockAdjustmentId);
        Assert.Equal(200m, batch.RemainingBaseQty);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_DamageDecrease_ConsumesOldestBatchFirst()
    {
        var f = await SeedAsync();
        await SeedOpeningStockAsync(f, 100, 50m);
        await SeedOpeningStockAsync(f, 100, 60m); // second, newer batch

        await f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, AdjustmentType.Damage, AdjustmentDirection.Decrease, 120, null, "Water damage"),
            f.UserId);

        var batches = await f.Db.PurchaseBatches.OrderBy(b => b.CreatedAt).ToListAsync();
        Assert.Equal(0m, batches[0].RemainingBaseQty); // fully consumed first
        Assert.Equal(80m, batches[1].RemainingBaseQty); // partially consumed second

        var stockItem = await f.Db.StockItems.SingleAsync(s => s.ProductId == f.Product.Id);
        Assert.Equal(80m, stockItem.QuantityOnHand);
        // average cost unchanged on a pure decrease
        Assert.Equal(55m, stockItem.AverageCost);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_DecreaseMoreThanOnHand_Throws()
    {
        var f = await SeedAsync();
        await SeedOpeningStockAsync(f, 50, 50m);

        var input = new CreateAdjustmentInput(f.Product.Id, AdjustmentType.Loss, AdjustmentDirection.Decrease, 51, null, null);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateAdjustmentAsync(input, f.UserId));
    }

    [Fact]
    public async Task CreateAdjustmentAsync_DamageWithIncreaseDirection_Throws()
    {
        var f = await SeedAsync();
        var input = new CreateAdjustmentInput(f.Product.Id, AdjustmentType.Damage, AdjustmentDirection.Increase, 10, null, null);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateAdjustmentAsync(input, f.UserId));
    }

    [Fact]
    public async Task CreateAdjustmentAsync_OpeningStockWithDecreaseDirection_Throws()
    {
        var f = await SeedAsync();
        var input = new CreateAdjustmentInput(f.Product.Id, AdjustmentType.OpeningStock, AdjustmentDirection.Decrease, 10, null, null);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateAdjustmentAsync(input, f.UserId));
    }

    [Fact]
    public async Task CreateAdjustmentAsync_CorrectionEitherDirection_Succeeds()
    {
        var f = await SeedAsync();
        await SeedOpeningStockAsync(f, 50, 50m);

        await f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, AdjustmentType.Correction, AdjustmentDirection.Decrease, 5, null, "Recount"), f.UserId);
        await f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, AdjustmentType.Correction, AdjustmentDirection.Increase, 3, 50m, "Recount"), f.UserId);

        var stockItem = await f.Db.StockItems.SingleAsync(s => s.ProductId == f.Product.Id);
        Assert.Equal(48m, stockItem.QuantityOnHand);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_IncreaseWithoutUnitCost_Throws()
    {
        var f = await SeedAsync();
        var input = new CreateAdjustmentInput(f.Product.Id, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 10, null, null);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateAdjustmentAsync(input, f.UserId));
    }

    [Fact]
    public async Task CreateAdjustmentAsync_WritesStockMovementWithCorrectSign()
    {
        var f = await SeedAsync();
        await SeedOpeningStockAsync(f, 100, 50m);

        await f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, AdjustmentType.Expiry, AdjustmentDirection.Decrease, 10, null, "Expired"), f.UserId);

        var movements = await f.Db.StockMovements.Where(m => m.ProductId == f.Product.Id).OrderBy(m => m.OccurredAt).ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Equal(100m, movements[0].QuantityBaseUnits);
        Assert.Equal(-10m, movements[1].QuantityBaseUnits);
        Assert.All(movements, m => Assert.Equal(StockMovementType.Adjustment, m.Type));
    }
}
