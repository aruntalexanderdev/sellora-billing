using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>
/// Functional improvements Phase 03 (FI-D26/27/28) on EF InMemory: close of the remainder, the ready-for-shipment queue scope, the audit
/// history, source-branch cost hiding, and "approval never touches stock". Close from the other statuses is in the lifecycle matrix.
/// </summary>
public class StockTransferFulfilmentTests
{
    private static T Ok<T>(ActionResult<T> result) => (T)Assert.IsType<OkObjectResult>(result.Result).Value!;

    // Approved Main store -> Blr store, 10 Box (100 pc), with the line counters set directly (shipments arrive in Phase 04).
    private static async Task<(TransferWorld W, StockTransfer T)> PartialAsync(decimal dispatched = 30m, decimal allocated = 30m, StockTransferStatus status = StockTransferStatus.PartiallyFulfilled)
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 200m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 10m));
        await SetAsync(w, t.Id, status, dispatched, allocated);
        return (w, await w.ReloadAsync(t.Id));
    }

    private static async Task SetAsync(TransferWorld w, Guid id, StockTransferStatus status, decimal dispatched, decimal allocated)
    {
        await using var db = w.Db();
        var row = await db.StockTransfers.Include(x => x.Lines).SingleAsync(x => x.Id == id);
        row.Status = status;
        var line = row.Lines.Single();
        line.DispatchedBaseQty = dispatched;
        line.AllocatedBaseQty = allocated;
        await db.SaveChangesAsync();
    }

    private static Task<StockTransfer?> CloseAsync(TransferWorld w, Guid id, string reason)
    {
        return Run();
        async Task<StockTransfer?> Run()
        {
            await using var db = w.Db();
            return await new StockTransferService(db, TransferWorld.Everything()).CloseAsync(id, reason, w.W.UserId);
        }
    }

    private static (SelloraDbContext Db, StockTransfersController Controller) Controller(TransferWorld w, Guid? active, IReadOnlySet<Guid>? allowed, IEffectivePermissionService permissions, bool companyScope = false)
    {
        var branch = new BranchContext();
        branch.Set(active, allowed);
        if (companyScope) branch.WidenToCompanyScope();
        var db = new SelloraDbContext(w.W.Options, TestCompany.Context, branch);
        return (db, new StockTransfersController(new StockTransferService(db, permissions), db, branch, permissions));
    }

    // ---- close -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Close_AfterAPartialShipment_ClosesTheRemainder_StampsIt_AuditsIt_AndMovesNoStock()
    {
        var (w, t) = await PartialAsync(dispatched: 30m, allocated: 30m);
        var (movements, qoh) = ((await w.MovementsAsync(t.Id)).Count, await w.CompanyQuantityAsync());

        var closed = await CloseAsync(w, t.Id, "  supplier out of stock ");

        var after = await w.ReloadAsync(t.Id);
        Assert.Equal(StockTransferStatus.Closed, closed!.Status);
        Assert.Equal(StockTransferStatus.Closed, after.Status);
        var line = after.Lines.Single();
        Assert.Equal((100m, 30m, 70m), (line.RequestedBaseQty, line.DispatchedBaseQty, line.ClosedBaseQty));
        Assert.Equal("supplier out of stock", after.ClosureReason);
        Assert.Equal(w.W.UserId, after.ClosedByUserId);
        Assert.NotNull(after.ClosedAt);
        Assert.Equal((movements, qoh), ((await w.MovementsAsync(t.Id)).Count, await w.CompanyQuantityAsync()));

        await using var db = w.Db();
        var audit = await db.AuditEvents.SingleAsync(e => e.TargetId == t.Id && e.Action == AuditActions.StockTransferClosed);
        Assert.Equal("supplier out of stock", audit.Reason);
        Assert.Contains("PartiallyFulfilled", audit.Before);
        Assert.Contains("Closed", audit.After);
    }

    [Fact]
    public async Task Close_FromApproved_WhenNothingWasDispatched_IsRefusedOnStatus()
    {
        var w = await TransferWorld.CreateAsync();
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 10m));

        Assert.Equal("status", await LocationWorld.FieldAsync(() => CloseAsync(w, t.Id, "x")));
        var after = await w.ReloadAsync(t.Id);
        Assert.Equal(StockTransferStatus.Approved, after.Status);
        Assert.Equal(0m, after.Lines.Single().ClosedBaseQty);
    }

    [Fact]
    public async Task Close_WhenNothingRemains_IsRefusedOnStatus()
    {
        // Review fix: every line fully dispatched (10 boxes = 100 base) - closing would close nothing.
        var (w, t) = await PartialAsync(dispatched: 100m, allocated: 100m);

        Assert.Equal("status", await LocationWorld.FieldAsync(() => CloseAsync(w, t.Id, "x")));
        Assert.Equal(StockTransferStatus.PartiallyFulfilled, (await w.ReloadAsync(t.Id)).Status);
    }

    [Fact]
    public async Task Close_FromApproved_WhenSomethingWasDispatched_IsAllowed()
    {
        var (w, t) = await PartialAsync(status: StockTransferStatus.Approved);
        Assert.Equal(StockTransferStatus.Closed, (await CloseAsync(w, t.Id, "short"))!.Status);
    }

    [Fact]
    public async Task Close_WhileAnAllocationIsOutstanding_IsRefusedOnStatus_AndChangesNothing()
    {
        var (w, t) = await PartialAsync(dispatched: 30m, allocated: 40m);

        Assert.Equal("status", await LocationWorld.FieldAsync(() => CloseAsync(w, t.Id, "x")));
        var after = await w.ReloadAsync(t.Id);
        Assert.Equal(StockTransferStatus.PartiallyFulfilled, after.Status);
        Assert.Equal(0m, after.Lines.Single().ClosedBaseQty);
        Assert.Null(after.ClosedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Close_WithoutAReason_IsRefusedOnReason(string? reason)
    {
        var (w, t) = await PartialAsync();
        Assert.Equal("reason", await LocationWorld.FieldAsync(() => CloseAsync(w, t.Id, reason!)));
        Assert.Equal(StockTransferStatus.PartiallyFulfilled, (await w.ReloadAsync(t.Id)).Status);
    }

    [Fact]
    public async Task Close_ReasonLength_500IsAccepted_501IsRefusedOnReason()
    {
        var (w, t) = await PartialAsync();
        Assert.Equal("reason", await LocationWorld.FieldAsync(() => CloseAsync(w, t.Id, new string('r', 501))));
        Assert.Equal(StockTransferStatus.Closed, (await CloseAsync(w, t.Id, new string('r', 500)))!.Status);
    }

    [Fact]
    public async Task Close_Repeated_ReturnsTheClosedTransfer_WithoutRestampingOrAuditingAgain()
    {
        var (w, t) = await PartialAsync();
        await CloseAsync(w, t.Id, "first");
        var first = await w.ReloadAsync(t.Id);

        var again = await CloseAsync(w, t.Id, "second");

        Assert.Equal(StockTransferStatus.Closed, again!.Status);
        var after = await w.ReloadAsync(t.Id);
        Assert.Equal((first.ClosedAt, first.ClosureReason, first.Lines.Single().ClosedBaseQty), (after.ClosedAt, after.ClosureReason, after.Lines.Single().ClosedBaseQty));
        await using var db = w.Db();
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.TargetId == t.Id && e.Action == AuditActions.StockTransferClosed));
    }

    [Fact]
    public async Task Close_ByAWorkingBranchThatIsNotTheSource_IsRefusedOnTheLocationField()
    {
        var (w, t) = await PartialAsync();
        await using var db = w.Db(w.Blr);
        var service = new StockTransferService(db, FakeScopedPermissions.AtBranch(w.Blr, "StockTransfers.Approve"));
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.CloseAsync(t.Id, "x", w.W.UserId));
        Assert.Equal("stockLocationId", ex.Field);
        Assert.Equal(StockTransferStatus.PartiallyFulfilled, (await w.ReloadAsync(t.Id)).Status);
    }

    [Fact]
    public async Task Close_OnUnknownTransfer_IsNull() =>
        Assert.Null(await CloseAsync(await TransferWorld.CreateAsync(), Guid.NewGuid(), "x"));

    // ---- approval ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Approve_LeavesStockItemsBatchesAndMovementsUntouched()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 200m);
        var t = await w.RequestedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 10m));

        async Task<(string Items, string Batches, int Movements)> StateAsync()
        {
            await using var db = w.Db();
            var items = await db.StockItems.AsNoTracking().OrderBy(i => i.StockLocationId).ThenBy(i => i.ProductId).Select(i => $"{i.StockLocationId}/{i.ProductId}/{i.QuantityOnHand}/{i.AverageCost}").ToListAsync();
            var batches = await db.PurchaseBatches.AsNoTracking().OrderBy(b => b.Id).Select(b => $"{b.Id}/{b.RemainingBaseQty}").ToListAsync();
            return (string.Join("|", items), string.Join("|", batches), await db.StockMovements.CountAsync());
        }

        var before = await StateAsync();
        var approved = await w.ApproveAsync(t.Id);

        Assert.Equal(StockTransferStatus.Approved, approved!.Status);
        Assert.Equal(before, await StateAsync());
        Assert.Empty(await w.MovementsAsync(t.Id));
        await w.AssertInvariantsAsync("approve");
    }

    // ---- ready-for-shipment queue ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadyForShipment_ListsApprovedAndPartlyFulfilledWithRemaining_InTheCallersSourceScope()
    {
        var w = await TransferWorld.CreateAsync();
        var ready = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 10m), w.Pen(w.Pc, 5m));
        var partial = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 10m));
        await SetAsync(w, partial.Id, StockTransferStatus.PartiallyFulfilled, 30m, 30m);
        var done = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 1m));
        await SetAsync(w, done.Id, StockTransferStatus.PartiallyFulfilled, 10m, 10m); // nothing left to ship
        var requested = await w.RequestedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 1m));
        var closedOne = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 1m));
        await SetAsync(w, closedOne.Id, StockTransferStatus.PartiallyFulfilled, 4m, 4m);
        await CloseAsync(w, closedOne.Id, "short");
        var fromBlr = await w.CreateTransferAsync(w.L.BlrStore, w.L.MainStore, [w.Pen(w.Box, 1m)], submit: true, branch: w.Blr);
        await w.ApproveAsync(fromBlr.Id, w.Blr);

        var (db, controller) = Controller(w, w.Main, new HashSet<Guid> { w.Main }, TransferWorld.Everything());
        await using var _ = db;
        var page = Ok(await controller.ReadyForShipment(null, null));

        Assert.Equal(new[] { ready.Id, partial.Id, done.Id }.Except([done.Id]).Order(), page.Items.Select(i => i.Id).Order());
        Assert.Equal(2, page.Total);
        var item = page.Items.Single(i => i.Id == ready.Id);
        Assert.Equal((StockTransferStatus.Approved, 2, 2, w.L.MainStore, w.L.BlrStore), (item.Status, item.LineCount, item.RemainingLineCount, item.SourceLocationId, item.DestinationLocationId));
        Assert.False(string.IsNullOrEmpty(item.SourceLocationName));
        Assert.NotNull(item.ApprovedAt);
        Assert.Equal(1, page.Items.Single(i => i.Id == partial.Id).RemainingLineCount);
        Assert.DoesNotContain(page.Items, i => i.Id == fromBlr.Id); // source Blr: not the caller's branch, although the transfer arrives at Main
    }

    [Fact]
    public async Task ReadyForShipment_ForeignSourceLocation_Is403_AndCompanyScopeWidensTheQueue()
    {
        var w = await TransferWorld.CreateAsync();
        var fromMain = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 1m));
        var fromBlr = await w.CreateTransferAsync(w.L.BlrStore, w.L.MainStore, [w.Pen(w.Box, 1m)], submit: true, branch: w.Blr);
        await w.ApproveAsync(fromBlr.Id, w.Blr);

        var (db, controller) = Controller(w, w.Main, new HashSet<Guid> { w.Main, w.Blr }, TransferWorld.Everything());
        await using (db)
        {
            var denied = Assert.IsType<ObjectResult>((await controller.ReadyForShipment(w.L.BlrStore, null)).Result);
            Assert.Equal(403, denied.StatusCode);
            Assert.Equal([fromMain.Id], Ok(await controller.ReadyForShipment(null, null)).Items.Select(i => i.Id));
        }

        var (wideDb, wide) = Controller(w, w.Main, new HashSet<Guid> { w.Main, w.Blr }, TransferWorld.Everything(), companyScope: true);
        await using (wideDb)
        {
            Assert.Equal(new[] { fromMain.Id, fromBlr.Id }.Order(), Ok(await wide.ReadyForShipment(null, null)).Items.Select(i => i.Id).Order());
            Assert.Equal([fromBlr.Id], Ok(await wide.ReadyForShipment(w.L.BlrStore, null)).Items.Select(i => i.Id));
        }
    }

    [Fact]
    public async Task ReadyForShipment_SearchesTransferNo_AndPages()
    {
        var w = await TransferWorld.CreateAsync();
        var a = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 1m));
        var b = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 1m));

        var (db, controller) = Controller(w, w.Main, null, TransferWorld.Everything());
        await using var _ = db;
        Assert.Equal([b.Id], Ok(await controller.ReadyForShipment(null, b.TransferNo.ToLowerInvariant())).Items.Select(i => i.Id));
        var first = Ok(await controller.ReadyForShipment(null, null, page: 1, pageSize: 1));
        var second = Ok(await controller.ReadyForShipment(null, null, page: 2, pageSize: 1));
        Assert.Equal((2, 1, 1), (first.Total, first.Items.Count, second.Items.Count));
        Assert.Equal(new[] { a.Id, b.Id }.Order(), first.Items.Concat(second.Items).Select(i => i.Id).Order());
    }

    // ---- history ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task History_ListsTheTransitionsInOrder_WithActorAndReason()
    {
        var w = await TransferWorld.CreateAsync();
        var t = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 10m)]);
        await using (var db = w.Db()) await new StockTransferService(db, TransferWorld.Everything()).SubmitAsync(t.Id, w.W.UserId);
        await w.ApproveAsync(t.Id);
        await SetAsync(w, t.Id, StockTransferStatus.PartiallyFulfilled, 30m, 30m);
        await CloseAsync(w, t.Id, "short");

        var (db2, controller) = Controller(w, w.Main, new HashSet<Guid> { w.Main }, TransferWorld.Everything());
        await using var _ = db2;
        var history = Ok(await controller.History(t.Id, default));

        Assert.Equal(
            [AuditActions.StockTransferCreated, AuditActions.StockTransferSubmitted, AuditActions.StockTransferApproved, AuditActions.StockTransferClosed],
            history.Select(h => h.Action));
        Assert.Equal((null, StockTransferStatus.Draft), (history[0].FromStatus, history[0].ToStatus));
        Assert.Equal((StockTransferStatus.Requested, StockTransferStatus.Approved), (history[2].FromStatus, history[2].ToStatus));
        Assert.Equal((StockTransferStatus.PartiallyFulfilled, StockTransferStatus.Closed, "short"), (history[3].FromStatus, history[3].ToStatus, history[3].Reason));
        Assert.All(history, h => Assert.Equal("R K", h.ActorName));
    }

    [Fact]
    public async Task History_OfATransferTheCallerCannotSee_Is404()
    {
        var w = await TransferWorld.CreateAsync();
        var t = await w.CreateTransferAsync(w.L.BlrStore, w.L.BlrWarehouse, [w.Pen(w.Box, 1m)], submit: true, branch: w.Blr);

        var (db, controller) = Controller(w, w.Main, new HashSet<Guid> { w.Main }, TransferWorld.Everything());
        await using var _ = db;
        Assert.IsType<NotFoundResult>((await controller.History(t.Id, default)).Result);
        Assert.IsType<NotFoundResult>((await controller.History(Guid.NewGuid(), default)).Result);
    }

    // ---- detail: line quantities, closure, cost visibility ------------------------------------------------------------------------------

    [Fact]
    public async Task Detail_ExposesClosureAndLineQuantities_AndTheNewStatusesAreFilterable()
    {
        var (w, t) = await PartialAsync(dispatched: 30m, allocated: 30m);
        await CloseAsync(w, t.Id, "short");

        var (db, controller) = Controller(w, w.Main, null, TransferWorld.Everything());
        await using var _ = db;
        var dto = Ok(await controller.Get(t.Id, default));
        var line = dto.Lines.Single();
        Assert.Equal((StockTransferStatus.Closed, "short", "R K"), (dto.Status, dto.ClosureReason, dto.ClosedByName));
        Assert.NotNull(dto.ClosedAt);
        Assert.Equal((30m, 70m, 0m), (line.AllocatedBaseQty, line.ClosedBaseQty, line.RemainingBaseQty));

        var closed = Ok(await controller.List(StockTransferStatus.Closed, null, null, null, null, null));
        Assert.Equal([t.Id], closed.Items.Select(i => i.Id));
        Assert.Empty(Ok(await controller.List(StockTransferStatus.PartiallyFulfilled, null, null, null, null, null)).Items);
    }

    [Fact]
    public async Task Detail_PartlyFulfilledLine_RemainingIsRequestedMinusClosedMinusDispatched()
    {
        var (w, t) = await PartialAsync(dispatched: 30m, allocated: 50m);
        var (db, controller) = Controller(w, w.Main, null, TransferWorld.Everything());
        await using var _ = db;
        var line = Ok(await controller.Get(t.Id, default)).Lines.Single();
        Assert.Equal((50m, 0m, 70m), (line.AllocatedBaseQty, line.ClosedBaseQty, line.RemainingBaseQty));
    }

    [Fact]
    public async Task Detail_BatchCost_IsHiddenFromAReaderWhoCannotAccessTheSourceBranch_AndShownToTheSourceAndCompany()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m, cost: 5m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 4m));
        await w.DispatchAsRequestedAsync(t.Id);

        var (destDb, destination) = Controller(w, w.Blr, new HashSet<Guid> { w.Blr }, TransferWorld.Everything());
        await using (destDb)
        {
            var batches = Ok(await destination.Get(t.Id, default)).Lines.Single().Batches;
            Assert.NotEmpty(batches);
            Assert.All(batches, b => Assert.Null(b.UnitCost));
            Assert.All(batches, b => Assert.True(b.DispatchedBaseQty > 0)); // everything else is still shown
        }

        var (sourceDb, source) = Controller(w, w.Main, new HashSet<Guid> { w.Main }, TransferWorld.Everything());
        await using (sourceDb) Assert.All(Ok(await source.Get(t.Id, default)).Lines.Single().Batches, b => Assert.Equal(5m, b.UnitCost));

        var (companyDb, company) = Controller(w, w.Blr, null, TransferWorld.Everything());
        await using (companyDb) Assert.All(Ok(await company.Get(t.Id, default)).Lines.Single().Batches, b => Assert.Equal(5m, b.UnitCost));
    }

    [Fact]
    public async Task Detail_BatchCost_FromACompanyLevelSource_IsShownOnlyToACompanyLevelReader()
    {
        // Review amendment (FI-D28): a central warehouse has no source branch; its costs are company information.
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.CompanyWarehouse, 100m, cost: 5m);
        var t = await w.ApprovedAsync(w.L.CompanyWarehouse, w.L.BlrStore, w.Pen(w.Box, 4m));
        await w.DispatchAsRequestedAsync(t.Id);

        var (destDb, destination) = Controller(w, w.Blr, new HashSet<Guid> { w.Blr }, TransferWorld.Everything());
        await using (destDb) Assert.All(Ok(await destination.Get(t.Id, default)).Lines.Single().Batches, b => Assert.Null(b.UnitCost));

        var (companyDb, company) = Controller(w, w.Blr, null, TransferWorld.Everything());
        await using (companyDb) Assert.All(Ok(await company.Get(t.Id, default)).Lines.Single().Batches, b => Assert.Equal(5m, b.UnitCost));
    }
}
