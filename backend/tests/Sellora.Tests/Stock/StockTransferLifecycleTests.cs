using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>
/// Phase 9 D2: the transfer state machine on EF InMemory. Every operation is exercised from every status: the allowed transition, the
/// state-idempotent repeat (submit / approve / reject / cancel return the transfer unchanged), or 400 on field "status". Also create / edit
/// draft and idempotent replay of create / dispatch / receipt / resolve (locks and races: PostgreSQL tests).
/// </summary>
public class StockTransferLifecycleTests
{
    private const string Ok = "ok";
    private const string Same = "same";
    private const string Refused = "400";

    // (operation, status the transfer is brought to, outcome)
    public static IEnumerable<object[]> Matrix()
    {
        var all = Enum.GetValues<StockTransferStatus>();
        var allowed = new Dictionary<string, (StockTransferStatus[] Ok, StockTransferStatus? Same)>
        {
            ["submit"] = ([StockTransferStatus.Draft], StockTransferStatus.Requested),
            ["approve"] = ([StockTransferStatus.Requested], StockTransferStatus.Approved),
            ["reject"] = ([StockTransferStatus.Requested], StockTransferStatus.Rejected),
            ["cancel"] = ([StockTransferStatus.Draft, StockTransferStatus.Requested, StockTransferStatus.Approved], StockTransferStatus.Cancelled),
            ["dispatch"] = ([StockTransferStatus.Approved], null),
            ["receive"] = ([StockTransferStatus.Dispatched, StockTransferStatus.PartiallyReceived], null),
            ["resolve"] = ([StockTransferStatus.Dispatched, StockTransferStatus.PartiallyReceived], null),
        };
        foreach (var (op, rule) in allowed)
        {
            foreach (var status in all)
            {
                yield return [op, status, rule.Ok.Contains(status) ? Ok : rule.Same == status ? Same : Refused];
            }
        }
    }

    // Brings a fresh 100-pc transfer (10 Box) Main store -> Blr store to the status.
    internal static async Task<(TransferWorld W, StockTransfer T)> InAsync(StockTransferStatus status)
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 200m);
        var t = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 10m)]);
        if (status == StockTransferStatus.Draft) return (w, t);
        if (status == StockTransferStatus.Cancelled)
        {
            await using var db = w.Db();
            return (w, (await new StockTransferService(db, TransferWorld.Everything()).CancelAsync(t.Id, "no", w.W.UserId))!);
        }
        await using (var db = w.Db()) await new StockTransferService(db, TransferWorld.Everything()).SubmitAsync(t.Id, w.W.UserId);
        if (status == StockTransferStatus.Requested) return (w, await w.ReloadAsync(t.Id));
        if (status == StockTransferStatus.Rejected)
        {
            await using var db = w.Db();
            return (w, (await new StockTransferService(db, TransferWorld.Everything()).RejectAsync(t.Id, "no", w.W.UserId))!);
        }
        await w.ApproveAsync(t.Id);
        if (status == StockTransferStatus.Approved) return (w, await w.ReloadAsync(t.Id));
        await w.DispatchAsRequestedAsync(t.Id);
        var line = (await w.ReloadAsync(t.Id)).Lines.Single();
        if (status == StockTransferStatus.PartiallyReceived) await w.ReceiveAsync(t.Id, line.Id, w.Box, 3m);
        if (status == StockTransferStatus.Completed) await w.ReceiveAsync(t.Id, line.Id, w.Box, 10m);
        return (w, await w.ReloadAsync(t.Id));
    }

    private static async Task<StockTransfer?> RunAsync(TransferWorld w, string op, StockTransfer t)
    {
        var line = t.Lines.Single();
        await using var db = w.Db();
        var service = new StockTransferService(db, TransferWorld.Everything());
        switch (op)
        {
            case "submit": return await service.SubmitAsync(t.Id, w.W.UserId);
            case "approve": return await service.ApproveAsync(t.Id, w.W.UserId);
            case "reject": return await service.RejectAsync(t.Id, "no", w.W.UserId);
            case "cancel": return await service.CancelAsync(t.Id, "changed mind", w.W.UserId);
            case "dispatch": return await service.DispatchAsync(t.Id, new DispatchStockTransferInput([new DispatchStockTransferLineInput(line.Id, w.Box, 10m)]), w.W.UserId);
            case "receive": return (await service.ReceiveAsync(t.Id, new ReceiveStockTransferInput(null, [new ReceiveStockTransferLineInput(line.Id, w.Box, 1m, 0m)]), w.W.UserId))?.Transfer;
            case "resolve": return (await service.ResolveAsync(t.Id, new ResolveStockTransferInput("loss", [new ResolveStockTransferLineInput(line.Id, StockTransferResolution.WriteOff)]), w.W.UserId))?.Transfer;
            default: throw new ArgumentException(op);
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Operation_FromEveryStatus_IsAllowed_AnIdempotentRepeat_OrRefusedOnStatus(string op, StockTransferStatus from, string outcome)
    {
        var (w, t) = await InAsync(from);
        Assert.Equal(from, t.Status);
        var before = await w.ReloadAsync(t.Id);
        var (movementsBefore, qohBefore) = ((await w.MovementsAsync(t.Id)).Count, await w.CompanyQuantityAsync());

        if (outcome == Refused)
        {
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() => RunAsync(w, op, before));
            Assert.Equal("status", ex.Field);
            var after = await w.ReloadAsync(t.Id);
            Assert.Equal(from, after.Status);
            Assert.Equal(movementsBefore, (await w.MovementsAsync(t.Id)).Count);
            Assert.Equal(qohBefore, await w.CompanyQuantityAsync());
        }
        else if (outcome == Same)
        {
            var result = await RunAsync(w, op, before);
            Assert.Equal(from, result!.Status);
            var after = await w.ReloadAsync(t.Id);
            // Nothing is re-stamped: the first actor/time stay.
            Assert.Equal((before.SubmittedAt, before.ApprovedAt, before.RejectedAt, before.CancelledAt), (after.SubmittedAt, after.ApprovedAt, after.RejectedAt, after.CancelledAt));
            Assert.Equal(movementsBefore, (await w.MovementsAsync(t.Id)).Count);
        }
        else
        {
            var result = await RunAsync(w, op, before);
            Assert.NotNull(result);
            if (op is "receive" or "resolve") Assert.Equal(before.Receipts.Count + 1, result.Receipts.Count); // a receipt is an event; the status may stay PartiallyReceived
            else Assert.NotEqual(from, result.Status);
        }
        await w.AssertInvariantsAsync($"{op} from {from}");
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("cancel")]
    [InlineData("dispatch")]
    [InlineData("receive")]
    [InlineData("resolve")]
    [InlineData("submit")]
    public async Task Operation_OnUnknownTransfer_IsNull(string op)
    {
        var w = await TransferWorld.CreateAsync();
        var ghost = new StockTransfer { Id = Guid.NewGuid(), TransferNo = "x", Lines = [new StockTransferLine { Id = Guid.NewGuid(), UnitCode = "box" }] };
        Assert.Null(await RunAsync(w, op, ghost));
    }

    [Fact]
    public async Task Create_Draft_And_SubmitTrue_StartInTheRightStatus_WithATrfNumberPerBranch()
    {
        var w = await TransferWorld.CreateAsync();
        var draft = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m)]);
        var requested = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m)], submit: true);
        var blr = await w.CreateTransferAsync(w.L.BlrStore, w.L.MainStore, [w.Pen(w.Box, 1m)], branch: w.Blr);

        Assert.Equal((StockTransferStatus.Draft, StockTransferStatus.Requested), (draft.Status, requested.Status));
        Assert.Null(draft.SubmittedAt);
        Assert.NotNull(requested.SubmittedAt);
        Assert.StartsWith("TRF", draft.TransferNo);
        Assert.NotEqual(draft.TransferNo, requested.TransferNo);
        Assert.Equal((w.Main, w.Blr), (draft.BranchId, blr.BranchId));
        Assert.Equal((w.Main, w.Blr), (draft.SourceBranchId, draft.DestinationBranchId));
        Assert.Equal(w.L.Transit, draft.TransitLocationId);
        // Creating moves nothing.
        Assert.Empty(await w.MovementsAsync(draft.Id));
    }

    [Fact]
    public async Task Create_EndpointRules_SameTransitUnknownInactive_AreRefusedOnTheEndpointField()
    {
        var w = await TransferWorld.CreateAsync();
        var line = new[] { w.Pen(w.Box, 1m) };
        Assert.Equal("destinationLocationId", await LocationWorld.FieldAsync(() => w.CreateTransferAsync(w.L.MainStore, w.L.MainStore, line)));
        Assert.Equal("destinationLocationId", await LocationWorld.FieldAsync(() => w.CreateTransferAsync(w.L.MainStore, w.L.Transit, line)));
        Assert.Equal("sourceLocationId", await LocationWorld.FieldAsync(() => w.CreateTransferAsync(w.L.Transit, w.L.MainStore, line)));
        Assert.Equal("sourceLocationId", await LocationWorld.FieldAsync(() => w.CreateTransferAsync(Guid.NewGuid(), w.L.MainStore, line)));
        await using (var db = w.Db())
        {
            (await db.StockLocations.SingleAsync(l => l.Id == w.L.BlrWarehouse)).IsActive = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal("destinationLocationId", await LocationWorld.FieldAsync(() => w.CreateTransferAsync(w.L.MainStore, w.L.BlrWarehouse, line)));
        Assert.Equal(0, await w.TransferCountAsync());
    }

    [Fact]
    public async Task Create_LineRules_NoLines_ZeroQuantity_WrongUnit_InactiveUnit_AreRefusedOnTheLineField()
    {
        var w = await TransferWorld.CreateAsync();
        Assert.Equal("lines", await LocationWorld.FieldAsync(() => w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [])));
        Assert.Equal("lines[0].quantity", await LocationWorld.FieldAsync(() => w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 0m)])));
        Assert.Equal("lines[1].productUnitId", await LocationWorld.FieldAsync(() => w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m), w.Pen(w.FlourKg, 1m)])));
        Assert.Equal("lines[0].productId", await LocationWorld.FieldAsync(() => w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [new StockTransferLineInput(Guid.NewGuid(), w.Box, 1m)])));
        await using (var db = w.Db())
        {
            (await db.ProductUnits.SingleAsync(u => u.Id == w.Full)).IsActive = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal("lines[0].productUnitId", await LocationWorld.FieldAsync(() => w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Full, 1m)])));
        Assert.Equal(0, await w.TransferCountAsync());
    }

    // ---- draft edit ---------------------------------------------------------------------------------------------------------------

    // DEFECT (reported, product code untouched): StockTransferService.UpdateDraftAsync adds new lines (Id and CompanyId pre-set / empty) to the tracked
    // transfer's collection after deleting the old ones; EF fix-up treats them as existing rows and throws "StockTransferLine.CompanyId is part of a key
    // and cannot be modified". Expected to pass once the new lines are added through db.StockTransferLines.Add(Range) (state Added).
    [Fact]
    public async Task UpdateDraft_ReplacesEndpointsNotesAndLines_WithFreshLineNumbers()
    {
        var w = await TransferWorld.CreateAsync();
        var t = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m), w.Pen(w.Half, 2m)]);
        var oldLineIds = t.Lines.Select(l => l.Id).ToHashSet();

        await using (var db = w.Db())
        {
            var updated = await new StockTransferService(db, TransferWorld.Everything()).UpdateDraftAsync(t.Id,
                new UpdateStockTransferInput(w.L.MainStore, w.L.BlrWarehouse, "edited", [w.Pen(w.Pc, 7m), w.Pen(w.Full, 1m), w.Pen(w.Box, 4m)]), w.W.UserId);
            Assert.Equal(StockTransferStatus.Draft, updated!.Status);
        }

        var after = await w.ReloadAsync(t.Id);
        Assert.Equal(w.L.BlrWarehouse, after.DestinationLocationId);
        Assert.Equal("edited", after.Notes);
        Assert.Equal([1, 2, 3], after.Lines.OrderBy(l => l.LineNo).Select(l => l.LineNo));
        Assert.Equal([7m, 100m, 40m], after.Lines.OrderBy(l => l.LineNo).Select(l => l.RequestedBaseQty));
        Assert.DoesNotContain(after.Lines, l => oldLineIds.Contains(l.Id));
        await using var check = w.Db();
        Assert.Equal(3, await check.StockTransferLines.CountAsync());
    }

    [Theory]
    [InlineData(StockTransferStatus.Requested)]
    [InlineData(StockTransferStatus.Approved)]
    [InlineData(StockTransferStatus.Dispatched)]
    [InlineData(StockTransferStatus.Cancelled)]
    public async Task UpdateDraft_WhenNotDraft_IsRefusedOnStatus_AndChangesNothing(StockTransferStatus status)
    {
        var (w, t) = await InAsync(status);
        await using var db = w.Db();
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new StockTransferService(db, TransferWorld.Everything()).UpdateDraftAsync(t.Id,
            new UpdateStockTransferInput(w.L.MainStore, w.L.BlrStore, "x", [w.Pen(w.Pc, 1m)]), w.W.UserId));
        Assert.Equal("status", ex.Field);
        Assert.Equal(10m, (await w.ReloadAsync(t.Id)).Lines.Single().RequestedQty);
    }

    // ---- idempotent replay ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ReplayedWithTheSameKey_ReturnsTheStoredTransfer_AndCreatesNoSecondOne()
    {
        var w = await TransferWorld.CreateAsync();
        var first = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m)], key: "k-create");
        var second = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 9m)], key: "k-create"); // even a different body: the stored result wins

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.TransferNo, second.TransferNo);
        Assert.Equal(1m, second.Lines.Single().RequestedQty);
        Assert.Equal(1, await w.TransferCountAsync());
        var other = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m)], key: "k-other");
        Assert.NotEqual(first.Id, other.Id);
    }

    [Fact]
    public async Task Dispatch_ReplayedWithTheSameKey_MovesStockOnce_AndReturnsTheTransfer()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 200m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 10m));

        var first = await w.DispatchAsRequestedAsync(t.Id, key: "k-d");
        var replay = await w.DispatchAsRequestedAsync(t.Id, key: "k-d"); // the transfer is Dispatched now: without the key this would be 400

        Assert.Equal(StockTransferStatus.Dispatched, replay!.Status);
        Assert.Equal(first!.DispatchedAt, replay.DispatchedAt);
        Assert.Equal((100m, 100m), (await w.QohAsync(w.L.MainStore), await w.QohAsync(w.L.Transit)));
        Assert.Equal(2, (await w.MovementsAsync(t.Id)).Count);
        await w.AssertInvariantsAsync("dispatch replay");
    }

    [Fact]
    public async Task Dispatch_WithoutKey_SecondCall_IsRefusedOnStatus_AndMovesNothing()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 200m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 10m));
        await w.DispatchAsRequestedAsync(t.Id);

        Assert.Equal("status", await LocationWorld.FieldAsync(() => w.DispatchAsRequestedAsync(t.Id)));
        Assert.Equal((100m, 100m), (await w.QohAsync(w.L.MainStore), await w.QohAsync(w.L.Transit)));
    }

    [Fact]
    public async Task Dispatch_KeyReusedOnAnotherTransfer_Is400OnIdempotencyKey_AndMovesNothing()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 200m);
        var a = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 10m));
        var b = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 5m));
        await w.DispatchAsRequestedAsync(a.Id, key: "shared");

        Assert.Equal("IdempotencyKey", await LocationWorld.FieldAsync(() => w.DispatchAsRequestedAsync(b.Id, key: "shared")));
        Assert.Equal(StockTransferStatus.Approved, (await w.ReloadAsync(b.Id)).Status);
        Assert.Equal(100m, await w.QohAsync(w.L.Transit));
    }

    [Fact]
    public async Task Receive_ReplayedWithTheSameKey_ReturnsTheSameReceipt_AndPostsOnce()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 200m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 10m));
        await w.DispatchAsRequestedAsync(t.Id);
        var line = (await w.ReloadAsync(t.Id)).Lines.Single();

        var first = await w.ReceiveAsync(t.Id, line.Id, w.Box, 4m, key: "k-r");
        var replay = await w.ReceiveAsync(t.Id, line.Id, w.Box, 4m, key: "k-r");

        Assert.Equal(first!.ReceiptId, replay!.ReceiptId);
        Assert.Equal((40m, 60m), (await w.QohAsync(w.L.BlrStore), await w.QohAsync(w.L.Transit)));
        Assert.Single((await w.ReloadAsync(t.Id)).Receipts);
        await w.AssertInvariantsAsync("receive replay");
    }

    [Fact]
    public async Task Receive_KeyReusedOnAnotherTransfer_Is400OnIdempotencyKey()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 200m);
        var a = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 10m));
        var b = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 5m));
        await w.DispatchAsRequestedAsync(a.Id);
        await w.DispatchAsRequestedAsync(b.Id);
        await w.ReceiveAsync(a.Id, (await w.ReloadAsync(a.Id)).Lines.Single().Id, w.Box, 1m, key: "shared");

        var lineB = (await w.ReloadAsync(b.Id)).Lines.Single();
        Assert.Equal("IdempotencyKey", await LocationWorld.FieldAsync(() => w.ReceiveAsync(b.Id, lineB.Id, w.Box, 1m, key: "shared")));
        Assert.Equal(0m, await w.QohAsync(w.L.BlrStore) - 10m);
        Assert.Empty((await w.ReloadAsync(b.Id)).Receipts);
    }

    [Fact]
    public async Task Resolve_ReplayedWithTheSameKey_ReturnsTheSameResolution_AndKeyOnAnotherTransferIs400()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 300m);
        var a = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 10m));
        var b = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 10m));
        await w.DispatchAsRequestedAsync(a.Id);
        await w.DispatchAsRequestedAsync(b.Id);
        var lineA = (await w.ReloadAsync(a.Id)).Lines.Single();
        var lineB = (await w.ReloadAsync(b.Id)).Lines.Single();
        ResolveStockTransferInput Input(Guid line, string? key) => new("lost", [new ResolveStockTransferLineInput(line, StockTransferResolution.WriteOff)], key);

        var first = await w.ResolveAsync(a.Id, Input(lineA.Id, "k-x"));
        var replay = await w.ResolveAsync(a.Id, Input(lineA.Id, "k-x")); // Completed now: without the key this would be 400 status

        Assert.Equal(first!.ReceiptId, replay!.ReceiptId);
        Assert.Equal(StockTransferStatus.Completed, replay.Transfer.Status);
        Assert.Equal(100m, await w.QohAsync(w.L.Transit)); // only b's 100 remains in transit
        Assert.Equal("IdempotencyKey", await LocationWorld.FieldAsync(() => w.ResolveAsync(b.Id, Input(lineB.Id, "k-x"))));
        Assert.Equal(StockTransferStatus.Dispatched, (await w.ReloadAsync(b.Id)).Status);
        await w.AssertInvariantsAsync("resolve replay");
    }

    [Fact]
    public async Task Transfer_OutsideTheCallersBranches_IsNull_ForEveryOperation()
    {
        var w = await TransferWorld.CreateAsync();
        var t = await w.CreateTransferAsync(w.L.BlrStore, w.L.BlrWarehouse, [w.Pen(w.Box, 1m)], submit: true, branch: w.Blr); // BLR to BLR, created at BLR
        var hidden = new HashSet<Guid> { w.Main };

        await using var db = w.Db(w.Main, hidden);
        var service = new StockTransferService(db, TransferWorld.Everything());
        Assert.Null(await service.ApproveAsync(t.Id, w.W.UserId));
        Assert.Null(await service.RejectAsync(t.Id, "x", w.W.UserId));
        Assert.Null(await service.CancelAsync(t.Id, "x", w.W.UserId));
        Assert.Null(await service.SubmitAsync(t.Id, w.W.UserId));
        Assert.Equal(StockTransferStatus.Requested, (await w.ReloadAsync(t.Id)).Status);
    }
}
