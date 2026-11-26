using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>
/// Two-level transfer approval (FI-D48..D51): requests whose estimated value (requested base qty x source average cost) reaches the company
/// threshold wait for the requesting (destination) branch first; the two levels are different people; maker-checker applies to both.
/// Main store -> BLR store: Main is the source (level 2), BLR the requester (level 1).
/// </summary>
public class TransferRequesterApprovalTests
{
    private static async Task<TransferWorld> WorldAsync(decimal? threshold, bool separation = false)
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m, cost: 5m); // 5 per piece at the source
        await using var db = w.Db();
        var s = await db.BusinessSettings.SingleAsync();
        s.TransferRequesterApprovalThreshold = threshold;
        s.TransferApprovalSeparationOfDuties = separation;
        await db.SaveChangesAsync();
        return w;
    }

    private static async Task<StockTransfer> RequestAsync(TransferWorld w, decimal pieces) =>
        await w.RequestedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Pc, pieces));

    private static async Task<StockTransfer?> RunAsync(TransferWorld w, Guid branch, IEffectivePermissionService p, Func<StockTransferService, Task<StockTransfer?>> act)
    {
        await using var db = w.Db(branch);
        return await act(new StockTransferService(db, p));
    }

    private static async Task<string?> FieldOrNullAsync(Func<Task> act)
    {
        try { await act(); return null; }
        catch (DomainValidationException ex) { return ex.Field; }
    }

    [Theory]
    [InlineData(null, 10, false)]   // off
    [InlineData(100.0, 10, false)]    // 10 pc x 5 = 50 < 100
    [InlineData(50.0, 10, true)]      // exactly the threshold
    [InlineData(0.0, 1, true)]        // threshold 0 = every request
    public async Task Submit_RoutesByTheEstimatedValue(double? threshold, int pieces, bool level1)
    {
        var w = await WorldAsync(threshold is { } t ? (decimal)t : null);
        var r = await RequestAsync(w, pieces);

        Assert.Equal(pieces * 5m, r.EstimatedValue);
        Assert.Equal(level1, r.RequiresRequesterApproval);
        Assert.Equal(level1 ? StockTransferStatus.AwaitingRequesterApproval : StockTransferStatus.Requested, r.Status);
    }

    [Fact]
    public async Task Submit_SnapshotsTheRouting_LaterThresholdOrCostChangesNeverReRouteIt()
    {
        var w = await WorldAsync(40m);
        var r = await RequestAsync(w, 10); // 50 >= 40
        await using (var db = w.Db())
        {
            (await db.BusinessSettings.SingleAsync()).TransferRequesterApprovalThreshold = null;
            (await db.StockItems.SingleAsync(i => i.StockLocationId == w.L.MainStore && i.ProductId == w.PenId)).AverageCost = 1m;
            await db.SaveChangesAsync();
        }
        var after = await w.ReloadAsync(r.Id);
        Assert.Equal(StockTransferStatus.AwaitingRequesterApproval, after.Status);
        Assert.Equal(50m, after.EstimatedValue);
    }

    [Fact]
    public async Task Level1_IsTheDestinationBranchs_AndTheSourceCannotSkipIt()
    {
        var w = await WorldAsync(0m);
        var r = await RequestAsync(w, 10);
        var mainApprover = FakeScopedPermissions.AtBranch(w.Main, "StockTransfers.ApproveRequest", "StockTransfers.Approve");
        var blrRequester = FakeScopedPermissions.AtBranch(w.Blr, "StockTransfers.ApproveRequest");

        Assert.Equal("stockLocationId", await FieldOrNullAsync(() => RunAsync(w, w.Main, mainApprover, s => s.RequesterApproveAsync(r.Id, Guid.NewGuid()))));
        Assert.Equal("status", await FieldOrNullAsync(() => RunAsync(w, w.Main, mainApprover, s => s.ApproveAsync(r.Id, Guid.NewGuid()))));   // level 2 waits

        var level1 = Guid.NewGuid();
        var approved = await RunAsync(w, w.Blr, blrRequester, s => s.RequesterApproveAsync(r.Id, level1));
        Assert.Equal(StockTransferStatus.Requested, approved!.Status);
        Assert.Equal(level1, approved.RequesterApprovedByUserId);

        var final = await RunAsync(w, w.Main, mainApprover, s => s.ApproveAsync(r.Id, Guid.NewGuid()));
        Assert.Equal(StockTransferStatus.Approved, final!.Status);
    }

    [Fact]
    public async Task TheTwoLevels_AreTwoDifferentPeople_EvenWithSeparationOff()
    {
        var w = await WorldAsync(0m, separation: false);
        var r = await RequestAsync(w, 10);
        var both = Guid.NewGuid();
        await RunAsync(w, w.Blr, TransferWorld.Everything(), s => s.RequesterApproveAsync(r.Id, both));

        Assert.Equal("status", await FieldOrNullAsync(() => RunAsync(w, w.Main, TransferWorld.Everything(), s => s.ApproveAsync(r.Id, both))));
        Assert.Equal(StockTransferStatus.Approved, (await RunAsync(w, w.Main, TransferWorld.Everything(), s => s.ApproveAsync(r.Id, Guid.NewGuid())))!.Status);
    }

    [Fact]
    public async Task MakerChecker_AppliesToLevel1()
    {
        var w = await WorldAsync(0m, separation: true);
        var r = await RequestAsync(w, 10); // created and submitted by w.W.UserId

        Assert.Equal("status", await FieldOrNullAsync(() => RunAsync(w, w.Blr, TransferWorld.Everything(), s => s.RequesterApproveAsync(r.Id, w.W.UserId))));
        Assert.Equal(StockTransferStatus.Requested, (await RunAsync(w, w.Blr, TransferWorld.Everything(), s => s.RequesterApproveAsync(r.Id, Guid.NewGuid())))!.Status);
    }

    [Fact]
    public async Task Level1_Reject_EndsTheRequest_WithAReason_AndTheCreatorMayCancelWhileItWaits()
    {
        var w = await WorldAsync(0m);
        var rejected = await RequestAsync(w, 10);
        Assert.Equal("reason", await FieldOrNullAsync(() => RunAsync(w, w.Blr, TransferWorld.Everything(), s => s.RequesterRejectAsync(rejected.Id, " ", Guid.NewGuid()))));
        var done = await RunAsync(w, w.Blr, TransferWorld.Everything(), s => s.RequesterRejectAsync(rejected.Id, "Not needed this week", Guid.NewGuid()));
        Assert.Equal(StockTransferStatus.Rejected, done!.Status);
        Assert.Equal("Not needed this week", done.RejectionReason);

        var cancelled = await RequestAsync(w, 10);
        var c = await RunAsync(w, w.Main, TransferWorld.Everything(), s => s.CancelAsync(cancelled.Id, "Ordered elsewhere", w.W.UserId));
        Assert.Equal(StockTransferStatus.Cancelled, c!.Status);
    }

    [Fact]
    public async Task Value_CountsZeroForAProductWithNoStockRowAtTheSource()
    {
        var w = await WorldAsync(0m);
        var r = await w.RequestedAsync(w.L.MainWarehouse, w.L.BlrStore, w.Pen(w.Pc, 10m)); // no pen stock at Main warehouse
        Assert.Equal(0m, r.EstimatedValue);
        Assert.Equal(StockTransferStatus.AwaitingRequesterApproval, r.Status); // 0 >= threshold 0
    }

    [Fact]
    public async Task Level1_Repeat_IsANoOp_ButOnARequestThatNeverNeededLevel1_ItIsRefused()
    {
        var w = await WorldAsync(40m);
        var high = await RequestAsync(w, 10); // 50 >= 40
        var level1 = Guid.NewGuid();
        await RunAsync(w, w.Blr, TransferWorld.Everything(), s => s.RequesterApproveAsync(high.Id, level1));
        var again = await RunAsync(w, w.Blr, TransferWorld.Everything(), s => s.RequesterApproveAsync(high.Id, Guid.NewGuid()));
        Assert.Equal(level1, again!.RequesterApprovedByUserId); // unchanged

        var low = await RequestAsync(w, 2); // 10 < 40: straight to the source
        Assert.Equal("status", await FieldOrNullAsync(() => RunAsync(w, w.Blr, TransferWorld.Everything(), s => s.RequesterApproveAsync(low.Id, Guid.NewGuid()))));
        await RunAsync(w, w.Main, TransferWorld.Everything(), s => s.RejectAsync(low.Id, "not now", Guid.NewGuid()));
        Assert.Equal("status", await FieldOrNullAsync(() => RunAsync(w, w.Blr, TransferWorld.Everything(), s => s.RequesterRejectAsync(low.Id, "x", Guid.NewGuid()))));
    }
}
