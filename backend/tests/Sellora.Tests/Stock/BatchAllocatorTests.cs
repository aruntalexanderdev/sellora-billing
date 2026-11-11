using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>
/// <see cref="BatchAllocator.ConsumeFifoAsync"/>, the one FIFO implementation shared by sales and adjustment decreases
/// (Phase 3 design §4): oldest-first by CreatedAt then BatchSequence, plan-then-apply (a shortage throws the caller's
/// exception before any batch is touched), batches drained earlier in the same unit of work are skipped, and every
/// allocation carries its batch's cost per base unit.
/// </summary>
public class BatchAllocatorTests
{
    private static readonly Guid Loc = Sellora.Domain.Companies.DefaultTenant.MainStoreLocationId;
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(SelloraDbContext Db, Product Pen, Product Other);

    private static async Task<Fixture> SeedAsync()
    {
        var db = TestDb.NewInMemory();
        var pc = TestUnits.Piece();
        var pen = new ProductBuilder().WithSku("STN-PEN").WithName("Ball Pen").WithBaseUnit(pc).WithBaseUnitRow().Build();
        var other = new ProductBuilder().WithSku("OTHER").WithBaseUnit(pc).WithTax(pen.Tax).WithBaseUnitRow().Build();
        db.AddRange(pc, pen.Tax, pen, other);
        await db.SaveChangesAsync();
        return new Fixture(db, pen, other);
    }

    private static PurchaseBatch Batch(Fixture f, decimal remaining, decimal unitCost, DateTimeOffset createdAt, long sequence, Product? product = null) =>
        new()
        {
            Id = Guid.NewGuid(), ProductId = (product ?? f.Pen).Id, BatchSequence = sequence, ReceivedDate = DateOnly.FromDateTime(createdAt.UtcDateTime),
            PurchasedBaseQty = Math.Max(remaining, 1m), RemainingBaseQty = remaining, UnitCost = unitCost, CreatedAt = createdAt,
        };

    private static async Task<List<PurchaseBatch>> AddAsync(Fixture f, params PurchaseBatch[] batches)
    {
        foreach (var batch in batches)
        {
            batch.OriginStockLocationId = Loc;
            f.Db.BatchLocationBalances.Add(new BatchLocationBalance { PurchaseBatchId = batch.Id, StockLocationId = Loc, ProductId = batch.ProductId, RemainingBaseQty = batch.RemainingBaseQty });
        }
        f.Db.PurchaseBatches.AddRange(batches);
        await f.Db.SaveChangesAsync();
        return batches.ToList();
    }

    private static Func<decimal, DomainValidationException> Shortage(List<decimal> seen) =>
        shortBy => { seen.Add(shortBy); return new DomainValidationException("Quantity", $"short by {shortBy}"); };

    private static Task<IReadOnlyList<BatchAllocator.Allocation>> ConsumeAsync(Fixture f, decimal qty, List<decimal>? seen = null) =>
        BatchAllocator.ConsumeFifoAsync(f.Db, Loc, f.Pen.Id, qty, Shortage(seen ?? []), default);

    [Fact]
    public async Task ConsumeFifoAsync_BatchesInsertedOutOfOrder_DrawsByCreatedAtThenBatchSequence()
    {
        var f = await SeedAsync();
        // Inserted newest first; two share a CreatedAt and are ordered by BatchSequence.
        var b = await AddAsync(f,
            Batch(f, 10m, 9m, T0.AddDays(2), 1),
            Batch(f, 10m, 7m, T0, 5),
            Batch(f, 10m, 6m, T0, 4),
            Batch(f, 10m, 8m, T0.AddDays(1), 2));

        var allocations = await ConsumeAsync(f, 35m);

        Assert.Equal(new[] { b[2].Id, b[1].Id, b[3].Id, b[0].Id }, allocations.Select(a => a.Batch.Id));
        Assert.Equal(new[] { 10m, 10m, 10m, 5m }, allocations.Select(a => a.QtyBaseUnits));
        Assert.Equal(new[] { 0m, 0m, 0m, 5m }, new[] { b[2], b[1], b[3], b[0] }.Select(x => x.RemainingBaseQty));
    }

    [Fact]
    public async Task ConsumeFifoAsync_ExactlyAllStock_DrainsEveryBatchAndNoShortage()
    {
        var f = await SeedAsync();
        var b = await AddAsync(f, Batch(f, 15m, 2m, T0, 1), Batch(f, 100m, 2.5m, T0.AddHours(1), 2));
        var seen = new List<decimal>();

        var allocations = await ConsumeAsync(f, 115m, seen);

        Assert.Empty(seen);
        Assert.Equal(115m, allocations.Sum(a => a.QtyBaseUnits));
        Assert.All(b, x => Assert.Equal(0m, x.RemainingBaseQty));
    }

    [Fact]
    public async Task ConsumeFifoAsync_SingleBatchCoversRequest_OneAllocationOnly()
    {
        var f = await SeedAsync();
        var b = await AddAsync(f, Batch(f, 100m, 2.5m, T0, 1), Batch(f, 100m, 3m, T0.AddHours(1), 2));

        var allocations = await ConsumeAsync(f, 20m);

        var only = Assert.Single(allocations);
        Assert.Equal((b[0].Id, 20m), (only.Batch.Id, only.QtyBaseUnits));
        Assert.Equal((80m, 100m), (b[0].RemainingBaseQty, b[1].RemainingBaseQty));
    }

    [Fact]
    public async Task ConsumeFifoAsync_Shortage_ThrowsCallersExceptionWithShortByAndMutatesNothing()
    {
        var f = await SeedAsync();
        var b = await AddAsync(f, Batch(f, 15m, 2m, T0, 1), Batch(f, 10m, 2.5m, T0.AddHours(1), 2));
        var seen = new List<decimal>();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => ConsumeAsync(f, 30.5m, seen));

        Assert.Equal(("Quantity", "short by 5.5"), (ex.Field, ex.Message));
        Assert.Equal(new[] { 5.5m }, seen);
        // The tracked instances (what a later SaveChanges would write) are untouched, not only the stored rows.
        Assert.Equal((15m, 10m), (b[0].RemainingBaseQty, b[1].RemainingBaseQty));
        Assert.All(f.Db.ChangeTracker.Entries<PurchaseBatch>(), e => Assert.Equal(EntityState.Unchanged, e.State));
    }

    [Fact]
    public async Task ConsumeFifoAsync_NoBatchesAtAll_ShortByTheWholeQuantity()
    {
        var f = await SeedAsync();
        var seen = new List<decimal>();

        await Assert.ThrowsAsync<DomainValidationException>(() => ConsumeAsync(f, 3m, seen));

        Assert.Equal(new[] { 3m }, seen);
    }

    /// <summary>Line 1 of a document drains B1 in memory (unsaved); the stored row still says 10, so the DB filter returns
    /// it — the allocator must skip it on the tracked value instead of writing a zero-quantity draw.</summary>
    [Fact]
    public async Task ConsumeFifoAsync_BatchDrainedEarlierInSameContext_SkippedNoZeroAllocation()
    {
        var f = await SeedAsync();
        var b = await AddAsync(f, Batch(f, 10m, 2m, T0, 1), Batch(f, 10m, 3m, T0.AddHours(1), 2));

        var line1 = await ConsumeAsync(f, 10m);
        var line2 = await ConsumeAsync(f, 4m);

        Assert.Equal((b[0].Id, 10m), (Assert.Single(line1).Batch.Id, line1[0].QtyBaseUnits));
        var draw = Assert.Single(line2);
        Assert.Equal((b[1].Id, 4m), (draw.Batch.Id, draw.QtyBaseUnits));
        Assert.DoesNotContain(line1.Concat(line2), a => a.QtyBaseUnits <= 0);
        Assert.Equal((0m, 6m), (b[0].RemainingBaseQty, b[1].RemainingBaseQty));
    }

    [Fact]
    public async Task ConsumeFifoAsync_DrainedEarlierInSameContextAndRestShort_ShortageCountsOnlyTrackedRemainders()
    {
        var f = await SeedAsync();
        var b = await AddAsync(f, Batch(f, 10m, 2m, T0, 1), Batch(f, 10m, 3m, T0.AddHours(1), 2));
        await ConsumeAsync(f, 10m);
        var seen = new List<decimal>();

        await Assert.ThrowsAsync<DomainValidationException>(() => ConsumeAsync(f, 12m, seen));

        Assert.Equal(new[] { 2m }, seen);
        Assert.Equal((0m, 10m), (b[0].RemainingBaseQty, b[1].RemainingBaseQty));
    }

    [Fact]
    public async Task ConsumeFifoAsync_AllocationsCarryEachBatchsUnitCost()
    {
        var f = await SeedAsync();
        await AddAsync(f, Batch(f, 15m, 2m, T0, 1), Batch(f, 100m, 8.3333m, T0.AddHours(1), 2));

        var allocations = await ConsumeAsync(f, 20m);

        Assert.Equal(new[] { (15m, 2m), (5m, 8.3333m) }, allocations.Select(a => (a.QtyBaseUnits, a.UnitCost)));
        Assert.Equal(15m * 2m + 5m * 8.3333m, allocations.Sum(a => a.QtyBaseUnits * a.UnitCost));
    }

    [Fact]
    public async Task ConsumeFifoAsync_OtherProductsAndEmptyBatches_Ignored()
    {
        var f = await SeedAsync();
        var b = await AddAsync(f,
            Batch(f, 50m, 1m, T0.AddDays(-1), 1, f.Other), // older, but another product
            Batch(f, 0m, 1m, T0.AddDays(-1), 2),           // older, but empty
            Batch(f, 10m, 2m, T0, 3));

        var allocations = await ConsumeAsync(f, 10m);

        Assert.Equal(b[2].Id, Assert.Single(allocations).Batch.Id);
        Assert.Equal(50m, b[0].RemainingBaseQty);
    }
}
