using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Shipments;

/// <summary>Phase 04 code-review fixes (FI-D32 amendment): purchase cancellation vs reservations, company-level source fit, allocation unit
/// activity, idempotent consignment cancel. EF InMemory (no locks); HTTP visibility is covered by the integration tests.</summary>
public class ShipmentReviewFixTests
{
    [Fact]
    public async Task CancelPurchase_ThatWouldTakeOnHandBelowReserved_IsRefusedWithStatusField_AndChangesNothing()
    {
        var w = await ShipmentWorld.CreateAsync(0m);
        var purchase = await w.L.BuyAsync(w.Main, w.L.MainStore, qty: 10m);
        await using (var db = w.T.Db())
        {
            (await db.StockItems.SingleAsync(i => i.StockLocationId == w.L.MainStore && i.ProductId == w.L.W.ProductId)).ReservedQuantity = 105m;
            await db.SaveChangesAsync();
        }

        await using var cancelDb = w.T.Db(w.Main);
        var service = new PurchaseService(cancelDb, new ExchangeRateService(cancelDb), ShipmentWorld.Everything());
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.CancelPurchaseAsync(purchase.Id, w.UserId));
        Assert.Equal("Status", ex.Field);
        Assert.Contains("reserved", ex.Message);

        await using var check = w.T.Db(w.Main);
        Assert.NotEqual(PurchaseStatus.Cancelled, (await check.PurchaseOrders.SingleAsync(p => p.Id == purchase.Id)).Status);
    }

    [Fact]
    public async Task CancelPurchase_LeavingEnoughForTheReservations_Succeeds()
    {
        var w = await ShipmentWorld.CreateAsync(0m);
        var purchase = await w.L.BuyAsync(w.Main, w.L.MainStore, qty: 10m);
        await using (var db = w.T.Db())
        {
            (await db.StockItems.SingleAsync(i => i.StockLocationId == w.L.MainStore && i.ProductId == w.L.W.ProductId)).ReservedQuantity = 8m;
            await db.SaveChangesAsync();
        }

        await using var cancelDb = w.T.Db(w.Main);
        await new PurchaseService(cancelDb, new ExchangeRateService(cancelDb), ShipmentWorld.Everything()).CancelPurchaseAsync(purchase.Id, w.UserId);
        await using var check = w.T.Db(w.Main);
        Assert.Equal(PurchaseStatus.Cancelled, (await check.PurchaseOrders.SingleAsync(p => p.Id == purchase.Id)).Status);
    }

    [Fact]
    public async Task CompanyLevelSource_NeedsAnAllBranchMembership_ForPlanning()
    {
        var w = await ShipmentWorld.CreateAsync();
        var narrowed = new HashSet<Guid> { w.Main };

        // Company-wide permission but a membership narrowed to one branch: the central warehouse and the central van are not theirs.
        foreach (var source in new[] { w.L.CompanyWarehouse, w.L.CentralVan })
        {
            await using var db = w.T.Db(w.Main, narrowed);
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
                new ShipmentService(db, ShipmentWorld.Everything()).CreateAsync(
                    new CreateShipmentInput(source, TransportMode.Road, TransportVia.OwnFleet, null, null, null, null, "n", null), w.UserId));
            Assert.Equal("sourceLocationId", ex.Field);
        }
        Assert.NotNull(await w.CreateShipmentAsync(source: w.L.CompanyWarehouse));      // all-branch member
        await using var own = w.T.Db(w.Main, narrowed);
        Assert.NotNull(await new ShipmentService(own, ShipmentWorld.Everything()).CreateAsync(   // a branch-owned source stays fine
            new CreateShipmentInput(w.L.MainStore, TransportMode.Road, TransportVia.OwnFleet, null, null, null, null, "n", null), w.UserId));
    }

    [Fact]
    public async Task ChangeAllocation_Increase_NeedsAnActiveUnit_ButReducingStillWorks()
    {
        var w = await ShipmentWorld.CreateAsync();
        var t = await w.ApprovedPenAsync(5m);
        var s = await w.CreateShipmentAsync();
        await w.AllocateAsync(s.Id, t.Lines.Single().Id, w.T.Box, 2m);
        var line = (await w.ReloadAsync(s.Id)).Consignments.Single().Lines.Single();
        await using (var db = w.T.Db())
        {
            (await db.ProductUnits.SingleAsync(u => u.Id == w.T.Box)).IsActive = false;
            await db.SaveChangesAsync();
        }

        Assert.Equal("productUnitId", await ShipmentWorld.FieldAsync(() => w.ChangeAsync(s.Id, line.Id, 3m)));
        await w.ChangeAsync(s.Id, line.Id, 1m);
        Assert.Equal(10m, await w.ReservedAsync(w.L.MainStore));
        await w.AssertReconciledAsync("reduce with inactive unit");
    }

    [Fact]
    public async Task CancelConsignment_OnAnAlreadyCancelledShipmentOrConsignment_ReturnsTheCancelledState()
    {
        var w = await ShipmentWorld.CreateAsync();
        var t = await w.ApprovedPenAsync(3m);
        var s = await w.CreateShipmentAsync();
        await w.AllocateAsync(s.Id, t.Lines.Single().Id, w.T.Box, 3m);
        var consignmentId = (await w.ReloadAsync(s.Id)).Consignments.Single().Id;

        await w.CancelConsignmentAsync(s.Id, consignmentId);
        var again = await w.CancelConsignmentAsync(s.Id, consignmentId);               // already-cancelled consignment
        Assert.Equal(ConsignmentStatus.Cancelled, again!.Consignments.Single().Status);

        await w.CancelAsync(s.Id);
        var onCancelled = await w.CancelConsignmentAsync(s.Id, consignmentId);          // shipment cancelled too
        Assert.Equal(ShipmentStatus.Cancelled, onCancelled!.Status);
        Assert.Equal(ConsignmentStatus.Cancelled, onCancelled.Consignments.Single().Status);
        Assert.Equal(0m, await w.ReservedAsync(w.L.MainStore));
    }
}
