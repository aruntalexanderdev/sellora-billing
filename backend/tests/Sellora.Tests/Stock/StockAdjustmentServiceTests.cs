using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

public class StockAdjustmentServiceTests
{
    private static SelloraDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return Sellora.Tests.TestData.TestCompany.NewDb(options);
    }

    private sealed record Fixture(SelloraDbContext Db, StockAdjustmentService Service, Product Product, Guid BaseUnitId, Guid UserId);

    private static async Task<Fixture> SeedAsync()
    {
        var db = NewDb();
        var user = TestUsers.Admin();
        var kg = TestUnits.Kilogram();
        ProductUnit baseRow = null!;
        var product = new ProductBuilder().WithSku("RICE-BP10").WithName("Ponni Rice").WithBaseUnit(kg).WithReorderLevel(100)
            .WithUnits(p => baseRow = TestProductUnits.Base(p)) // adjustments below are entered in the base unit (kg)
            .Build();

        db.AddRange(user.Role, user, kg, product.Tax, product);
        await db.SaveChangesAsync();

        return new Fixture(db, new StockAdjustmentService(db), product, baseRow.Id, user.Id);
    }

    private static async Task SeedOpeningStockAsync(Fixture f, decimal qty, decimal unitCost)
    {
        await f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, qty, unitCost, "Initial stock"),
            f.UserId);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_ReplayedWithSameIdempotencyKey_AdjustsOnce()
    {
        var f = await SeedAsync();
        var input = new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 50, 10m, null, "adj-1");

        var first = await f.Service.CreateAdjustmentAsync(input, f.UserId);
        var replay = await f.Service.CreateAdjustmentAsync(input, f.UserId);

        Assert.Equal(first.Id, replay.Id);
        Assert.Single(await f.Db.StockAdjustments.ToListAsync());
        Assert.Equal(50m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_Increase_AndDecrease_BothLinkTheirMovementToTheBatch()
    {
        var f = await SeedAsync();
        await SeedOpeningStockAsync(f, 100, 50m);
        var batch = await f.Db.PurchaseBatches.SingleAsync();

        await f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.Damage, AdjustmentDirection.Decrease, 10, null, "Damaged"),
            f.UserId);

        var movements = await f.Db.StockMovements.ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.All(movements, m => Assert.Equal(batch.Id, m.PurchaseBatchId));
        Assert.Equal(90m, await f.Db.PurchaseBatches.SumAsync(b => b.RemainingBaseQty));
    }

    [Fact]
    public async Task CreateAdjustmentAsync_DecreaseAcrossTwoBatches_WritesOneMovementPerBatchSummingToTheAdjustment()
    {
        var f = await SeedAsync();
        await SeedOpeningStockAsync(f, 100, 50m); // older batch
        await SeedOpeningStockAsync(f, 100, 60m); // newer batch

        var adjustment = await f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.Loss, AdjustmentDirection.Decrease, 130, null, "Stolen"),
            f.UserId);

        var batches = await f.Db.PurchaseBatches.OrderBy(b => b.CreatedAt).ToListAsync();
        Assert.Equal(0m, batches[0].RemainingBaseQty);
        Assert.Equal(70m, batches[1].RemainingBaseQty);
        Assert.Equal(100m, batches[0].PurchasedBaseQty); // history untouched

        var removals = await f.Db.StockMovements.Where(m => m.ReferenceId == adjustment.Id && m.QuantityBaseUnits < 0).ToListAsync();
        Assert.Equal(2, removals.Count);
        Assert.Equal(-130m, removals.Sum(m => m.QuantityBaseUnits));
        Assert.Equal(-100m, removals.Single(m => m.PurchaseBatchId == batches[0].Id).QuantityBaseUnits);
        Assert.Equal(-30m, removals.Single(m => m.PurchaseBatchId == batches[1].Id).QuantityBaseUnits);
        Assert.Equal(70m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_BatchesShortOfStockOnHand_RefusesAndChangesNothing()
    {
        var f = await SeedAsync();
        await SeedOpeningStockAsync(f, 100, 50m);
        var stock = await f.Db.StockItems.SingleAsync();
        stock.QuantityOnHand = 200; // drift: on-hand claims more than the batches hold
        await f.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.Loss, AdjustmentDirection.Decrease, 150, null, null), f.UserId));

        // Read untracked: what was persisted, not the failed request's half-modified in-memory entities.
        Assert.Equal(100m, (await f.Db.PurchaseBatches.AsNoTracking().SingleAsync()).RemainingBaseQty);
        Assert.Single(await f.Db.StockAdjustments.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task CreateAdjustmentAsync_ZeroOrNegativeQuantity_IsRejected(double qty)
    {
        var f = await SeedAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, (decimal)qty, 10m, null), f.UserId));
    }

    [Fact]
    public async Task CreateAdjustmentAsync_QuantityBeyondThreeDecimals_IsRejected()
    {
        var f = await SeedAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 1.2345m, 10m, null), f.UserId));
    }

    [Fact]
    public async Task CreateAdjustmentAsync_OpeningStockIncrease_CreatesBatchAndUpdatesStockItem()
    {
        var f = await SeedAsync();

        var adjustment = await f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 200, 55m, "Initial stock"),
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
            new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.Damage, AdjustmentDirection.Decrease, 120, null, "Water damage"),
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

        var input = new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.Loss, AdjustmentDirection.Decrease, 51, null, null);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateAdjustmentAsync(input, f.UserId));
    }

    [Fact]
    public async Task CreateAdjustmentAsync_DamageWithIncreaseDirection_Throws()
    {
        var f = await SeedAsync();
        var input = new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.Damage, AdjustmentDirection.Increase, 10, null, null);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateAdjustmentAsync(input, f.UserId));
    }

    [Fact]
    public async Task CreateAdjustmentAsync_OpeningStockWithDecreaseDirection_Throws()
    {
        var f = await SeedAsync();
        var input = new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.OpeningStock, AdjustmentDirection.Decrease, 10, null, null);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateAdjustmentAsync(input, f.UserId));
    }

    [Fact]
    public async Task CreateAdjustmentAsync_CorrectionEitherDirection_Succeeds()
    {
        var f = await SeedAsync();
        await SeedOpeningStockAsync(f, 50, 50m);

        await f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.Correction, AdjustmentDirection.Decrease, 5, null, "Recount"), f.UserId);
        await f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.Correction, AdjustmentDirection.Increase, 3, 50m, "Recount"), f.UserId);

        var stockItem = await f.Db.StockItems.SingleAsync(s => s.ProductId == f.Product.Id);
        Assert.Equal(48m, stockItem.QuantityOnHand);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_IncreaseWithoutUnitCost_Throws()
    {
        var f = await SeedAsync();
        var input = new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 10, null, null);

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateAdjustmentAsync(input, f.UserId));
    }

    [Fact]
    public async Task CreateAdjustmentAsync_WritesStockMovementWithCorrectSign()
    {
        var f = await SeedAsync();
        await SeedOpeningStockAsync(f, 100, 50m);

        await f.Service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(f.Product.Id, f.BaseUnitId, AdjustmentType.Expiry, AdjustmentDirection.Decrease, 10, null, "Expired"), f.UserId);

        var movements = await f.Db.StockMovements.Where(m => m.ProductId == f.Product.Id).OrderBy(m => m.OccurredAt).ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Equal(100m, movements[0].QuantityBaseUnits);
        Assert.Equal(-10m, movements[1].QuantityBaseUnits);
        Assert.Equal(StockMovementType.OpeningStock, movements[0].Type);
        Assert.Equal(StockMovementType.Adjustment, movements[1].Type);
    }

    // ------------------------------------------------------------------ Phase 3: adjustments entered in product units

    /// <summary>Ball Pen (pc base; Box 10, Half Carton 50, Full Carton 100) and Ponni Rice (kg base, 3 dp), no stock.</summary>
    private sealed record PenFixture(
        SelloraDbContext Db, StockAdjustmentService Service, Guid UserId,
        Product Pen, ProductUnit Pc, ProductUnit Box, ProductUnit HCtn, ProductUnit FCtn,
        Product Rice, ProductUnit Kg);

    private static async Task<PenFixture> SeedPenAsync(Action<Product, Unit>? extraPenUnits = null)
    {
        var db = NewDb();
        var user = TestUsers.Admin();
        var (pcUnit, boxUnit, hctnUnit, fctnUnit, kgUnit) = (TestUnits.Piece(), TestUnits.Box(), TestUnits.HalfCarton(), TestUnits.FullCarton(), TestUnits.Kilogram());
        ProductUnit pc = null!, box = null!, hctn = null!, fctn = null!, kg = null!;
        var pen = new ProductBuilder().WithSku("STN-PEN").WithName("Ball Pen").WithBaseUnit(pcUnit)
            .WithUnits(p =>
            {
                pc = TestProductUnits.Base(p);
                box = TestProductUnits.Pack(p, boxUnit, pc, 10m);
                hctn = TestProductUnits.Pack(p, hctnUnit, box, 5m);
                fctn = TestProductUnits.Pack(p, fctnUnit, hctn, 2m);
                extraPenUnits?.Invoke(p, boxUnit);
            })
            .Build();
        var rice = new ProductBuilder().WithSku("RICE").WithName("Ponni Rice").WithBaseUnit(kgUnit).WithTax(pen.Tax)
            .WithUnits(p => kg = TestProductUnits.Base(p))
            .Build();
        db.AddRange(user.Role, user, pcUnit, boxUnit, hctnUnit, fctnUnit, kgUnit, pen.Tax, pen, rice);
        await db.SaveChangesAsync();
        return new PenFixture(db, new StockAdjustmentService(db), user.Id, pen, pc, box, hctn, fctn, rice, kg);
    }

    private static Task<StockAdjustment> AdjustAsync(PenFixture f, ProductUnit unit, AdjustmentDirection direction, decimal qty, decimal? cost = null,
        AdjustmentType? type = null, string? key = null) =>
        f.Service.CreateAdjustmentAsync(new CreateAdjustmentInput(unit.ProductId, unit.Id,
            type ?? (direction == AdjustmentDirection.Increase ? AdjustmentType.OpeningStock : AdjustmentType.Damage), direction, qty, cost, null, key), f.UserId);

    [Fact]
    public async Task CreateAdjustmentAsync_FractionalFactorBaseQtyBeyondBaseUnitDecimals_IsRejectedWithBaseExplanation()
    {
        // Base kg tracked to 2 dp; a 0.5 kg pack entered to 2 dp. 0.01 pack is a valid entry but 0.005 kg is not.
        var db = NewDb();
        var user = TestUsers.Admin();
        var kg = TestUnits.New("Kilogram", "kg", MeasurementCategory.Weight, 2, 1m);
        var packUnit = TestUnits.New("Half kg pack", "hpk", MeasurementCategory.Packaging, 2, null);
        ProductUnit pack = null!;
        var product = new ProductBuilder().WithSku("SUGAR").WithName("Sugar").WithBaseUnit(kg)
            .WithUnits(p => pack = TestProductUnits.Pack(p, packUnit, TestProductUnits.Base(p), 0.5m))
            .Build();
        db.AddRange(user.Role, user, kg, packUnit, product.Tax, product);
        await db.SaveChangesAsync();
        var service = new StockAdjustmentService(db);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(product.Id, pack.Id, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 0.01m, 5m, null), user.Id));

        Assert.Equal("Quantity", ex.Field);
        Assert.Equal("0.01 hpk = 0.005 kg; Kilogram is tracked to 2 decimal place(s).", ex.Message);
        Assert.Empty(await db.StockAdjustments.AsNoTracking().ToListAsync());

        var ok = await service.CreateAdjustmentAsync(
            new CreateAdjustmentInput(product.Id, pack.Id, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 0.02m, 5m, null), user.Id);
        Assert.Equal(0.01m, ok.QuantityBaseUnits);
    }

    private static async Task<DomainValidationException> AssertRejectedAsync(Func<Task> act, string field, string? message = null)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(act);
        Assert.Equal(field, ex.Field);
        if (message is not null) Assert.Equal(message, ex.Message);
        return ex;
    }

    /// <summary>§8 a: the phase acceptance scenario.</summary>
    [Fact]
    public async Task CreateAdjustmentAsync_OpeningStockThreeFullCartons_SnapshotsEnteredUnitAndStoresThreeHundredPieces()
    {
        var f = await SeedPenAsync();

        var adjustment = await AdjustAsync(f, f.FCtn, AdjustmentDirection.Increase, 3m, 250m);

        var stored = await f.Db.StockAdjustments.AsNoTracking().SingleAsync();
        Assert.Equal((3m, f.FCtn.Id, f.FCtn.UnitId, "fctn", 100m, 300m), (stored.EnteredQuantity, stored.ProductUnitId, stored.UnitId, stored.UnitCode, stored.ConversionFactorToBase, stored.QuantityBaseUnits));
        Assert.Equal(((decimal?)250m, (decimal?)2.5m), (stored.EnteredUnitCost, stored.UnitCost));
        Assert.Equal((0m, 300m), (stored.BeforeQty, stored.AfterQty));

        var batch = await f.Db.PurchaseBatches.AsNoTracking().SingleAsync();
        Assert.Equal((adjustment.Id, 300m, 300m, 2.5m), (batch.StockAdjustmentId!.Value, batch.PurchasedBaseQty, batch.RemainingBaseQty, batch.UnitCost));

        var stock = await f.Db.StockItems.AsNoTracking().SingleAsync();
        Assert.Equal((f.Pen.Id, 300m, 2.5m), (stock.ProductId, stock.QuantityOnHand, stock.AverageCost));

        var movement = await f.Db.StockMovements.AsNoTracking().SingleAsync();
        Assert.Equal((300m, (Guid?)batch.Id, StockMovementType.OpeningStock, adjustment.Id, "StockAdjustment"),
            (movement.QuantityBaseUnits, movement.PurchaseBatchId, movement.Type, movement.ReferenceId, movement.ReferenceType));
    }

    /// <summary>§8 b: 2 Box = 20 pc drawn FIFO from a 15 pc batch then a 100 pc batch.</summary>
    [Fact]
    public async Task CreateAdjustmentAsync_DecreaseTwoBoxesOverBatchesOf15And100_OneMovementPerBatchAndQoh95()
    {
        var f = await SeedPenAsync();
        await AdjustAsync(f, f.Pc, AdjustmentDirection.Increase, 15m, 2m);   // older batch: 15 pc
        await AdjustAsync(f, f.FCtn, AdjustmentDirection.Increase, 1m, 200m); // newer batch: 100 pc

        var decrease = await AdjustAsync(f, f.Box, AdjustmentDirection.Decrease, 2m);

        Assert.Equal((2m, "box", 10m, 20m, (decimal?)null, (decimal?)null),
            (decrease.EnteredQuantity, decrease.UnitCode, decrease.ConversionFactorToBase, decrease.QuantityBaseUnits, decrease.UnitCost, decrease.EnteredUnitCost));
        var batches = await f.Db.PurchaseBatches.AsNoTracking().OrderBy(b => b.CreatedAt).ToListAsync();
        Assert.Equal(new[] { 0m, 95m }, batches.Select(b => b.RemainingBaseQty));
        var removals = await f.Db.StockMovements.AsNoTracking().Where(m => m.ReferenceId == decrease.Id).ToListAsync();
        Assert.Equal(2, removals.Count);
        Assert.Equal(-15m, removals.Single(m => m.PurchaseBatchId == batches[0].Id).QuantityBaseUnits);
        Assert.Equal(-5m, removals.Single(m => m.PurchaseBatchId == batches[1].Id).QuantityBaseUnits);
        Assert.Equal((95m, 115m, 95m), ((await f.Db.StockItems.AsNoTracking().SingleAsync()).QuantityOnHand, decrease.BeforeQty, decrease.AfterQty));
    }

    /// <summary>§8 c: entered decimals follow the entered unit's DecimalPlaces.</summary>
    [Fact]
    public async Task CreateAdjustmentAsync_FractionalBox_RejectedOnQuantityAndNothingWritten()
    {
        var f = await SeedPenAsync();

        await AssertRejectedAsync(() => AdjustAsync(f, f.Box, AdjustmentDirection.Increase, 1.5m, 10m), "Quantity", "Box is counted in whole numbers.");

        Assert.Empty(await f.Db.StockAdjustments.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.PurchaseBatches.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateAdjustmentAsync_FractionalKgOnKgBase_Accepted()
    {
        var f = await SeedPenAsync();

        var adjustment = await AdjustAsync(f, f.Kg, AdjustmentDirection.Increase, 1.5m, 60m);

        Assert.Equal((1.5m, 1m, 1.5m, "kg"), (adjustment.EnteredQuantity, adjustment.ConversionFactorToBase, adjustment.QuantityBaseUnits, adjustment.UnitCode));
        Assert.Equal(1.5m, (await f.Db.StockItems.AsNoTracking().SingleAsync(s => s.ProductId == f.Rice.Id)).QuantityOnHand);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_FourDecimalKg_RejectedOnQuantity()
    {
        var f = await SeedPenAsync();

        await AssertRejectedAsync(() => AdjustAsync(f, f.Kg, AdjustmentDirection.Increase, 1.2345m, 60m), "Quantity", "Kilogram allows at most 3 decimal place(s).");
    }

    /// <summary>§8 d: the entered unit must be an active row of the same product.</summary>
    [Fact]
    public async Task CreateAdjustmentAsync_UnitOfAnotherProduct_RejectedOnProductUnitId()
    {
        var f = await SeedPenAsync();

        await AssertRejectedAsync(() => f.Service.CreateAdjustmentAsync(new CreateAdjustmentInput(f.Pen.Id, f.Kg.Id,
            AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 1m, 10m, null), f.UserId), "ProductUnitId", "This unit does not belong to this product.");
    }

    [Fact]
    public async Task CreateAdjustmentAsync_InactiveUnit_RejectedOnProductUnitId()
    {
        var f = await SeedPenAsync();
        f.Box.IsActive = false;
        await f.Db.SaveChangesAsync();

        await AssertRejectedAsync(() => AdjustAsync(f, f.Box, AdjustmentDirection.Increase, 1m, 10m), "ProductUnitId", "Box is no longer active.");
    }

    [Fact]
    public async Task CreateAdjustmentAsync_UnknownUnit_RejectedOnProductUnitId()
    {
        var f = await SeedPenAsync();

        await AssertRejectedAsync(() => f.Service.CreateAdjustmentAsync(new CreateAdjustmentInput(f.Pen.Id, Guid.NewGuid(),
            AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 1m, 10m, null), f.UserId), "ProductUnitId", "Select a valid unit.");
    }

    [Fact]
    public async Task CreateAdjustmentAsync_UnknownProduct_RejectedOnProductId()
    {
        var f = await SeedPenAsync();

        await AssertRejectedAsync(() => f.Service.CreateAdjustmentAsync(new CreateAdjustmentInput(Guid.NewGuid(), f.Box.Id,
            AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 1m, 10m, null), f.UserId), "ProductId");
        Assert.Empty(await f.Db.StockItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateAdjustmentAsync_ActiveUnitNeitherPurchasableNorSellable_Accepted()
    {
        var f = await SeedPenAsync();
        f.HCtn.IsPurchasable = false;
        f.HCtn.IsSellable = false;
        await f.Db.SaveChangesAsync();

        var adjustment = await AdjustAsync(f, f.HCtn, AdjustmentDirection.Increase, 2m, 100m);

        Assert.Equal((f.HCtn.Id, 100m), (adjustment.ProductUnitId, adjustment.QuantityBaseUnits));
    }

    [Fact]
    public async Task CreateAdjustmentAsync_InactiveProductActiveUnit_StillAdjustable()
    {
        var f = await SeedPenAsync();
        await AdjustAsync(f, f.Box, AdjustmentDirection.Increase, 3m, 30m);
        f.Pen.IsActive = false;
        await f.Db.SaveChangesAsync();

        var writeOff = await AdjustAsync(f, f.Box, AdjustmentDirection.Decrease, 3m, type: AdjustmentType.Expiry);

        Assert.Equal(0m, writeOff.AfterQty);
    }

    /// <summary>§8 e: AverageCost is weighted over base quantities whatever unit each increase was entered in.</summary>
    [Fact]
    public async Task CreateAdjustmentAsync_BaseThenBoxIncreases_AverageCostWeightedPerBaseUnit()
    {
        var f = await SeedPenAsync();
        await AdjustAsync(f, f.Pc, AdjustmentDirection.Increase, 100m, 2m);  // 100 pc @ 2
        await AdjustAsync(f, f.Box, AdjustmentDirection.Increase, 5m, 30m);  // 50 pc @ 3 (30 per box)

        var stock = await f.Db.StockItems.AsNoTracking().SingleAsync();
        Assert.Equal(150m, stock.QuantityOnHand);
        Assert.Equal(StockCost.PerBaseUnit((100m * 2m + 50m * 3m) / 150m), stock.AverageCost);
        Assert.Equal(new[] { 2m, 3m }, (await f.Db.PurchaseBatches.AsNoTracking().OrderBy(b => b.CreatedAt).ToListAsync()).Select(b => b.UnitCost));
    }

    /// <summary>§8 f: cost per base unit is rounded explicitly to 4 dp and the same value feeds batch, adjustment and average.</summary>
    [Fact]
    public async Task CreateAdjustmentAsync_BoxOfTwelveAt100_PerBaseCostRoundedTo6dpEverywhere()
    {
        var dozenBox = Guid.Empty;
        var f = await SeedPenAsync((p, _) =>
        {
            var crate = TestUnits.Case();
            dozenBox = TestProductUnits.Pack(p, crate, p.Units.Single(u => u.IsBaseUnit), 12m).Id;
        });
        var box12 = await f.Db.ProductUnits.SingleAsync(u => u.Id == dozenBox);

        var adjustment = await AdjustAsync(f, box12, AdjustmentDirection.Increase, 1m, 100m);

        var stored = await f.Db.StockAdjustments.AsNoTracking().SingleAsync();
        Assert.Equal(((decimal?)100m, (decimal?)8.333333m, 12m, 12m), (stored.EnteredUnitCost, stored.UnitCost, stored.ConversionFactorToBase, stored.QuantityBaseUnits));
        Assert.Equal(8.333333m, (await f.Db.PurchaseBatches.AsNoTracking().SingleAsync()).UnitCost);
        Assert.Equal(8.333333m, (await f.Db.StockItems.AsNoTracking().SingleAsync()).AverageCost);
        Assert.Equal(adjustment.Id, stored.Id);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_EnteredCostWithMoreThan4dp_RoundedAwayFromZeroBeforeConversion()
    {
        var f = await SeedPenAsync();

        var adjustment = await AdjustAsync(f, f.Box, AdjustmentDirection.Increase, 1m, 25.00005m);

        Assert.Equal(((decimal?)25.0001m, (decimal?)2.50001m), (adjustment.EnteredUnitCost, adjustment.UnitCost));
    }

    [Fact]
    public async Task CreateAdjustmentAsync_DecreaseWithCost_CostIgnoredAndBothCostsNull()
    {
        var f = await SeedPenAsync();
        await AdjustAsync(f, f.Box, AdjustmentDirection.Increase, 2m, 30m);

        var decrease = await AdjustAsync(f, f.Box, AdjustmentDirection.Decrease, 1m, 999m);

        Assert.Equal(((decimal?)null, (decimal?)null), (decrease.EnteredUnitCost, decrease.UnitCost));
        Assert.Equal(3m, (await f.Db.StockItems.AsNoTracking().SingleAsync()).AverageCost);
    }

    /// <summary>§8 g: the message states both the entered and the base quantity.</summary>
    [Fact]
    public async Task CreateAdjustmentAsync_DecreaseInPacksBeyondStock_MessageStatesEnteredAndBaseQuantities()
    {
        var f = await SeedPenAsync();
        await AdjustAsync(f, f.Pc, AdjustmentDirection.Increase, 50m, 2m);

        await AssertRejectedAsync(() => AdjustAsync(f, f.FCtn, AdjustmentDirection.Decrease, 1m), "Quantity",
            "Cannot remove 1 fctn (100 pc) — only 50 pc is in stock.");

        Assert.Equal(50m, (await f.Db.PurchaseBatches.AsNoTracking().SingleAsync()).RemainingBaseQty);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_BatchesShortOfQohInPacks_RejectedOnQuantityWithShortage()
    {
        var f = await SeedPenAsync();
        await AdjustAsync(f, f.Box, AdjustmentDirection.Increase, 3m, 30m); // 30 pc in one batch
        var stock = await f.Db.StockItems.SingleAsync();
        stock.QuantityOnHand = 60m; // drift: QOH claims more than the batches hold
        await f.Db.SaveChangesAsync();

        await AssertRejectedAsync(() => AdjustAsync(f, f.HCtn, AdjustmentDirection.Decrease, 1m), "Quantity",
            "Batch stock is short by 20 — cannot remove 50 pc.");

        Assert.Equal(30m, (await f.Db.PurchaseBatches.AsNoTracking().SingleAsync()).RemainingBaseQty);
    }

    /// <summary>§8 h: snapshots are history — replacing Box(10) by Box(12) does not change a posted adjustment.</summary>
    [Fact]
    public async Task CreateAdjustmentAsync_BoxReplacedByBoxOfTwelveAfterPosting_PostedAdjustmentKeepsFactor10AndCode()
    {
        var f = await SeedPenAsync();
        var first = await AdjustAsync(f, f.Box, AdjustmentDirection.Increase, 2m, 30m);

        f.Box.IsActive = false;
        var box12 = new ProductUnit
        {
            Id = Guid.NewGuid(), ProductId = f.Pen.Id, UnitId = f.Box.UnitId, InnerProductUnitId = f.Pc.Id, InnerQuantity = 12m,
            ConversionFactorToBase = 12m, IsBaseUnit = false, SortOrder = 4,
        };
        f.Db.ProductUnits.Add(box12);
        await f.Db.SaveChangesAsync();
        var second = await AdjustAsync(f, box12, AdjustmentDirection.Increase, 1m, 36m);

        f.Db.ChangeTracker.Clear();
        var stored = await f.Db.StockAdjustments.AsNoTracking().ToDictionaryAsync(a => a.Id);
        Assert.Equal((f.Box.Id, "box", 10m, 2m, 20m), (stored[first.Id].ProductUnitId, stored[first.Id].UnitCode, stored[first.Id].ConversionFactorToBase, stored[first.Id].EnteredQuantity, stored[first.Id].QuantityBaseUnits));
        Assert.Equal((box12.Id, "box", 12m, 1m, 12m), (stored[second.Id].ProductUnitId, stored[second.Id].UnitCode, stored[second.Id].ConversionFactorToBase, stored[second.Id].EnteredQuantity, stored[second.Id].QuantityBaseUnits));
        Assert.Equal(32m, (await f.Db.StockItems.AsNoTracking().SingleAsync()).QuantityOnHand);
        await AssertRejectedAsync(() => AdjustAsync(f, f.Box, AdjustmentDirection.Increase, 1m, 30m), "ProductUnitId", "Box is no longer active.");
    }

    /// <summary>§8 i: the first stock-in creates the product's StockItem (InMemory path of EnsureAndLockStockItemsAsync).</summary>
    [Fact]
    public async Task CreateAdjustmentAsync_ProductWithoutStockItem_CreatesIt()
    {
        var f = await SeedPenAsync();
        Assert.Empty(await f.Db.StockItems.AsNoTracking().ToListAsync());

        await AdjustAsync(f, f.HCtn, AdjustmentDirection.Increase, 1m, 100m);

        var stock = await f.Db.StockItems.AsNoTracking().SingleAsync();
        Assert.Equal((f.Pen.Id, 50m, 2m), (stock.ProductId, stock.QuantityOnHand, stock.AverageCost));
    }

    [Fact]
    public async Task CreateAdjustmentAsync_RejectedDecreaseOnProductWithoutStockItem_PersistsNoStockItem()
    {
        var f = await SeedPenAsync();

        await AssertRejectedAsync(() => AdjustAsync(f, f.Box, AdjustmentDirection.Decrease, 1m), "Quantity", "Cannot remove 1 box (10 pc) — only 0 pc is in stock.");

        Assert.Empty(await f.Db.StockItems.AsNoTracking().ToListAsync());
    }

    /// <summary>§8 j.</summary>
    [Fact]
    public async Task CreateAdjustmentAsync_ReplayInPacksWithSameKey_PostsOnceAndReturnsTheOriginal()
    {
        var f = await SeedPenAsync();

        var first = await AdjustAsync(f, f.FCtn, AdjustmentDirection.Increase, 2m, 250m, key: "opening-pen");
        var replay = await AdjustAsync(f, f.FCtn, AdjustmentDirection.Increase, 2m, 250m, key: "opening-pen");

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal((2m, "fctn", 200m), (replay.EnteredQuantity, replay.UnitCode, replay.QuantityBaseUnits));
        Assert.Single(await f.Db.StockAdjustments.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.PurchaseBatches.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.StockMovements.AsNoTracking().ToListAsync());
        Assert.Equal(200m, (await f.Db.StockItems.AsNoTracking().SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task CreateAdjustmentAsync_MixedUnitsSequence_LedgerBatchesAndQohReconcile()
    {
        var f = await SeedPenAsync();
        await AdjustAsync(f, f.FCtn, AdjustmentDirection.Increase, 1m, 250m);   // +100
        await AdjustAsync(f, f.Box, AdjustmentDirection.Increase, 3m, 30m);     // +30
        await AdjustAsync(f, f.HCtn, AdjustmentDirection.Decrease, 2m);         // -100 (draws all of batch 1)
        await AdjustAsync(f, f.Pc, AdjustmentDirection.Decrease, 7m, type: AdjustmentType.Loss); // -7

        var qoh = (await f.Db.StockItems.AsNoTracking().SingleAsync()).QuantityOnHand;
        Assert.Equal(23m, qoh);
        Assert.Equal(qoh, await f.Db.StockMovements.AsNoTracking().SumAsync(m => m.QuantityBaseUnits));
        Assert.Equal(qoh, await f.Db.PurchaseBatches.AsNoTracking().SumAsync(b => b.RemainingBaseQty));
        Assert.All(await f.Db.StockAdjustments.AsNoTracking().ToListAsync(), a => Assert.Equal(a.QuantityBaseUnits, a.EnteredQuantity * a.ConversionFactorToBase));
    }
}
