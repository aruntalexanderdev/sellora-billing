using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Sellora.Api.Authorization;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Authorization;

/// <summary>
/// Phase 7 (E1-E5): the handler evaluates the request's membership server-side at the working branch (header, else primary, else default) and
/// ignores token claims. A branch-only holder passes only at a branch whose role holds the code and then sees only those branches.
/// </summary>
public class PermissionAuthorizationHandlerTests
{
    private static async Task<(bool Succeeded, BranchContext Branches)> EvaluateAsync(
        RbacWorld w, UserCompanyMembership? m, string code, Guid? header = null, Guid? primary = null, IReadOnlySet<Guid>? allowed = null,
        bool withClaim = false, IMemoryCache? cache = null, Guid? active = null)
    {
        w.Db.ChangeTracker.Clear();
        var membership = new MembershipContext();
        if (m is not null)
        {
            var fresh = w.Db.UserCompanyMemberships.AsEnumerable().Single(x => x.Id == m.Id);
            membership.Set(fresh.Id, fresh.AccessVersion);
        }
        var branches = new BranchContext();
        branches.Set(header, allowed, primary, active);
        var handler = new PermissionAuthorizationHandler(Rbac.Effective(w.Db, membership, cache), membership, branches);

        var claims = withClaim ? [new Claim(AuthClaimTypes.Permission, code)] : Array.Empty<Claim>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var requirement = new PermissionRequirement(code);
        var context = new AuthorizationHandlerContext([requirement], principal, resource: null);
        await handler.HandleAsync(context);
        return (context.HasSucceeded, branches);
    }

    private static IReadOnlySet<Guid> Set(params Guid[] ids) => ids.ToHashSet();

    [Fact]
    public async Task HandleRequirementAsync_CompanyRoleHoldsCode_SucceedsAtAnyBranch_WithoutNarrowing()
    {
        var w = new RbacWorld();
        var (_, m) = w.CompanyWideWith("cw", w.CompanyRole("Viewer", "Sales.View"));

        var (ok, branches) = await EvaluateAsync(w, m, "Sales.View", allowed: null);

        Assert.True(ok);
        Assert.Null(branches.AllowedBranchIds);
    }

    [Fact]
    public async Task HandleRequirementAsync_CodeNotHeld_DoesNotSucceed()
    {
        var w = new RbacWorld();
        var (_, m) = w.CompanyWideWith("cw", w.CompanyRole("Viewer", "Sales.View"));

        var (ok, _) = await EvaluateAsync(w, m, "Users.View");

        Assert.False(ok);
    }

    [Fact]
    public async Task HandleRequirementAsync_PermissionClaimInTheToken_IsIgnored()
    {
        var w = new RbacWorld();
        var (_, m) = w.CompanyWideWith("cw", w.CompanyRole("Viewer", "Sales.View"));

        var (ok, _) = await EvaluateAsync(w, m, "Users.View", withClaim: true);

        Assert.False(ok);
    }

    [Fact]
    public async Task HandleRequirementAsync_NoMembershipEstablished_DoesNotSucceed_EvenWithClaim()
    {
        var w = new RbacWorld();

        var (ok, _) = await EvaluateAsync(w, null, "Sales.View", withClaim: true);

        Assert.False(ok);
    }

    [Fact]
    public async Task HandleRequirementAsync_Administrator_HoldsEveryCode()
    {
        var w = new RbacWorld();
        var (_, m) = w.CompanyWideWith("adm", w.Administrator);

        var (ok, _) = await EvaluateAsync(w, m, "Reports.Profit");

        Assert.True(ok);
    }

    [Fact]
    public async Task HandleRequirementAsync_BranchRoleAtB1_PassesAtB1AndNarrowsToB1()
    {
        var w = new RbacWorld();
        var (_, m) = w.BranchMemberWith("b1", w.BranchRole("B1 Cashier", w.B1, "Sales.View"), w.B1, w.B2);

        var (ok, branches) = await EvaluateAsync(w, m, "Sales.View", header: w.B1.Id, allowed: Set(w.B1.Id, w.B2.Id), primary: w.B1.Id);

        Assert.True(ok);
        Assert.Equal(Set(w.B1.Id), branches.AllowedBranchIds);
        Assert.False(branches.CanAccess(w.B2.Id));
    }

    [Fact]
    public async Task HandleRequirementAsync_BranchRoleAtB1_WorkingBranchB2_DoesNotSucceed()
    {
        var w = new RbacWorld();
        var (_, m) = w.BranchMemberWith("b1", w.BranchRole("B1 Cashier", w.B1, "Sales.View"), w.B1, w.B2);

        var (ok, _) = await EvaluateAsync(w, m, "Sales.View", header: w.B2.Id, allowed: Set(w.B1.Id, w.B2.Id), primary: w.B1.Id);

        Assert.False(ok);
    }

    [Fact]
    public async Task HandleRequirementAsync_BranchRole_NoHeader_UsesThePrimaryBranch()
    {
        var w = new RbacWorld();
        var (_, m) = w.BranchMemberWith("b2", w.BranchRole("B2 Cashier", w.B2, "Sales.View"), w.B2, w.B1);

        var atPrimary = await EvaluateAsync(w, m, "Sales.View", header: null, allowed: Set(w.B1.Id, w.B2.Id), primary: w.B2.Id);
        var otherPrimary = await EvaluateAsync(w, m, "Sales.View", header: null, allowed: Set(w.B1.Id, w.B2.Id), primary: w.B1.Id);

        Assert.True(atPrimary.Succeeded);
        Assert.False(otherPrimary.Succeeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_BranchRole_NoHeaderNoPrimary_UsesTheActiveBranchResolvedByTheMiddleware()
    {
        var w = new RbacWorld();
        var main = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(w.Db.Branches, b => b.IsDefault));
        var (_, m) = w.BranchMemberWith("main", w.BranchRole("Main Cashier", main, "Sales.View"), main);

        var (ok, _) = await EvaluateAsync(w, m, "Sales.View", header: null, primary: null, allowed: Set(main.Id), active: main.Id);

        Assert.True(ok);
    }

    [Fact]
    public async Task HandleRequirementAsync_BranchRoleWithoutAllocation_GrantsNothing()
    {
        var w = new RbacWorld();
        var role = w.BranchRole("B1 Cashier", w.B1, "Sales.View");
        var (_, m) = w.AddMember("noalloc", CompanyAccessScope.AssignedBranches);
        w.Allocate(m, w.B2, primary: true);
        w.Assign(m, role); // service refuses this; the evaluation still must not honour it

        var (ok, _) = await EvaluateAsync(w, m, "Sales.View", header: w.B1.Id, allowed: Set(w.B2.Id), primary: w.B2.Id);

        Assert.False(ok);
    }

    [Fact]
    public async Task HandleRequirementAsync_BranchHoldsAtDifferentBranchesPerCode_NarrowingIntersects()
    {
        var w = new RbacWorld();
        var b1 = w.BranchRole("B1 Buyer", w.B1, "Sales.View", "Purchase.View");
        var b2 = w.BranchRole("B2 Seller", w.B2, "Sales.View");
        var (_, m) = w.BranchMemberWith("two", b1, w.B1, w.B2);
        w.Assign(m, b2);
        var allowed = Set(w.B1.Id, w.B2.Id);

        // Both policies on one endpoint: the allowed set shrinks to the intersection of the permitted branches.
        w.Db.ChangeTracker.Clear();
        var membership = new MembershipContext();
        var fresh = w.Db.UserCompanyMemberships.AsEnumerable().Single(x => x.Id == m.Id);
        membership.Set(fresh.Id, fresh.AccessVersion);
        var branches = new BranchContext();
        branches.Set(w.B1.Id, allowed, w.B1.Id);
        var handler = new PermissionAuthorizationHandler(Rbac.Effective(w.Db, membership), membership, branches);
        var context = new AuthorizationHandlerContext([new PermissionRequirement("Sales.View"), new PermissionRequirement("Purchase.View")],
            new ClaimsPrincipal(new ClaimsIdentity("test")), null);
        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
        Assert.Equal(Set(w.B1.Id), branches.AllowedBranchIds);
    }

    [Fact]
    public async Task HandleRequirementAsync_CompanyRolePlusBranchRole_CompanyGrantWinsAndDoesNotNarrow()
    {
        var w = new RbacWorld();
        var (_, m) = w.BranchMemberWith("mix", w.CompanyRole("Viewer", "Sales.View"), w.B1, w.B2);
        w.Assign(m, w.BranchRole("B1 Sales", w.B1, "Sales.View"));

        var (ok, branches) = await EvaluateAsync(w, m, "Sales.View", header: w.B2.Id, allowed: Set(w.B1.Id, w.B2.Id), primary: w.B1.Id);

        Assert.True(ok);
        Assert.Equal(Set(w.B1.Id, w.B2.Id), branches.AllowedBranchIds);
    }

    [Fact]
    public async Task HandleRequirementAsync_InactiveRole_GrantsNothing()
    {
        var w = new RbacWorld();
        var role = w.CompanyRole("Viewer", "Sales.View");
        var (_, m) = w.CompanyWideWith("cw", role);
        await w.SetRoleActiveAsync(role, false);

        var (ok, _) = await EvaluateAsync(w, m, "Sales.View");

        Assert.False(ok);
    }

    [Fact]
    public async Task HandleRequirementAsync_SuspendedMembership_GrantsNothing()
    {
        var w = new RbacWorld();
        var (_, m) = w.AddMember("susp", status: MembershipStatus.Suspended);
        w.Assign(m, w.CompanyRole("Viewer", "Sales.View"));

        var (ok, _) = await EvaluateAsync(w, m, "Sales.View");

        Assert.False(ok);
    }

    [Fact]
    public async Task HandleRequirementAsync_EndedAssignment_GrantsNothing()
    {
        var w = new RbacWorld();
        var (_, m) = w.CompanyWideWith("cw", w.CompanyRole("Viewer", "Sales.View"));
        var row = w.Db.UserRoleAssignments.AsEnumerable().Single(a => a.UserCompanyMembershipId == m.Id);
        row.Status = AssignmentStatus.Ended;
        row.EndAt = DateTimeOffset.UtcNow;
        await w.Db.SaveChangesAsync();

        var (ok, _) = await EvaluateAsync(w, m, "Sales.View");

        Assert.False(ok);
    }

    [Fact]
    public async Task HandleRequirementAsync_MultipleCompanyRoles_AreUnioned()
    {
        var w = new RbacWorld();
        var (_, m) = w.CompanyWideWith("cw", w.CompanyRole("Viewer", "Sales.View"));
        w.Assign(m, w.CompanyRole("Buyer", "Purchase.View"));

        Assert.True((await EvaluateAsync(w, m, "Sales.View")).Succeeded);
        Assert.True((await EvaluateAsync(w, m, "Purchase.View")).Succeeded);
        Assert.False((await EvaluateAsync(w, m, "Purchase.Create")).Succeeded);
    }

    [Fact]
    public async Task HandleRequirementAsync_AllocationAloneGrantsNothing()
    {
        var w = new RbacWorld();
        var (_, m) = w.AddMember("alloc", CompanyAccessScope.AssignedBranches);
        w.Allocate(m, w.B1, primary: true);

        var (ok, _) = await EvaluateAsync(w, m, "Sales.View", header: w.B1.Id, allowed: Set(w.B1.Id), primary: w.B1.Id);

        Assert.False(ok);
    }

    // ---- cache (T8) ----

    [Fact]
    public async Task Evaluation_SameAccessVersion_IsServedFromTheCache_EvenAfterTheRowsChanged()
    {
        var w = new RbacWorld();
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 });
        var (_, m) = w.CompanyWideWith("cw", w.CompanyRole("Viewer", "Sales.View"));
        Assert.True((await EvaluateAsync(w, m, "Sales.View", cache: cache)).Succeeded);
        var row = w.Db.UserRoleAssignments.AsEnumerable().Single(a => a.UserCompanyMembershipId == m.Id);
        row.Status = AssignmentStatus.Ended;
        row.EndAt = DateTimeOffset.UtcNow;
        await w.Db.SaveChangesAsync(); // no AccessVersion bump: documents that the version is the invalidation key

        var cached = await EvaluateAsync(w, m, "Sales.View", cache: cache);
        await w.BumpAsync(m);
        var afterBump = await EvaluateAsync(w, m, "Sales.View", cache: cache);

        Assert.True(cached.Succeeded);
        Assert.False(afterBump.Succeeded);
    }

    [Fact]
    public async Task Evaluation_UnchangedAccessVersion_QueriesTheDatabaseOnce()
    {
        var w = new RbacWorld();
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 });
        var (_, m) = w.CompanyWideWith("cw", w.CompanyRole("Viewer", "Sales.View"));
        var membership = new MembershipContext();
        membership.Set(m.Id, 0);
        var service = Rbac.Effective(w.Db, membership, cache);

        var first = await service.GetAsync(m.Id, 0);
        var second = await service.GetAsync(m.Id, 0);
        var newVersion = await service.GetAsync(m.Id, 1);

        Assert.Same(first, second);
        Assert.NotSame(first, newVersion);
    }
}
