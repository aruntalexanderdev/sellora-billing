using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>FI-D7 / FI-D23: the one availability check (available = on hand - reserved), and transfer line quantities above zero (FI-D24).</summary>
public class StockAvailabilityTests
{
    private static readonly Guid Loc = Guid.NewGuid();
    private static readonly Guid ProductA = Guid.NewGuid();
    private static readonly Guid ProductB = Guid.NewGuid();

    private static SelloraDbContext NewDb() => TestCompany.NewDb(new DbContextOptionsBuilder<SelloraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IReadOnlyDictionary<(Guid LocationId, Guid ProductId), StockItem> Items(params (Guid Product, decimal Qoh)[] rows) =>
        rows.ToDictionary(r => (Loc, r.Product), r => new StockItem { StockLocationId = Loc, ProductId = r.Product, QuantityOnHand = r.Qoh });

    private static DomainValidationException Short(StockAvailability.Shortage s) => new("Lines", $"{s.Requested}>{s.Available}");

    [Theory]
    [InlineData(10, 9, true)]
    [InlineData(10, 10, true)]   // exact
    [InlineData(10, 10.001, false)]
    public async Task EnsureAsync_ComparesRequestedWithOnHand(double onHand, double requested, bool fits)
    {
        await using var db = NewDb();
        var act = () => StockAvailability.EnsureAsync(db, Items((ProductA, (decimal)onHand)), [new(Loc, ProductA, (decimal)requested)], Short, default);

        if (fits) await act();
        else Assert.Equal("Lines", (await Assert.ThrowsAsync<DomainValidationException>(act)).Field);
    }

    [Fact]
    public async Task EnsureAsync_SumsRequestsForTheSameProduct_AndMissingRowCountsAsZero()
    {
        await using var db = NewDb();
        var items = Items((ProductA, 10m));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => StockAvailability.EnsureAsync(db, items,
            [new(Loc, ProductA, 6m), new(Loc, ProductA, 5m)], Short, default));
        Assert.Equal("11>10", ex.Message);

        await Assert.ThrowsAsync<DomainValidationException>(() => StockAvailability.EnsureAsync(db, items, [new(Loc, ProductB, 1m)], Short, default));
    }

    [Fact]
    public async Task EnsureAsync_ReportsTheFirstShortProduct()
    {
        await using var db = NewDb();
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => StockAvailability.EnsureAsync(db, Items((ProductA, 5m), (ProductB, 5m)),
            [new(Loc, ProductA, 5m), new(Loc, ProductB, 6m)], s => new DomainValidationException("p", s.ProductId == ProductB ? "B" : "A"), default));
        Assert.Equal("B", ex.Message);
    }

    [Fact]
    public async Task ReservedSource_ReducesAvailability_ForStockItemAndBatchChecks_AndIsScoped()
    {
        await using var db = NewDb();
        var items = Items((ProductA, 10m));

        using (StockAvailability.UseReservedSource((_, _, _, _, _) => Task.FromResult(4m)))
        {
            await StockAvailability.EnsureAsync(db, items, [new(Loc, ProductA, 6m)], Short, default); // 10 - 4 = 6, exact
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() => StockAvailability.EnsureAsync(db, items, [new(Loc, ProductA, 7m)], Short, default));
            Assert.Equal("7>6", ex.Message);
            Assert.Equal(6m, await StockAvailability.AvailableAsync(db, Loc, ProductA, items[(Loc, ProductA)], default));
        }

        await StockAvailability.EnsureAsync(db, items, [new(Loc, ProductA, 10m)], Short, default); // restored: nothing reserved
    }

    [Fact]
    public async Task EnsureInBatchAsync_UsesTheBatchBalanceMinusReserved()
    {
        await using var db = NewDb();
        var batchId = Guid.NewGuid();
        db.BatchLocationBalances.Add(new BatchLocationBalance { PurchaseBatchId = batchId, StockLocationId = Loc, ProductId = ProductA, RemainingBaseQty = 8m });
        await db.SaveChangesAsync();

        await StockAvailability.EnsureInBatchAsync(db, batchId, Loc, ProductA, 8m, a => new("Lines", a.ToString()), default);
        using (StockAvailability.UseReservedSource((_, _, _, batch, _) => Task.FromResult(batch == batchId ? 3m : 0m)))
        {
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
                StockAvailability.EnsureInBatchAsync(db, batchId, Loc, ProductA, 6m, a => new("Lines", a.ToString()), default));
            Assert.Equal("5", ex.Message);
        }
    }

    // ---- FI-D30: the real source is StockItem.ReservedQuantity ---------------------------------------------------------------------

    [Fact]
    public async Task RealReservedSource_IsTheLockedStockItemsReservedQuantity()
    {
        await using var db = NewDb();
        var item = new StockItem { StockLocationId = Loc, ProductId = ProductA, QuantityOnHand = 100m, ReservedQuantity = 30m };
        db.StockItems.Add(item);
        await db.SaveChangesAsync();

        Assert.Equal(30m, await StockAvailability.ReservedAsync(db, Loc, ProductA, null, default));
        Assert.Equal(0m, await StockAvailability.ReservedAsync(db, Loc, ProductB, null, default));
        Assert.Equal(70m, await StockAvailability.AvailableAsync(db, Loc, ProductA, item, default));

        var items = new Dictionary<(Guid, Guid), StockItem> { [(Loc, ProductA)] = item };
        await StockAvailability.EnsureAsync(db, items, [new(Loc, ProductA, 70m)], Short, default);
        Assert.Equal("71>70", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            StockAvailability.EnsureAsync(db, items, [new(Loc, ProductA, 71m)], Short, default))).Message);
    }

    [Fact]
    public async Task BatchAvailability_IsTheLowerOfTheBatchBalanceAndOnHandLessReserved()
    {
        await using var db = NewDb();
        var batchId = Guid.NewGuid();
        db.StockItems.Add(new StockItem { StockLocationId = Loc, ProductId = ProductA, QuantityOnHand = 100m, ReservedQuantity = 30m });
        db.BatchLocationBalances.Add(new BatchLocationBalance { PurchaseBatchId = batchId, StockLocationId = Loc, ProductId = ProductA, RemainingBaseQty = 100m });
        await db.SaveChangesAsync();

        Assert.Equal(70m, await StockAvailability.AvailableInBatchAsync(db, batchId, Loc, ProductA, default));
        await StockAvailability.EnsureInBatchAsync(db, batchId, Loc, ProductA, 70m, a => new("Lines", a.ToString()), default);
        Assert.Equal("70", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            StockAvailability.EnsureInBatchAsync(db, batchId, Loc, ProductA, 80m, a => new("Lines", a.ToString()), default))).Message);
    }

    // ---- transfer dispatch (van loading uses transfers) honours the reservation -------------------------------------------------

    [Fact]
    public async Task Dispatch_ReservationReducesAvailability_RefusedOnTheLineQuantityKey()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 200m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 10m)); // 100 pc

        using (StockAvailability.UseReservedSource((_, _, _, _, _) => Task.FromResult(150m)))
        {
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() => w.DispatchAsRequestedAsync(t.Id));
            Assert.Equal("lines[0].quantity", ex.Field);
        }

        Assert.Equal(200m, await w.QohAsync(w.L.MainStore));
        await w.DispatchAsRequestedAsync(t.Id); // no reservation: ships
        Assert.Equal(100m, await w.QohAsync(w.L.MainStore));
    }

    // ---- FI-D24: request quantities above zero (create + edit share the line builder) -------------------------------------------

    [Fact]
    public async Task Create_ZeroQuantity_IsRejectedOnTheLineQuantityKey()
    {
        var w = await TransferWorld.CreateAsync();
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m), w.Pen(w.Box, 0m)]));
        Assert.Equal("lines[1].quantity", ex.Field);
    }

    [Fact]
    public async Task UpdateDraft_ZeroQuantity_IsRejectedOnTheLineQuantityKey()
    {
        var w = await TransferWorld.CreateAsync();
        var t = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m)]);

        await using var db = w.Db();
        var service = new StockTransferService(db, TransferWorld.Everything());
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateDraftAsync(t.Id, new UpdateStockTransferInput(w.L.MainStore, w.L.BlrStore, "t", [w.Pen(w.Box, 0m)]), w.W.UserId));
        Assert.Equal("lines[0].quantity", ex.Field);
    }
}
