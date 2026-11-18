using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Shipments;

/// <summary>
/// Phase 05 shipment dispatch (FI-D34) on EF InMemory: FIFO source to Transit with batch rows traceable to the consignment line, reservation consumed
/// exactly once, the unloaded remainder released, request status, source fit, idempotency, and the legacy-path guards (batch-row filter, cancel after dispatch).
/// Every test re-checks the reservation invariant (FI-D5) and the V4 Transit identity.
/// </summary>
public class ShipmentDispatchTests
{
    private static Task<string?> Field(Func<Task> act) => ShipmentWorld.FieldAsync(act);

    [Fact]
    public async Task Dispatch_MovesTheLoadedQuantityToTransit_ConsumesTheReservation_AndSetsEveryStatus()
    {
        var w = await ShipmentWorld.CreateAsync(100m);
        var (ready, t) = await w.ReadyAsync(boxes: 5m);
        var line = ready.Consignments.Single().Lines.Single();
        Assert.Equal(50m, await w.ReservedAsync(w.L.MainStore));

        var shipment = await w.DispatchAsync(ready.Id);

        Assert.Equal(ShipmentStatus.Dispatched, shipment!.Status);
        Assert.NotNull(shipment.DispatchedAt);
        Assert.Equal(w.UserId, shipment.DispatchedByUserId);
        var consignment = shipment.Consignments.Single();
        Assert.Equal(ConsignmentStatus.InTransit, consignment.Status);
        Assert.NotNull(consignment.DispatchedAt);

        Assert.Equal((50m, 50m), (await w.T.QohAsync(w.L.MainStore), await w.T.QohAsync(w.L.Transit)));
        Assert.Equal(0m, await w.ReservedAsync(w.L.MainStore));
        var reservation = Assert.Single(await w.ReservationsAsync());
        Assert.Equal((StockReservationStatus.Consumed, 50m), (reservation.Status, reservation.BaseQty));
        Assert.NotNull(reservation.ConsumedAt);

        var after = (await w.ReloadAsync(ready.Id)).Consignments.Single().Lines.Single();
        Assert.Equal((50m, 50m, 50m, 5m), (after.DispatchedBaseQty, after.AllocatedBaseQty, after.LoadedBaseQty, after.EnteredQty));
        var request = await w.T.ReloadAsync(t.Id);
        Assert.Equal(50m, request.Lines.Single().DispatchedBaseQty);
        Assert.Equal(StockTransferStatus.Fulfilled, request.Status);
        Assert.Null(request.DispatchedAt); // CK_StockTransfers_Status_Dispatch: the request's own stamp is for the legacy Dispatched status only
        // The request line's dispatch snapshot is in base units so CK_StockTransferLines_Dispatch_Arithmetic (DispatchedQty x DispatchFactor = DispatchedBaseQty) holds.
        var rl = request.Lines.Single();
        Assert.Equal((w.T.Pc, "pc", 1m, 50m), (rl.DispatchProductUnitId, rl.DispatchUnitCode, rl.DispatchFactor, rl.DispatchedQty));
        Assert.Equal(rl.DispatchedBaseQty, rl.DispatchedQty * rl.DispatchFactor);

        var row = Assert.Single(await w.BatchRowsAsync());
        Assert.Equal((line.Id, request.Lines.Single().Id, 50m), (row.ConsignmentLineId, row.LineId, row.DispatchedBaseQty));
        var movements = await w.MovementsAsync(t.Id);
        Assert.Equal(new[] { -50m, 50m }, movements.Select(m => m.QuantityBaseUnits).OrderBy(q => q).ToArray());
        Assert.All(movements, m => Assert.Equal(5m, m.EnteredQuantity)); // one batch: the entered quantity (5 Box) is recorded

        await using var db = w.T.Db();
        Assert.Contains(await db.AuditEvents.Select(e => e.Action).ToListAsync(), a => a == AuditActions.ShipmentDispatched);
        await w.AssertReconciledAsync("dispatch");
        await w.T.AssertInvariantsAsync("dispatch");
    }

    [Fact]
    public async Task Dispatch_DrawsOldestBatchFirst_KeepsBatchCost_AndWritesOneRowPerBatch()
    {
        var w = await ShipmentWorld.CreateAsync(0m);
        await w.T.StockPenAsync(w.L.MainStore, 40m, 5m);   // oldest
        await w.T.StockPenAsync(w.L.MainStore, 60m, 8m);
        var (ready, t) = await w.ReadyAsync(boxes: 7m);    // 70 pc: 40 from the first batch, 30 from the second
        var before = await w.T.CompanyValueAsync();

        await w.DispatchAsync(ready.Id);

        var rows = (await w.BatchRowsAsync()).OrderBy(r => r.UnitCost).ToList();
        Assert.Equal(new[] { (40m, 5m), (30m, 8m) }, rows.Select(r => (r.DispatchedBaseQty, r.UnitCost)).ToArray());
        Assert.All(rows, r => Assert.NotNull(r.ConsignmentLineId));
        Assert.Equal(4, (await w.MovementsAsync(t.Id)).Count);
        Assert.Equal((30m, 70m), (await w.T.QohAsync(w.L.MainStore), await w.T.QohAsync(w.L.Transit)));
        await w.T.AssertValueConservedAsync(before, "dispatch");
        await w.AssertReconciledAsync("fifo");
        await w.T.AssertInvariantsAsync("fifo");
    }

    [Fact]
    public async Task Dispatch_ReleasesTheAllocatedButUnloadedRemainder_AndConsumesOnlyTheLoaded()
    {
        var w = await ShipmentWorld.CreateAsync(100m);
        var (ready, t) = await w.ReadyAsync(boxes: 5m, requestedBoxes: 8m);
        // Loading below the allocation cannot leave the shipment ReadyToDispatch through the service, so the state is seeded: 3 of the 5 allocated Box loaded.
        await using (var db = w.T.Db())
        {
            var line = await db.ConsignmentLines.SingleAsync();
            line.LoadedBaseQty = 30m;
            await db.SaveChangesAsync();
        }

        await w.DispatchAsync(ready.Id);

        var after = (await w.ReloadAsync(ready.Id)).Consignments.Single().Lines.Single();
        Assert.Equal((30m, 30m, 3m), (after.DispatchedBaseQty, after.AllocatedBaseQty, after.EnteredQty));
        Assert.Equal(0m, await w.ReservedAsync(w.L.MainStore));                    // consumed 30 + released 20
        Assert.Equal((70m, 30m), (await w.T.QohAsync(w.L.MainStore), await w.T.QohAsync(w.L.Transit)));
        var reservation = Assert.Single(await w.ReservationsAsync());
        Assert.Equal((StockReservationStatus.Consumed, 30m), (reservation.Status, reservation.BaseQty));
        var request = await w.T.ReloadAsync(t.Id);
        var requestLine = request.Lines.Single();
        Assert.Equal((30m, 30m), (requestLine.DispatchedBaseQty, requestLine.AllocatedBaseQty)); // the 20 are allocatable again
        Assert.Equal(StockTransferStatus.PartiallyFulfilled, request.Status);                       // 30 of 80 shipped
        await w.AssertReconciledAsync("remainder released");
        await w.T.AssertInvariantsAsync("remainder released");
    }

    [Fact]
    public async Task Dispatch_PartOfTheRequest_IsPartiallyFulfilled_AndTheRestCanBeAllocatedAgain()
    {
        var w = await ShipmentWorld.CreateAsync(100m);
        var (ready, t) = await w.ReadyAsync(boxes: 3m, requestedBoxes: 10m);
        await w.DispatchAsync(ready.Id);

        Assert.Equal(StockTransferStatus.PartiallyFulfilled, (await w.T.ReloadAsync(t.Id)).Status);
        var second = await w.CreateShipmentAsync();
        var allocated = await w.AllocateAsync(second.Id, t.Lines.Single().Id, w.T.Box, 7m);
        Assert.Equal(70m, allocated!.Consignments.Single().Lines.Single().AllocatedBaseQty);
        await w.AssertReconciledAsync("second allocation");
    }

    [Fact]
    public async Task Dispatch_SameKeyTwice_MovesOnce_AndAnotherKeyOnAnotherShipmentIsRefused()
    {
        var w = await ShipmentWorld.CreateAsync(200m);
        var (a, _) = await w.ReadyAsync(boxes: 5m);
        var first = await w.DispatchAsync(a.Id, key: "k-ship");
        var replay = await w.DispatchAsync(a.Id, key: "k-ship");

        Assert.Equal(first!.DispatchedAt, replay!.DispatchedAt);
        Assert.Equal((150m, 50m), (await w.T.QohAsync(w.L.MainStore), await w.T.QohAsync(w.L.Transit)));
        Assert.Single(await w.BatchRowsAsync());

        var (b, _) = await w.ReadyAsync(boxes: 2m);
        Assert.Equal("IdempotencyKey", await Field(() => w.DispatchAsync(b.Id, key: "k-ship")));
        Assert.Equal(ShipmentStatus.ReadyToDispatch, (await w.ReloadAsync(b.Id)).Status);
    }

    [Fact]
    public async Task Dispatch_WithoutAKey_SecondCall_IsRefusedOnStatus_AndMovesNothingMore()
    {
        var w = await ShipmentWorld.CreateAsync(100m);
        var (ready, _) = await w.ReadyAsync(boxes: 5m);
        await w.DispatchAsync(ready.Id);

        Assert.Equal("status", await Field(() => w.DispatchAsync(ready.Id)));
        Assert.Equal((50m, 50m), (await w.T.QohAsync(w.L.MainStore), await w.T.QohAsync(w.L.Transit)));
        await w.T.AssertInvariantsAsync("second dispatch");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatch_OfAShipmentThatIsNotReady_IsRefusedOnStatus(bool loaded)
    {
        var w = await ShipmentWorld.CreateAsync(100m);
        var t = await w.ApprovedPenAsync(5m);
        var s = await w.CreateShipmentAsync();
        await w.AllocateAsync(s.Id, t.Lines.Single().Id, w.T.Box, 5m);
        if (loaded)
        {
            var c = (await w.ReloadAsync(s.Id)).Consignments.Single();
            await w.LoadAsync(s.Id, c.Id, c.Lines.Single().Id, 5m); // loaded but no leg: not ready
        }

        Assert.Equal("status", await Field(() => w.DispatchAsync(s.Id)));
        Assert.Equal(50m, await w.ReservedAsync(w.L.MainStore));
        Assert.Equal(0m, await w.T.QohAsync(w.L.Transit));
    }

    [Fact]
    public async Task Dispatch_OfAnUnknownShipment_IsNull()
    {
        var w = await ShipmentWorld.CreateAsync();
        Assert.Null(await w.DispatchAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Cancel_AfterDispatch_IsRefused_AndConsignmentCancelToo()
    {
        var w = await ShipmentWorld.CreateAsync(100m);
        var (ready, _) = await w.ReadyAsync(boxes: 5m);
        await w.DispatchAsync(ready.Id);

        Assert.Equal("status", await Field(() => w.CancelAsync(ready.Id)));
        Assert.Equal("status", await Field(() => w.CancelConsignmentAsync(ready.Id, ready.Consignments.Single().Id)));
        Assert.Equal((50m, 50m), (await w.T.QohAsync(w.L.MainStore), await w.T.QohAsync(w.L.Transit)));
        await w.AssertReconciledAsync("cancel refused");
    }

    // ---- source fit (ported from the legacy dispatch permission tests; StockTransfers.Dispatch at the source) ----------------------------

    [Fact]
    public async Task Dispatch_NeedsTheSourceToFit_AndTheCompanyLevelCodeBypasses()
    {
        var w = await ShipmentWorld.CreateAsync(100m);
        var (ready, _) = await w.ReadyAsync(boxes: 2m);

        Assert.Equal("stockLocationId", await Field(() => w.DispatchAsync(ready.Id, branch: w.Blr, permissions: LocationWorld.Company())));
        Assert.Equal("stockLocationId", await Field(() => w.DispatchAsync(ready.Id, branch: w.Blr, permissions: LocationWorld.Company("StockTransfers.Receive"))));
        Assert.Equal(ShipmentStatus.ReadyToDispatch, (await w.ReloadAsync(ready.Id)).Status);
        Assert.Equal(0m, await w.T.QohAsync(w.L.Transit));

        await w.DispatchAsync(ready.Id, branch: w.Main, permissions: FakeScopedPermissions.AtBranch(w.Main, "StockTransfers.Dispatch")); // the source's own branch
        Assert.Equal(ShipmentStatus.Dispatched, (await w.ReloadAsync(ready.Id)).Status);
    }

    [Fact]
    public async Task Dispatch_CompanyLevelCode_OpensAnotherBranchesSource()
    {
        var w = await ShipmentWorld.CreateAsync(100m);
        var (ready, _) = await w.ReadyAsync(boxes: 2m);

        await w.DispatchAsync(ready.Id, branch: w.Blr, permissions: LocationWorld.Company("StockTransfers.Dispatch"));
        Assert.Equal(ShipmentStatus.Dispatched, (await w.ReloadAsync(ready.Id)).Status);
    }

    [Fact]
    public async Task Dispatch_FromAnotherBranchsVehicle_IsRefusedWithoutTheCode_EvenForAHolderAtTheWorkingBranch()
    {
        var w = await ShipmentWorld.CreateAsync(0m);
        await w.T.StockPenAsync(w.L.BlrVan, 50m);
        var (ready, _) = await w.ReadyAsync(source: w.L.BlrVan, destination: w.L.CompanyWarehouse, boxes: 1m);

        Assert.Equal("stockLocationId", await Field(() => w.DispatchAsync(ready.Id, branch: w.Main, permissions: FakeScopedPermissions.AtBranch(w.Main, "StockTransfers.Dispatch"))));
        await w.DispatchAsync(ready.Id, branch: w.Blr, permissions: FakeScopedPermissions.AtBranch(w.Blr, "StockTransfers.Dispatch")); // the van's own branch
        Assert.Equal(ShipmentStatus.Dispatched, (await w.ReloadAsync(ready.Id)).Status);
    }

    [Theory]
    [InlineData("main-to-blr", "dispatched")]               // the source is inside the allocation
    [InlineData("blr-to-main", "hidden")]                  // BLR's shipment is outside the member's branch filter: a 404, never a 400 that leaks it
    [InlineData("blr-van-to-main", "hidden")]
    [InlineData("company-to-blr", "hidden")]               // FI-D32 amendment: a company-level source is invisible to a branch-restricted member
    public async Task Dispatch_RestrictedMemberWithCompanyLevelCode_NeedsTheSourceInsideItsAllocation(string scenario, string outcome)
    {
        var w = await ShipmentWorld.CreateAsync(0m);
        var (src, dst) = scenario switch
        {
            "main-to-blr" => (w.L.MainStore, w.L.BlrStore),
            "blr-to-main" => (w.L.BlrStore, w.L.MainStore),
            "company-to-blr" => (w.L.CompanyWarehouse, w.L.BlrStore),
            _ => (w.L.BlrVan, w.L.MainStore),
        };
        await w.T.StockPenAsync(src, 100m);
        var (ready, _) = await w.ReadyAsync(source: src, destination: dst, boxes: 2m);

        await using var db = w.T.Db(w.Main, new HashSet<Guid> { w.Main });
        string actual;
        try
        {
            var result = await new ShipmentService(db, LocationWorld.Company("StockTransfers.Dispatch")).DispatchAsync(ready.Id, new DispatchShipmentInput(), w.UserId);
            actual = result is null ? "hidden" : "dispatched";
        }
        catch (DomainValidationException ex) { actual = ex.Field; }

        Assert.Equal(outcome, actual);
        Assert.Equal(outcome == "dispatched" ? ShipmentStatus.Dispatched : ShipmentStatus.ReadyToDispatch, (await w.ReloadAsync(ready.Id)).Status);
    }

    [Fact]
    public async Task Dispatch_WithoutAPermissionService_FailsClosedForACompanyWarehouseSource()
    {
        // Replaces the legacy StockTransferPermissionTests.Dispatch_WithoutAPermissionService_FailsClosedForACompanyWarehouseSource.
        var w = await ShipmentWorld.CreateAsync(0m);
        await w.T.StockPenAsync(w.L.CompanyWarehouse, 100m);
        var (ready, _) = await w.ReadyAsync(source: w.L.CompanyWarehouse, destination: w.L.MainStore, boxes: 1m);

        await using (var db = w.T.Db(w.Main))
        {
            await Assert.ThrowsAsync<DomainValidationException>(() => new ShipmentService(db, null).DispatchAsync(ready.Id, new DispatchShipmentInput(), w.UserId));
        }

        Assert.Equal(ShipmentStatus.ReadyToDispatch, (await w.ReloadAsync(ready.Id)).Status);
        Assert.Equal((0m, 100m), (await w.T.QohAsync(w.L.Transit), await w.T.QohAsync(w.L.CompanyWarehouse)));
    }

    // ---- legacy path stays closed / unaffected -------------------------------------------------------------------------------------

    [Fact]
    public async Task LegacyReceive_IgnoresShipmentBatchRows_OfTheSameLine()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 200m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 10m));
        await w.DispatchAsRequestedAsync(t.Id); // legacy-dispatched state: 100 pc in Transit on legacy batch rows
        var line = (await w.ReloadAsync(t.Id)).Lines.Single();

        // A shipment-dispatched row on the same request line, on an OLDER batch: FIFO without the ConsignmentLineId filter would draw from it first.
        await using (var db = w.Db())
        {
            var older = new PurchaseBatch
            {
                CompanyId = line.CompanyId, Id = Guid.NewGuid(), ProductId = w.PenId, OriginStockLocationId = w.L.MainStore, BatchSequence = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-30), ReceivedDate = new DateOnly(2026, 10, 1), PurchasedBaseQty = 30m, RemainingBaseQty = 0m, UnitCost = 4m,
            };
            db.PurchaseBatches.Add(older);
            db.StockTransferLineBatches.Add(new StockTransferLineBatch
            {
                CompanyId = line.CompanyId, Id = Guid.NewGuid(), LineId = line.Id, PurchaseBatchId = older.Id, ConsignmentLineId = Guid.NewGuid(), DispatchedBaseQty = 30m, UnitCost = 4m,
            });
            await db.SaveChangesAsync();
        }

        var r1 = await w.ReceiveAsync(t.Id, line.Id, w.Box, 6m);
        await using (var db = w.Db())
        {
            await new StockTransferService(db, TransferWorld.Everything()).ResolveAsync(t.Id,
                new ResolveStockTransferInput("loss", [new ResolveStockTransferLineInput(line.Id, StockTransferResolution.WriteOff)]), w.W.UserId);
        }

        Assert.Equal(StockTransferStatus.Completed, (await w.ReloadAsync(t.Id)).Status);
        var rows = await w.ReloadAsync(t.Id);
        var legacy = rows.Lines.Single().Batches.Single(b => b.ConsignmentLineId is null);
        var foreign = rows.Lines.Single().Batches.Single(b => b.ConsignmentLineId is not null);
        Assert.Equal((60m, 40m), (legacy.ReceivedBaseQty, legacy.WrittenOffBaseQty));
        Assert.Equal((0m, 0m, 0m, 0m), (foreign.ReceivedBaseQty, foreign.DamagedBaseQty, foreign.WrittenOffBaseQty, foreign.ReturnedBaseQty));
        Assert.Equal(60m, await w.QohAsync(w.L.BlrStore));
        Assert.NotNull(r1);
    }
}
