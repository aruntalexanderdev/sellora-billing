using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>
/// Phase 9 D7 on EF InMemory: per-action branch fit (Create: source OR destination fits; Approve/Reject/Dispatch: source; Receive: destination;
/// Cancel: either), the company-level holder of the matching code bypassing the fit (another branch's vehicle fits only without the bypass: never), Resolve company-only, and fail-closed
/// without a permission service. Main = working branch of the caller unless a test says otherwise; Main store -> BLR store is the plain case.
/// </summary>
public class StockTransferPermissionTests
{
    /// <summary>A caller holding nothing (the helpers treat a null service as "default to everything", so the empty set stands in; the null-service fail-closed case has its own tests).</summary>
    private static IEffectivePermissionService None => new FakeEffectivePermissions();
    private static IEffectivePermissionService Only(string code) => LocationWorld.Company(code);

    private static async Task<(TransferWorld W, StockTransfer T)> AtAsync(StockTransferStatus status, Guid? source = null, Guid? destination = null)
    {
        var w = await TransferWorld.CreateAsync();
        var src = source ?? w.L.MainStore;
        await w.StockPenAsync(src, 100m);
        await w.StockPenAsync(w.L.MainStore, 100m);
        var t = await w.ApprovedAsync(src, destination ?? w.L.BlrStore, w.Pen(w.Box, 2m));
        if (status == StockTransferStatus.Dispatched) await w.DispatchAsRequestedAsync(t.Id);
        return (w, await w.ReloadAsync(t.Id));
    }

    private static async Task<string?> FieldOrNullAsync(Func<Task> act)
    {
        try { await act(); return null; }
        catch (DomainValidationException ex) { return ex.Field; }
    }

    // ---- create ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("main", "main-store", "blr-store", null)]   // the source fits the working branch
    [InlineData("blr", "main-store", "blr-store", null)]    // the destination fits
    [InlineData("main", "blr-store", "blr-warehouse", "stockLocationId")] // neither endpoint is Main's
    [InlineData("main", "company-warehouse", "central-van", "stockLocationId")] // company-level endpoints need the permission
    [InlineData("main", "blr-van", "company-warehouse", "stockLocationId")]     // another branch's vehicle never fits
    [InlineData("main", "main-warehouse", "company-warehouse", null)]
    [InlineData("blr", "blr-van", "company-warehouse", null)]  // a vehicle assigned to the working branch is that branch's
    public async Task Create_WithoutACompanyLevelGrant_NeedsSourceOrDestinationToFitTheWorkingBranch(string branch, string from, string to, string? field)
    {
        var w = await TransferWorld.CreateAsync();
        Guid Loc(string n) => n switch
        {
            "main-store" => w.L.MainStore, "blr-store" => w.L.BlrStore, "blr-warehouse" => w.L.BlrWarehouse, "main-warehouse" => w.L.MainWarehouse,
            "company-warehouse" => w.L.CompanyWarehouse, "central-van" => w.L.CentralVan, _ => w.L.BlrVan,
        };

        var actual = await FieldOrNullAsync(() => w.CreateTransferAsync(Loc(from), Loc(to), [w.Pen(w.Box, 1m)], branch: branch == "main" ? w.Main : w.Blr, permissions: new FakeScopedPermissions([], new Dictionary<Guid, string[]>())));

        Assert.Equal(field, actual);
    }

    [Fact]
    public async Task Create_WithTheCodeCompanyWide_BypassesTheFit_IncludingAnotherBranchsVehicle()
    {
        var w = await TransferWorld.CreateAsync();
        await w.CreateTransferAsync(w.L.BlrStore, w.L.BlrWarehouse, [w.Pen(w.Box, 1m)], branch: w.Main, permissions: Only("StockTransfers.Create"));
        await w.CreateTransferAsync(w.L.BlrVan, w.L.CompanyWarehouse, [w.Pen(w.Box, 1m)], branch: w.Main, permissions: Only("StockTransfers.Create"));
        Assert.Equal(2, await w.TransferCountAsync());
    }

    [Fact]
    public async Task Create_HoldingADifferentCodeCompanyWide_DoesNotBypass()
    {
        var w = await TransferWorld.CreateAsync();
        Assert.Equal("stockLocationId", await FieldOrNullAsync(() =>
            w.CreateTransferAsync(w.L.BlrStore, w.L.BlrWarehouse, [w.Pen(w.Box, 1m)], branch: w.Main, permissions: Only("StockTransfers.Approve"))));
    }

    [Fact]
    public async Task Create_WithoutAPermissionService_FailsClosedOnCompanyLevelLocations_ButBranchOwnedOnesStillFit()
    {
        var w = await TransferWorld.CreateAsync();
        await using (var db = w.Db(w.Main))
        {
            var service = new StockTransferService(db, null);
            await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(
                new CreateStockTransferInput(w.L.CompanyWarehouse, w.L.CentralVan, null, false, [w.Pen(w.Box, 1m)]), w.W.UserId));
        }
        await using (var db = w.Db(w.Main))
        {
            var created = await new StockTransferService(db, null).CreateAsync(
                new CreateStockTransferInput(w.L.MainStore, w.L.BlrStore, null, false, [w.Pen(w.Box, 1m)]), w.W.UserId);
            Assert.Equal(StockTransferStatus.Draft, created.Status);
        }
    }

    // ---- approve / reject / dispatch: the source -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task ApproveAndReject_NeedTheSourceToFitTheWorkingBranch(string op)
    {
        var (w, _) = await AtAsync(StockTransferStatus.Approved);
        var requested = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m)], submit: true);
        async Task Run(Guid branch, IEffectivePermissionService? p)
        {
            await using var db = w.Db(branch);
            var s = new StockTransferService(db, p);
            if (op == "approve") await s.ApproveAsync(requested.Id, w.W.UserId); else await s.RejectAsync(requested.Id, "no", w.W.UserId);
        }

        Assert.Equal("stockLocationId", await FieldOrNullAsync(() => Run(w.Blr, None)));                 // the destination branch cannot approve
        Assert.Equal("stockLocationId", await FieldOrNullAsync(() => Run(w.Blr, Only("StockTransfers.Dispatch"))));
        Assert.Equal(StockTransferStatus.Requested, (await w.ReloadAsync(requested.Id)).Status);
        Assert.Null(await FieldOrNullAsync(() => Run(w.Main, None)));                                       // the source branch can
        Assert.NotEqual(StockTransferStatus.Requested, (await w.ReloadAsync(requested.Id)).Status);
    }

    [Fact]
    public async Task Approve_CompanyLevelSource_NeedsTheCodeCompanyWide()
    {
        var w = await TransferWorld.CreateAsync();
        var t = await w.CreateTransferAsync(w.L.CompanyWarehouse, w.L.MainStore, [w.Pen(w.Box, 1m)], submit: true);

        Assert.Equal("stockLocationId", await FieldOrNullAsync(() => w.ApproveAsync(t.Id, w.Main, None)));
        Assert.Equal("stockLocationId", await FieldOrNullAsync(() => w.ApproveAsync(t.Id, w.Main, FakeScopedPermissions.AtBranch(w.Main, "StockTransfers.Approve"))));
        var approved = await w.ApproveAsync(t.Id, w.Main, Only("StockTransfers.Approve"));
        Assert.Equal(StockTransferStatus.Approved, approved!.Status);
    }

    [Fact]
    public async Task FitsApprover_MatchesTheApproveRule_AndTheRefusalNamesTheSourceBranch()
    {
        var w = await TransferWorld.CreateAsync();
        var requested = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m)], submit: true);
        var central = await w.CreateTransferAsync(w.L.CompanyWarehouse, w.L.MainStore, [w.Pen(w.Box, 1m)], submit: true);

        async Task<bool> Fits(Guid transferId, Guid branch, IEffectivePermissionService p)
        {
            await using var db = w.Db(branch);
            return await new StockTransferService(db, p).FitsApproverAsync(await w.ReloadAsync(transferId), default);
        }

        Assert.True(await Fits(requested.Id, w.Main, None));                                  // source branch decides
        Assert.False(await Fits(requested.Id, w.Blr, None));                                 // destination branch does not
        Assert.False(await Fits(central.Id, w.Main, FakeScopedPermissions.AtBranch(w.Main, "StockTransfers.Approve")));
        Assert.True(await Fits(central.Id, w.Main, Only("StockTransfers.Approve")));          // company-level source: company-level holder

        await using var blrDb = w.Db(w.Blr);
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new StockTransferService(blrDb, None).ApproveAsync(requested.Id, w.W.UserId));
        Assert.Equal("stockLocationId", ex.Field);
        Assert.StartsWith("Only the source branch (", ex.Message);

        await using var mainDb = w.Db(w.Main);
        var central2 = await Assert.ThrowsAsync<DomainValidationException>(() => new StockTransferService(mainDb, None).ApproveAsync(central.Id, w.W.UserId));
        Assert.Contains("company-level access", central2.Message);
    }

    // ---- receive: the destination ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Receive_NeedsTheDestinationToFit_NotTheSource()
    {
        var (w, t) = await AtAsync(StockTransferStatus.Dispatched);
        var line = t.Lines.Single();

        Assert.Equal("stockLocationId", await FieldOrNullAsync(() => w.ReceiveAsync(t.Id, new(null, [new(line.Id, w.Box, 1m, 0m)]), w.Main, None)));
        Assert.Equal("stockLocationId", await FieldOrNullAsync(() => w.ReceiveAsync(t.Id, new(null, [new(line.Id, w.Box, 1m, 0m)]), w.Main, Only("StockTransfers.Dispatch"))));
        Assert.Empty((await w.ReloadAsync(t.Id)).Receipts);

        await w.ReceiveAsync(t.Id, new(null, [new(line.Id, w.Box, 1m, 0m)]), w.Blr, None);
        Assert.Single((await w.ReloadAsync(t.Id)).Receipts);
        await w.ReceiveAsync(t.Id, new(null, [new(line.Id, w.Box, 1m, 0m)]), w.Main, Only("StockTransfers.Receive")); // company-wide: bypass
        Assert.Equal(2, (await w.ReloadAsync(t.Id)).Receipts.Count);
        await w.AssertInvariantsAsync("receive permissions");
    }

    [Fact]
    public async Task Receive_IntoAnotherBranchsVehicle_IsRefusedForThatBranchesOutsiders()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrVan, w.Pen(w.Box, 1m));
        await w.DispatchAsRequestedAsync(t.Id);
        var line = (await w.ReloadAsync(t.Id)).Lines.Single();

        Assert.Equal("stockLocationId", await FieldOrNullAsync(() => w.ReceiveAsync(t.Id, new(null, [new(line.Id, w.Box, 1m, 0m)]), w.Main, FakeScopedPermissions.AtBranch(w.Main, "StockTransfers.Receive"))));
        Assert.Null(await FieldOrNullAsync(() => w.ReceiveAsync(t.Id, new(null, [new(line.Id, w.Box, 1m, 0m)]), w.Blr, FakeScopedPermissions.AtBranch(w.Blr, "StockTransfers.Receive"))));
    }

    // ---- cancel: either endpoint ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_EitherEndpointsBranchMay_AThirdPartyNeedsTheCompanyLevelCode()
    {
        var w = await TransferWorld.CreateAsync();
        async Task<StockTransfer?> Cancel(Guid id, Guid branch, IEffectivePermissionService? p) { await using var db = w.Db(branch); return await new StockTransferService(db, p).CancelAsync(id, "x", w.W.UserId); }

        var a = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m)], submit: true);
        Assert.Equal(StockTransferStatus.Cancelled, (await Cancel(a.Id, w.Blr, None))!.Status);       // the destination's branch
        var b = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m)], submit: true);
        Assert.Equal(StockTransferStatus.Cancelled, (await Cancel(b.Id, w.Main, None))!.Status);      // the source's branch

        var c = await w.CreateTransferAsync(w.L.CompanyWarehouse, w.L.CentralVan, [w.Pen(w.Box, 1m)], submit: true); // company-level both
        Assert.Equal("stockLocationId", await FieldOrNullAsync(() => Cancel(c.Id, w.Main, None)));
        Assert.Equal("stockLocationId", await FieldOrNullAsync(() => Cancel(c.Id, w.Main, Only("StockTransfers.Create"))));
        Assert.Equal(StockTransferStatus.Cancelled, (await Cancel(c.Id, w.Main, Only("StockTransfers.Cancel")))!.Status);
    }

    // ---- restricted membership (AssignedBranches) holding the code company-level: the grant never opens a branch outside the allocation ----

    private static HashSet<Guid> Only(params Guid[] branches) => [.. branches];

    [Theory]
    [InlineData("main-to-blr", "stockLocationId")]       // destination is BLR's store: outside the allocation
    [InlineData("blr-to-main", null)]                    // destination is the allocated branch
    [InlineData("blr-to-company", null)]                 // company-level destination is open to any member
    [InlineData("blr-to-blr-van", "stockLocationId")]    // a vehicle assigned to another branch is that branch's
    public async Task Receive_RestrictedMemberWithCompanyLevelCode_NeedsTheDestinationInsideItsAllocation(string scenario, string? field)
    {
        var w = await TransferWorld.CreateAsync();
        var (src, dst) = scenario switch
        {
            "main-to-blr" => (w.L.MainStore, w.L.BlrStore),
            "blr-to-main" => (w.L.BlrStore, w.L.MainStore),
            "blr-to-company" => (w.L.BlrStore, w.L.CompanyWarehouse),
            _ => (w.L.MainStore, w.L.BlrVan),
        };
        await w.StockPenAsync(src, 100m);
        var t = await w.ApprovedAsync(src, dst, w.Pen(w.Box, 2m));
        await w.DispatchAsRequestedAsync(t.Id);
        var line = (await w.ReloadAsync(t.Id)).Lines.Single();

        await using var db = w.Db(w.Main, Only(w.Main));
        var input = new ReceiveStockTransferInput(null, [new(line.Id, w.Box, 1m, 0m)]);
        var actual = await FieldOrNullAsync(() => new StockTransferService(db, Only("StockTransfers.Receive")).ReceiveAsync(t.Id, input, w.W.UserId));

        Assert.Equal(field, actual);
        Assert.Equal(field is null ? 1 : 0, (await w.ReloadAsync(t.Id)).Receipts.Count);
    }

    [Theory]
    [InlineData("blr-store", "blr-warehouse", "stockLocationId")]  // both endpoints outside the allocation
    [InlineData("blr-van", "blr-store", "stockLocationId")]
    [InlineData("main-store", "blr-store", null)]                   // source inside
    [InlineData("blr-store", "main-store", null)]                   // destination inside
    [InlineData("company-warehouse", "blr-store", null)]            // company-level endpoint is open
    public async Task Create_RestrictedMemberWithCompanyLevelCode_NeedsAtLeastOneEndpointInsideItsAllocation(string from, string to, string? field)
    {
        var w = await TransferWorld.CreateAsync();
        Guid Loc(string n) => n switch
        {
            "main-store" => w.L.MainStore, "blr-store" => w.L.BlrStore, "blr-warehouse" => w.L.BlrWarehouse,
            "company-warehouse" => w.L.CompanyWarehouse, _ => w.L.BlrVan,
        };

        await using var db = w.Db(w.Main, Only(w.Main));
        var input = new CreateStockTransferInput(Loc(from), Loc(to), "t", false, [w.Pen(w.Box, 1m)]);
        var actual = await FieldOrNullAsync(() => new StockTransferService(db, Only("StockTransfers.Create")).CreateAsync(input, w.W.UserId));

        Assert.Equal(field, actual);
        Assert.Equal(field is null ? 1 : 0, await w.TransferCountAsync());
    }

    // ---- resolve: company-level only -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resolve_IsCompanyLevelOnly_AtBranchHoldersAndNoServiceAreRefused()
    {
        var (w, t) = await AtAsync(StockTransferStatus.Dispatched);
        var line = t.Lines.Single();
        ResolveStockTransferInput Input() => new("lost", [new(line.Id, StockTransferResolution.WriteOff)]);

        Assert.Equal("stockLocationId", await FieldOrNullAsync(() => w.ResolveAsync(t.Id, Input(), w.Main, None)));
        Assert.Equal("stockLocationId", await FieldOrNullAsync(() => w.ResolveAsync(t.Id, Input(), w.Main, FakeScopedPermissions.AtBranch(w.Main, "StockTransfers.Resolve"))));
        Assert.Equal("stockLocationId", await FieldOrNullAsync(() => w.ResolveAsync(t.Id, Input(), w.Main, Only("StockTransfers.Receive"))));
        Assert.Equal(StockTransferStatus.Dispatched, (await w.ReloadAsync(t.Id)).Status);
        Assert.Equal(20m, await w.QohAsync(w.L.Transit));

        var done = await w.ResolveAsync(t.Id, Input(), w.Blr, Only("StockTransfers.Resolve")); // any working branch with the company-wide code
        Assert.Equal(StockTransferStatus.Completed, done!.Transfer.Status);
        await w.AssertInvariantsAsync("resolve permissions");
    }

    [Theory]
    [InlineData("main-to-blr", "stockLocationId")]       // the destination is outside the allocation
    [InlineData("blr-to-main", "stockLocationId")]       // the source (where a return would go) is outside the allocation
    [InlineData("main-to-company", null)]                // both sides inside the allocation or company-level
    public async Task Resolve_RestrictedMemberWithCompanyLevelCode_NeedsBothEndpointsInsideItsAllocation(string scenario, string? field)
    {
        var w = await TransferWorld.CreateAsync();
        var (src, dst) = scenario switch
        {
            "main-to-blr" => (w.L.MainStore, w.L.BlrStore),
            "blr-to-main" => (w.L.BlrStore, w.L.MainStore),
            _ => (w.L.MainStore, w.L.CompanyWarehouse),
        };
        await w.StockPenAsync(src, 100m);
        var t = await w.ApprovedAsync(src, dst, w.Pen(w.Box, 2m));
        await w.DispatchAsRequestedAsync(t.Id);
        var line = (await w.ReloadAsync(t.Id)).Lines.Single();

        await using var db = w.Db(w.Main, Only(w.Main));
        var input = new ResolveStockTransferInput("lost", [new(line.Id, StockTransferResolution.ReturnToSource)]);
        var actual = await FieldOrNullAsync(() => new StockTransferService(db, Only("StockTransfers.Resolve")).ResolveAsync(t.Id, input, w.W.UserId));

        Assert.Equal(field, actual);
        Assert.Equal(field is null ? StockTransferStatus.Completed : StockTransferStatus.Dispatched, (await w.ReloadAsync(t.Id)).Status);
    }

}
