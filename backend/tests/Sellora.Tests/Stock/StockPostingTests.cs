using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>
/// <see cref="StockPosting"/> (Phase 8, D5): the one place stock quantity, moving-average cost, batch remainders and the
/// per-location <see cref="BatchLocationBalance"/> rows change. Every operation keeps the balance and the batch's dual-written
/// total in step (ADR-12) and works on one (location, product) StockItem; another location's stock is never touched.
/// </summary>
public class StockPostingTests
{
    private static readonly Guid L1 = DefaultTenant.MainStoreLocationId;
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(SelloraDbContext Db, Product Product, Guid L2, StockItem Item1, StockItem Item2);

    private static async Task<Fixture> SeedAsync()
    {
        var db = TestDb.NewInMemory();
        var pc = TestUnits.Piece();
        var product = new ProductBuilder().WithSku("PEN").WithBaseUnit(pc).WithBaseUnitRow().Build();
        var branch = BranchBuilder.Extra("BLR");
        var store2 = BranchBuilder.Store(branch);
        var item1 = new StockItem { StockLocationId = L1, ProductId = product.Id };
        var item2 = new StockItem { StockLocationId = store2.Id, ProductId = product.Id };
        db.AddRange(pc, product.Tax, product, branch, store2, item1, item2);
        await db.SaveChangesAsync();
        return new Fixture(db, product, store2.Id, item1, item2);
    }

    private static PurchaseBatch NewBatch(Fixture f, decimal qty, decimal cost, int day) => new()
    {
        Id = Guid.NewGuid(), ProductId = f.Product.Id, PurchasedBaseQty = qty, UnitCost = cost, CreatedAt = T0.AddDays(day), BatchSequence = day + 1,
        ReceivedDate = DateOnly.FromDateTime(T0.AddDays(day).UtcDateTime),
    };

    private static PurchaseBatch Receive(Fixture f, StockItem item, decimal qty, decimal cost, int day)
    {
        var batch = NewBatch(f, qty, cost, day);
        f.Db.PurchaseBatches.Add(batch);
        StockPosting.Receive(f.Db, item, batch, qty, cost);
        return batch;
    }

    private static Func<decimal, DomainValidationException> Short() => by => new DomainValidationException("Quantity", $"short by {by}");

    private static decimal Balance(Fixture f, PurchaseBatch batch, Guid location) =>
        f.Db.BatchLocationBalances.Local.SingleOrDefault(b => b.PurchaseBatchId == batch.Id && b.StockLocationId == location)?.RemainingBaseQty ?? 0m;

    [Fact]
    public async Task Receive_TwoBatches_MovingAverageBalancesOriginAndRemainingFollow()
    {
        var f = await SeedAsync();

        var b1 = Receive(f, f.Item1, 10m, 2m, 0);
        var b2 = Receive(f, f.Item1, 10m, 4m, 1);

        Assert.Equal((20m, 3m), (f.Item1.QuantityOnHand, f.Item1.AverageCost));
        Assert.Equal((10m, 10m), (b1.RemainingBaseQty, b2.RemainingBaseQty));
        Assert.Equal((10m, 10m), (Balance(f, b1, L1), Balance(f, b2, L1)));
        Assert.All(new[] { b1, b2 }, b => Assert.Equal(L1, b.OriginStockLocationId));
        var rows = f.Db.BatchLocationBalances.Local.ToList();
        Assert.All(rows, r => Assert.Equal((L1, f.Product.Id), (r.StockLocationId, r.ProductId)));
    }

    [Fact]
    public async Task Receive_AtASecondLocation_AverageCostIsPerLocation()
    {
        var f = await SeedAsync();
        Receive(f, f.Item1, 10m, 2m, 0);

        var b2 = Receive(f, f.Item2, 5m, 10m, 1);

        Assert.Equal((10m, 2m), (f.Item1.QuantityOnHand, f.Item1.AverageCost));
        Assert.Equal((5m, 10m), (f.Item2.QuantityOnHand, f.Item2.AverageCost));
        Assert.Equal(f.L2, b2.OriginStockLocationId);
    }

    [Fact]
    public async Task IssueFifoAsync_AcrossTwoBatches_DecrementsBalancesAndBatchesAndKeepsAverage()
    {
        var f = await SeedAsync();
        var b1 = Receive(f, f.Item1, 10m, 2m, 0);
        var b2 = Receive(f, f.Item1, 10m, 4m, 1);
        await f.Db.SaveChangesAsync();

        var draws = await StockPosting.IssueFifoAsync(f.Db, f.Item1, 14m, Short(), default);

        Assert.Equal(new[] { (b1.Id, 10m), (b2.Id, 4m) }, draws.Select(d => (d.Batch.Id, d.QtyBaseUnits)));
        Assert.Equal((6m, 3m), (f.Item1.QuantityOnHand, f.Item1.AverageCost));
        Assert.Equal((0m, 6m), (b1.RemainingBaseQty, b2.RemainingBaseQty));
        Assert.Equal((0m, 6m), (Balance(f, b1, L1), Balance(f, b2, L1)));
    }

    [Fact]
    public async Task IssueFifoAsync_OtherLocationHoldsStock_NotConsumedAndShortageRaised()
    {
        var f = await SeedAsync();
        var elsewhere = Receive(f, f.Item2, 50m, 2m, 0);
        await f.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => StockPosting.IssueFifoAsync(f.Db, f.Item1, 5m, Short(), default));

        Assert.Equal("short by 5", ex.Message);
        Assert.Equal(50m, elsewhere.RemainingBaseQty);
        Assert.Equal((0m, 50m), (f.Item1.QuantityOnHand, f.Item2.QuantityOnHand));
    }

    [Fact]
    public async Task IssueFifoAsync_OlderBatchAtAnotherLocation_NotDrawnBeforeANewerBatchHere()
    {
        var f = await SeedAsync();
        var older = Receive(f, f.Item2, 10m, 1m, 0);   // older, but at L2
        var here = Receive(f, f.Item1, 10m, 2m, 1);
        await f.Db.SaveChangesAsync();

        var draws = await StockPosting.IssueFifoAsync(f.Db, f.Item1, 4m, Short(), default);

        Assert.Equal(here.Id, Assert.Single(draws).Batch.Id);
        Assert.Equal((10m, 6m), (older.RemainingBaseQty, here.RemainingBaseQty));
    }

    [Fact]
    public async Task IssueFromBatchAsync_PurchaseReturn_ReducesBalanceBatchAndValueAtTheBatchCost()
    {
        var f = await SeedAsync();
        Receive(f, f.Item1, 10m, 2m, 0);
        var b2 = Receive(f, f.Item1, 10m, 4m, 1);
        await f.Db.SaveChangesAsync();

        await StockPosting.IssueFromBatchAsync(f.Db, f.Item1, b2, 5m, default);

        Assert.Equal(15m, f.Item1.QuantityOnHand);
        Assert.Equal(2.666667m, f.Item1.AverageCost); // (20 + 20) / 15
        Assert.Equal((5m, 5m), (b2.RemainingBaseQty, Balance(f, b2, L1)));
    }

    [Fact]
    public async Task IssueFromBatchAsync_BatchNotAtTheLocation_Throws()
    {
        var f = await SeedAsync();
        var elsewhere = Receive(f, f.Item2, 10m, 2m, 0);
        await f.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => StockPosting.IssueFromBatchAsync(f.Db, f.Item1, elsewhere, 1m, default));
    }

    [Fact]
    public async Task IssueFromBatchAsync_WholeStock_QuantityAndAverageGoToZero()
    {
        var f = await SeedAsync();
        var b = Receive(f, f.Item1, 10m, 2m, 0);
        await f.Db.SaveChangesAsync();

        await StockPosting.IssueFromBatchAsync(f.Db, f.Item1, b, 10m, default);

        Assert.Equal((0m, 0m, 0m), (f.Item1.QuantityOnHand, f.Item1.AverageCost, b.RemainingBaseQty));
    }

    [Fact]
    public async Task RestoreAsync_BatchWasConsumedHere_IncrementsTheExistingBalanceAndQuantityKeepsAverage()
    {
        var f = await SeedAsync();
        var b = Receive(f, f.Item1, 10m, 2m, 0);
        await f.Db.SaveChangesAsync();
        await StockPosting.IssueFifoAsync(f.Db, f.Item1, 10m, Short(), default);

        await StockPosting.RestoreAsync(f.Db, f.Item1, b, 4m, default);

        Assert.Equal((4m, 2m), (f.Item1.QuantityOnHand, f.Item1.AverageCost));
        Assert.Equal((4m, 4m), (b.RemainingBaseQty, Balance(f, b, L1)));
        Assert.Single(f.Db.BatchLocationBalances.Local, x => x.PurchaseBatchId == b.Id);
    }

    [Fact]
    public async Task RestoreAsync_NoBalanceAtTheLocationYet_CreatesTheRowWithProductAndLocation()
    {
        var f = await SeedAsync();
        var b = Receive(f, f.Item1, 10m, 2m, 0);
        await f.Db.SaveChangesAsync();

        await StockPosting.RestoreAsync(f.Db, f.Item2, b, 3m, default);

        var row = f.Db.BatchLocationBalances.Local.Single(x => x.PurchaseBatchId == b.Id && x.StockLocationId == f.L2);
        Assert.Equal((f.Product.Id, 3m), (row.ProductId, row.RemainingBaseQty));
        Assert.Equal(13m, b.RemainingBaseQty); // dual-write: the batch total follows the sum of its balances
        Assert.Equal(3m, f.Item2.QuantityOnHand);
    }

    [Fact]
    public async Task ReverseReceiptAsync_UntouchedBatch_ZeroesBalanceBatchAndValueAndKeepsTheZeroRow()
    {
        var f = await SeedAsync();
        Receive(f, f.Item1, 10m, 2m, 0);
        var b2 = Receive(f, f.Item1, 10m, 4m, 1);
        await f.Db.SaveChangesAsync();

        await StockPosting.ReverseReceiptAsync(f.Db, f.Item1, b2, default);

        Assert.Equal((10m, 2m), (f.Item1.QuantityOnHand, f.Item1.AverageCost));
        Assert.Equal((0m, 0m), (b2.RemainingBaseQty, Balance(f, b2, L1)));
        Assert.Contains(f.Db.BatchLocationBalances.Local, x => x.PurchaseBatchId == b2.Id && x.RemainingBaseQty == 0m);
    }

    [Fact]
    public async Task BalanceAsync_BatchNeverAtTheLocation_IsZero()
    {
        var f = await SeedAsync();
        var b = Receive(f, f.Item1, 10m, 2m, 0);
        await f.Db.SaveChangesAsync();

        Assert.Equal(10m, await StockPosting.BalanceAsync(f.Db, b.Id, L1, default));
        Assert.Equal(0m, await StockPosting.BalanceAsync(f.Db, b.Id, f.L2, default));
    }

    [Fact]
    public async Task EveryOperation_KeepsBatchTotalEqualToSumOfBalances()
    {
        var f = await SeedAsync();
        var b1 = Receive(f, f.Item1, 10m, 2m, 0);
        var b2 = Receive(f, f.Item1, 10m, 4m, 1);
        await f.Db.SaveChangesAsync();
        await StockPosting.IssueFifoAsync(f.Db, f.Item1, 12m, Short(), default);
        await StockPosting.IssueFromBatchAsync(f.Db, f.Item1, b2, 3m, default);
        await StockPosting.RestoreAsync(f.Db, f.Item1, b1, 2m, default);
        await f.Db.SaveChangesAsync();

        foreach (var batch in new[] { b1, b2 })
        {
            var sum = await f.Db.BatchLocationBalances.Where(x => x.PurchaseBatchId == batch.Id).SumAsync(x => x.RemainingBaseQty);
            Assert.Equal(batch.RemainingBaseQty, sum);
        }
        Assert.Equal(f.Item1.QuantityOnHand, await f.Db.BatchLocationBalances.Where(x => x.StockLocationId == L1).SumAsync(x => x.RemainingBaseQty));
    }
}
