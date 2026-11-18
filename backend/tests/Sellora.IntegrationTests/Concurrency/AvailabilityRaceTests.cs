using System.Net;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using H = Sellora.IntegrationTests.TestData.ShipmentHttp;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// FI-D23 on PostgreSQL: the shared availability check (<see cref="StockAvailability"/>) runs under the StockItems row locks, so writers that compete
/// for the same (location, product) never oversell. Two-writer races are made deterministic with <see cref="LockRace"/> (a gate holds the source
/// StockItems row; both writers are seen waiting in the StockItems FOR UPDATE before the gate opens); N-writer races are parallel hammers whose
/// assertions hold for every interleaving. The loser must always get a <see cref="DomainValidationException"/> (a 400), never a database or
/// constraint exception, and the stock invariants must hold. Written to pass with or without CK_StockItems_QuantityOnHand_NonNegative.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AvailabilityRaceTests(PostgresFixture postgres)
{
    private const string StockItemsForUpdate = """%"StockItems"%FOR UPDATE%""";

    private static DispatchStockTransferInput DispatchInput(StockTransfer transfer) =>
        new(transfer.Lines.OrderBy(l => l.LineNo).Select(l => new DispatchStockTransferLineInput(l.Id, l.ProductUnitId, l.RequestedQty)).ToList(), null);

    private static async Task<Exception?[]> ParallelAsync(params Func<Task>[] work)
    {
        var start = new TaskCompletionSource();
        var tasks = work.Select(w => Task.Run(async () =>
        {
            await start.Task;
            try { await w(); return (Exception?)null; }
            catch (Exception ex) { return ex; }
        })).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(120));
    }

    private static void AssertNoDatabaseFailure(IEnumerable<Exception?> outcomes) =>
        Assert.All(outcomes.Where(o => o is not null), o => Assert.IsType<DomainValidationException>(o));

    private static Task<SalesInvoice> SaleOnAsync(SelloraDbContext db, TransferScenario t, Guid location, decimal pieces) =>
        new SalesService(db, LocationScenario.CompanyLevel).CreateSaleAsync(
            new CreateSaleInput(t.L.S.CustomerId, StockScenario.SaleDate, PaymentMethod.Cash, 0, [new SaleLineInput(t.PenId, t.Pc, pieces, 10m, 0)], StockLocationId: location), t.UserId);

    /// <summary>A gate holds the product's (only) StockItems row; both writers are released and must be seen waiting inside the StockItems FOR UPDATE.</summary>
    private static async Task<Exception?[]> GatedAsync(TransferScenario t, params (string Name, Func<SelloraDbContext, Task> Work)[] writers)
    {
        await using var race = await LockRace.BeginAsync(t.Database, LockRace.LockRow("StockItems", "ProductId", t.PenId));
        var started = new List<LockRace.Writer>();
        foreach (var (name, work) in writers)
        {
            var w = await race.StartAsync(name, work, branchId: t.Main);
            await race.WaitBlockedAsync(w, StockItemsForUpdate);
            started.Add(w);
        }
        return await race.ReleaseAsync();
    }

    private static void AssertOneWinner(Exception?[] outcomes)
    {
        Assert.Equal(1, outcomes.Count(o => o is null));
        Assert.IsType<DomainValidationException>(Assert.Single(outcomes, o => o is not null));
    }

    // ---- two-writer races, same source location -------------------------------------------------------------------------------------

    [Fact]
    public async Task PurchaseReturnVsSale_CombinedOverBatchBalance_ExactlyOneSucceeds_LoserIsADomainError()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        var purchase = await t.BuyAsync(null, t.MainStore, t.Pc, 100m, 5m);
        var line = purchase.Lines.Single();

        var outcomes = await GatedAsync(t,
            ("return", db => new PurchaseService(db, new ExchangeRateService(db), LocationScenario.CompanyLevel).CreateReturnAsync(
                new CreatePurchaseReturnInput(purchase.Id, StockScenario.SaleDate, null, [new PurchaseReturnLineInput(line.Id, t.Pc, 60m)]), t.UserId)),
            ("sale", db => SaleOnAsync(db, t, t.MainStore, 60m)));

        AssertOneWinner(outcomes);
        Assert.Equal(40m, await t.QohAsync(t.MainStore));
        await t.AssertInvariantsAsync("purchase return vs sale");
    }

    // ---- sale / adjustment vs a SHIPMENT dispatch (FI-D34, FI-D38 e: replaces the legacy-dispatch races) -----------------------------------------------
    // 10 on hand, a ReadyToDispatch shipment of 6 (reserved 6): available is 4 before the dispatch AND after it (on hand 4, reserved 0), so a competing
    // writer fits for exactly 4 whatever the interleaving. Never an oversell, never a negative on-hand / reserved / available, both reconciliation scripts clean.

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public async Task SaleVsShipmentDispatch_AgainstTheUnreservedRemainder_NeverOversells(int pieces, bool fits)
    {
        for (var round = 0; round < 3; round++)
        {
            var (s, sid, _) = await H.ReadySceneAsync(postgres, 10m, 6m);
            var transitBefore = await H.TransitQohAsync(s);

            var responses = await H.ParallelAsync(() => H.DispatchShipmentAsync(s, sid), () => H.SellPiecesAsync(s, pieces));

            await H.AssertNo500Async($"round {round}: sale vs dispatch", responses);
            Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
            Assert.Equal(fits, responses[1].IsSuccessStatusCode);
            if (!fits) Assert.Equal(HttpStatusCode.BadRequest, responses[1].StatusCode);
            Assert.Equal((fits ? 0m : 4m, 0m, transitBefore + 6m), (await H.QohAsync(s), await H.ReservedAsync(s), await H.TransitQohAsync(s)));
            await H.AssertNothingNegativeAsync(s, $"round {round}: sale {pieces} vs dispatch");
        }
    }

    [Fact]
    public async Task SalesHammerVsShipmentDispatch_OnlyTheUnreservedRemainderSells_NeverNegative()
    {
        var (s, sid, _) = await H.ReadySceneAsync(postgres, 10m, 6m);

        var responses = await H.ParallelAsync(() => H.DispatchShipmentAsync(s, sid), () => H.SellPiecesAsync(s, 2m), () => H.SellPiecesAsync(s, 2m), () => H.SellPiecesAsync(s, 2m));

        await H.AssertNo500Async("sales vs dispatch", responses);
        Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
        Assert.Equal(2, responses.Skip(1).Count(r => r.IsSuccessStatusCode));                       // 4 free = two sales of 2
        Assert.All(responses.Skip(1).Where(r => !r.IsSuccessStatusCode), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal((0m, 0m), (await H.QohAsync(s), await H.ReservedAsync(s)));
        await H.AssertNothingNegativeAsync(s, "sales hammer vs dispatch");
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public async Task AdjustmentDecreaseVsShipmentDispatch_AgainstTheUnreservedRemainder_NeverGoesNegative(int pieces, bool fits)
    {
        for (var round = 0; round < 3; round++)
        {
            var (s, sid, _) = await H.ReadySceneAsync(postgres, 10m, 6m);
            var transitBefore = await H.TransitQohAsync(s);

            var responses = await H.ParallelAsync(() => H.DispatchShipmentAsync(s, sid), () => H.AdjustDownAsync(s, pieces));

            await H.AssertNo500Async($"round {round}: adjustment vs dispatch", responses);
            Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
            Assert.Equal(fits, responses[1].IsSuccessStatusCode);
            if (!fits) Assert.Equal(HttpStatusCode.BadRequest, responses[1].StatusCode);
            Assert.Equal((fits ? 0m : 4m, 0m, transitBefore + 6m), (await H.QohAsync(s), await H.ReservedAsync(s), await H.TransitQohAsync(s)));
            await H.AssertNothingNegativeAsync(s, $"round {round}: adjustment {pieces} vs dispatch");
        }
    }

    [Fact]
    public async Task AdjustmentsHammerVsShipmentDispatch_CombinedOverTheUnreservedRemainder_ExactlyOneAdjustmentWins()
    {
        var (s, sid, _) = await H.ReadySceneAsync(postgres, 10m, 6m);

        var responses = await H.ParallelAsync(() => H.DispatchShipmentAsync(s, sid), () => H.AdjustDownAsync(s, 4m), () => H.AdjustDownAsync(s, 4m));

        await H.AssertNo500Async("adjustments vs dispatch", responses);
        Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
        Assert.Equal(1, responses.Skip(1).Count(r => r.IsSuccessStatusCode));
        Assert.All(responses.Skip(1).Where(r => !r.IsSuccessStatusCode), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal((0m, 0m), (await H.QohAsync(s), await H.ReservedAsync(s)));
        await H.AssertNothingNegativeAsync(s, "adjustments hammer vs dispatch");
    }

    // ---- N-writer races --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoPurchaseReturns_OfTheSameBatchExceedingItsBalance_ExactlyOneSucceeds()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        var purchase = await t.BuyAsync(null, t.MainStore, t.Pc, 100m, 5m);
        var line = purchase.Lines.Single();
        await t.SellAsync(null, t.MainStore, t.Pc, 50m, 10m); // batch balance 50; the line itself still has 100 returnable

        var outcomes = await ParallelAsync(Enumerable.Range(0, 2).Select<int, Func<Task>>(_ => async () =>
        {
            await using var db = t.Db(t.Main);
            await new PurchaseService(db, new ExchangeRateService(db), LocationScenario.CompanyLevel).CreateReturnAsync(
                new CreatePurchaseReturnInput(purchase.Id, StockScenario.SaleDate, null, [new PurchaseReturnLineInput(line.Id, t.Pc, 40m)]), t.UserId);
        }).ToArray());

        AssertOneWinner(outcomes);
        Assert.Equal(10m, await t.QohAsync(t.MainStore));
        await t.AssertInvariantsAsync("two purchase returns");
    }

    [Fact]
    public async Task TenConcurrentSalesOfOneUnit_WithThreeOnHand_ExactlyThreeSucceed()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(null, t.MainStore, t.Pc, 3m, 5m);

        var outcomes = await ParallelAsync(Enumerable.Range(0, 10).Select<int, Func<Task>>(_ => async () =>
        {
            await using var db = t.Db(t.Main);
            await SaleOnAsync(db, t, t.MainStore, 1m);
        }).ToArray());

        Assert.Equal(3, outcomes.Count(o => o is null));
        AssertNoDatabaseFailure(outcomes);
        Assert.Equal(0m, await t.QohAsync(t.MainStore));
        await using (var db = database.CreateDbContext()) Assert.Equal(3, await db.SalesInvoices.CountAsync());
        await t.AssertInvariantsAsync("ten sales");
    }

    [Fact]
    public async Task PurchaseRacingASale_OnAProductWithNothingOnHand_NoDeadlock_StateMatchesTheSerialOrder()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        var expected = 0m;

        for (var round = 0; round < 5; round++)
        {
            var outcomes = await ParallelAsync(
                async () => { await using var db = t.Db(t.Main); await new PurchaseService(db, new ExchangeRateService(db), LocationScenario.CompanyLevel).CreatePurchaseAsync(
                    new CreatePurchaseInput(t.L.S.SupplierId, StockScenario.PurchaseDate, null, null, PaymentMethod.Cash, 0, [new PurchaseLineInput(t.PenId, t.Pc, 10m, 5m, 0, 0)], StockLocationId: t.MainStore), t.UserId); },
                async () => { await using var db = t.Db(t.Main); await SaleOnAsync(db, t, t.MainStore, 1m); });

            Assert.Null(outcomes[0]);
            AssertNoDatabaseFailure(outcomes);
            expected += 10m - (outcomes[1] is null ? 1m : 0m);
            Assert.Equal(expected, await t.QohAsync(t.MainStore));
        }
        await t.AssertInvariantsAsync("purchase vs sale on empty stock");
    }

    [Fact]
    public async Task SalesUnderAReservation_TwoReservedOfFive_AtMostThreeAreSold()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var t = await TransferScenario.CreateAsync(database);
        await t.BuyAsync(null, t.MainStore, t.Pc, 5m, 5m);

        Exception?[] outcomes;
        using (StockAvailability.UseReservedSource((_, location, product, batch, _) =>
                   Task.FromResult(location == t.MainStore && product == t.PenId && batch is null ? 2m : 0m)))
        {
            outcomes = await ParallelAsync(Enumerable.Range(0, 6).Select<int, Func<Task>>(_ => async () =>
            {
                await using var db = t.Db(t.Main);
                await SaleOnAsync(db, t, t.MainStore, 1m);
            }).ToArray());
        }

        Assert.Equal(3, outcomes.Count(o => o is null));
        AssertNoDatabaseFailure(outcomes);
        Assert.Equal(2m, await t.QohAsync(t.MainStore));
        await t.AssertInvariantsAsync("reserved sales");
    }
}
