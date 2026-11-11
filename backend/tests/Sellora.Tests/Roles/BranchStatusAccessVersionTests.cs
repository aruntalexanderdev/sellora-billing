using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Roles;

/// <summary>Phase 7: a branch's active state decides whether a branch role counts, so changing it bumps AccessVersion of every membership holding an Active branch-role assignment there.</summary>
public class BranchStatusAccessVersionTests
{
    private static async Task SetStatusAsync(RbacWorld w, Branch branch, bool active)
    {
        w.Db.ChangeTracker.Clear();
        // A branch owns its store (production invariant); BranchService flips it together with the branch.
        if (!await w.Db.StockLocations.AnyAsync(l => l.BranchId == branch.Id))
        {
            w.Db.StockLocations.Add(BranchBuilder.Store(branch));
            await w.Db.SaveChangesAsync();
            w.Db.ChangeTracker.Clear();
        }
        await new BranchService(w.Db, new StockLocationUsageService(w.Db)).SetStatusAsync(branch.Id, active, "test", Guid.NewGuid());
    }

    [Fact]
    public async Task SetStatusAsync_Deactivate_BumpsOnlyHoldersOfAnActiveRoleAtThatBranch()
    {
        var w = new RbacWorld();
        var b1Role = w.BranchRole("B1 Seller", w.B1, "Sales.View");
        var b2Role = w.BranchRole("B2 Seller", w.B2, "Sales.View");
        var (_, holder) = w.BranchMemberWith("holder", b1Role, w.B2, w.B1);
        var (_, otherBranch) = w.BranchMemberWith("other", b2Role, w.B2);
        var (_, companyWide) = w.CompanyWideWith("wide", w.CompanyRole("Wide", "Sales.View"));
        var (_, ended) = w.BranchMemberWith("ended", b1Role, w.B2, w.B1);
        var row = (await w.AssignmentsOfAsync(ended)).Single();
        row.Status = AssignmentStatus.Ended;
        row.EndAt = DateTimeOffset.UtcNow;
        w.Db.UserRoleAssignments.Update(row);
        await w.Db.SaveChangesAsync();

        var before = new[] { await w.VersionOfAsync(holder), await w.VersionOfAsync(otherBranch), await w.VersionOfAsync(companyWide), await w.VersionOfAsync(ended) };
        await SetStatusAsync(w, w.B1, false);

        Assert.Equal([before[0] + 1, before[1], before[2], before[3]],
            [await w.VersionOfAsync(holder), await w.VersionOfAsync(otherBranch), await w.VersionOfAsync(companyWide), await w.VersionOfAsync(ended)]);
    }

    [Fact]
    public async Task SetStatusAsync_Reactivate_BumpsHoldersAgain_AndANoOpChangeDoesNot()
    {
        var w = new RbacWorld();
        var role = w.BranchRole("B1 Seller", w.B1, "Sales.View");
        var (_, holder) = w.BranchMemberWith("holder", role, w.B2, w.B1);
        var start = await w.VersionOfAsync(holder);

        await SetStatusAsync(w, w.B1, true); // already active: nothing changes
        Assert.Equal(start, await w.VersionOfAsync(holder));

        await SetStatusAsync(w, w.B1, false);
        await SetStatusAsync(w, w.B1, true);

        Assert.Equal(start + 2, await w.VersionOfAsync(holder));
    }
}
