using Sellora.Domain.Abstractions;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>
/// Transfer approval controls (FI-D46/FI-D47): maker-checker (neither creator nor submitter may approve under the request's submit-time
/// snapshot of BusinessSettings.TransferApprovalSeparationOfDuties) and approving less than requested (the cut is closed, with a reason).
/// </summary>
public class TransferApprovalControlsTests
{
    private static async Task SetSeparationAsync(TransferWorld w, bool on)
    {
        await using var db = w.Db();
        (await db.BusinessSettings.SingleAsync()).TransferApprovalSeparationOfDuties = on;
        await db.SaveChangesAsync();
    }

    private static async Task<StockTransfer?> ApproveAsAsync(TransferWorld w, Guid id, Guid userId, ApproveStockTransferInput? input = null)
    {
        await using var db = w.Db(w.Main);
        return await new StockTransferService(db, TransferWorld.Everything()).ApproveAsync(id, input ?? new ApproveStockTransferInput(), userId);
    }

    private static async Task<string?> FieldOrNullAsync(Func<Task> act)
    {
        try { await act(); return null; }
        catch (DomainValidationException ex) { return ex.Field; }
    }

    // ---- maker-checker ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Separation_TheCreatorCannotApprove_AnotherUserCan()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m);
        await SetSeparationAsync(w, true);
        var t = await w.RequestedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 1m));
        Assert.True((await w.ReloadAsync(t.Id)).RequireApprovalSeparation);

        Assert.Equal("status", await FieldOrNullAsync(() => ApproveAsAsync(w, t.Id, w.W.UserId)));
        Assert.Equal(StockTransferStatus.Requested, (await w.ReloadAsync(t.Id)).Status);

        var checker = Guid.NewGuid();
        var approved = await ApproveAsAsync(w, t.Id, checker);
        Assert.Equal(StockTransferStatus.Approved, approved!.Status);
        Assert.Equal(checker, approved.ApprovedByUserId);
    }

    [Fact]
    public async Task Separation_TheSubmitterOfADraftCannotApproveEither()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m);
        await SetSeparationAsync(w, true);
        var draft = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m)]);
        var submitter = Guid.NewGuid();
        await using (var db = w.Db(w.Main))
        {
            await new StockTransferService(db, TransferWorld.Everything()).SubmitAsync(draft.Id, submitter);
        }

        Assert.Equal("status", await FieldOrNullAsync(() => ApproveAsAsync(w, draft.Id, submitter)));   // submitter
        Assert.Equal("status", await FieldOrNullAsync(() => ApproveAsAsync(w, draft.Id, w.W.UserId)));  // creator
        Assert.NotNull(await ApproveAsAsync(w, draft.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task Separation_IsSnapshottedAtSubmit_LaterSettingChangesNeverAlterAPendingRequest()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m);

        await SetSeparationAsync(w, false);
        var before = await w.RequestedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 1m));
        await SetSeparationAsync(w, true);
        var after = await w.RequestedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 1m));
        await SetSeparationAsync(w, false);

        Assert.NotNull(await ApproveAsAsync(w, before.Id, w.W.UserId));                                   // submitted while off
        Assert.Equal("status", await FieldOrNullAsync(() => ApproveAsAsync(w, after.Id, w.W.UserId)));    // submitted while on
    }

    [Fact]
    public async Task Separation_Off_TheCreatorMayApprove_AndRejectIsNeverBlocked()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m);
        var t = await w.RequestedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 1m));
        Assert.NotNull(await ApproveAsAsync(w, t.Id, w.W.UserId));

        await SetSeparationAsync(w, true);
        var own = await w.RequestedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 1m));
        await using var db = w.Db(w.Main);
        var rejected = await new StockTransferService(db, TransferWorld.Everything()).RejectAsync(own.Id, "not needed", w.W.UserId);
        Assert.Equal(StockTransferStatus.Rejected, rejected!.Status);
    }

    // ---- approving less than requested -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Reduce_ClosesTheCut_StoresTheApprovedSnapshot_AndKeepsOtherLinesAsRequested()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m);
        var t = await w.RequestedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 3m), w.Pen(w.Pc, 4m));
        var (boxLine, pcLine) = (t.Lines.Single(l => l.RequestedQty == 3m), t.Lines.Single(l => l.RequestedQty == 4m));

        var approved = await ApproveAsAsync(w, t.Id, w.W.UserId, new ApproveStockTransferInput([new(boxLine.Id, 1m)], "Only one box spare"));

        var box = (await w.ReloadAsync(t.Id)).Lines.Single(l => l.Id == boxLine.Id);
        Assert.Equal(1m, box.ApprovedQty);
        Assert.Equal(boxLine.ConversionFactorToBase, box.ApprovedBaseQty);
        Assert.Equal(boxLine.RequestedBaseQty - boxLine.ConversionFactorToBase, box.ClosedBaseQty);
        var pc = (await w.ReloadAsync(t.Id)).Lines.Single(l => l.Id == pcLine.Id);
        Assert.Equal(4m, pc.ApprovedQty);
        Assert.Equal(0m, pc.ClosedBaseQty);
        Assert.Equal("Only one box spare", approved!.ApprovalAdjustmentReason);
    }

    [Fact]
    public async Task Reduce_ALineToZero_IsAllowed_ButEveryLineAtZeroMeansReject()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m);
        var t = await w.RequestedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 3m), w.Pen(w.Pc, 4m));
        var ids = t.Lines.Select(l => l.Id).ToArray();

        Assert.Equal("lines", await FieldOrNullAsync(() => ApproveAsAsync(w, t.Id, w.W.UserId, new([new(ids[0], 0m), new(ids[1], 0m)], "none spare"))));
        Assert.NotNull(await ApproveAsAsync(w, t.Id, w.W.UserId, new([new(ids[0], 0m)], "out of boxes")));
        var zero = (await w.ReloadAsync(t.Id)).Lines.Single(l => l.Id == ids[0]);
        Assert.Equal(zero.RequestedBaseQty, zero.ClosedBaseQty);
    }

    [Theory]
    [InlineData(5, "Short", "lines[0].quantity")]     // above requested
    [InlineData(-1, "Short", "lines[0].quantity")]    // negative
    [InlineData(1.5, "Short", "lines[0].quantity")]   // a box is whole-numbered
    [InlineData(1, null, "reason")]                   // a reduction needs a reason
    public async Task Reduce_InvalidInput_IsRefused_AndChangesNothing(double qty, string? reason, string field)
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m);
        var t = await w.RequestedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 3m));

        Assert.Equal(field, await FieldOrNullAsync(() => ApproveAsAsync(w, t.Id, w.W.UserId, new([new(t.Lines.Single().Id, (decimal)qty)], reason))));
        var after = await w.ReloadAsync(t.Id);
        Assert.Equal(StockTransferStatus.Requested, after.Status);
        Assert.Null(after.Lines.Single().ApprovedQty);
        Assert.Equal(0m, after.Lines.Single().ClosedBaseQty);
    }

    [Fact]
    public async Task Reduce_UnknownOrDuplicateLine_IsRefused()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m);
        var t = await w.RequestedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 3m));
        var id = t.Lines.Single().Id;

        Assert.Equal("lines[0].lineId", await FieldOrNullAsync(() => ApproveAsAsync(w, t.Id, w.W.UserId, new([new(Guid.NewGuid(), 1m)], "x"))));
        Assert.Equal("lines", await FieldOrNullAsync(() => ApproveAsAsync(w, t.Id, w.W.UserId, new([new(id, 1m), new(id, 2m)], "x"))));
    }

    [Fact]
    public async Task Approve_AsRequested_NeedsNoReason_AndRecordsTheFullApprovedQuantity()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m);
        var t = await w.RequestedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 3m));

        var approved = await ApproveAsAsync(w, t.Id, w.W.UserId, new([new(t.Lines.Single().Id, 3m)]));
        Assert.Null(approved!.ApprovalAdjustmentReason);
        var line = (await w.ReloadAsync(t.Id)).Lines.Single();
        Assert.Equal(3m, line.ApprovedQty);
        Assert.Equal(line.RequestedBaseQty, line.ApprovedBaseQty);
        Assert.Equal(0m, line.ClosedBaseQty);
    }
}
