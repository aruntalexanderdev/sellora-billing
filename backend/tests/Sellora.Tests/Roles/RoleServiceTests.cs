using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Roles;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Roles;

/// <summary>
/// Phase 7 role definition rules (D1, D9, D10 R1-R3) on EF InMemory: scope immutability, branch-eligible codes only, codes held at the role's scope, the Q1 CompanyWide
/// requirement for company roles, branch administrators confined to their branch (404), system role protection, holder AccessVersion bumps.
/// </summary>
public class RoleServiceTests
{
    private static readonly string[] ManagerCodes = ["Roles.View", "Roles.Create", "Roles.Edit", "Roles.Duplicate", "Roles.Disable", "Sales.View"];

    private static CreateRoleInput Input(string name, IReadOnlyList<string> codes, RoleScope? scope = null, Guid? branch = null) => new(name, null, codes, scope, branch);

    private static async Task<(RbacWorld W, RoleActor Admin)> AdminWorldAsync()
    {
        var w = new RbacWorld();
        var (u, m) = w.CompanyWideWith("admin", w.Administrator);
        return (w, await w.ActorOfAsync(u, m));
    }

    private static async Task<RoleActor> ManagerAsync(RbacWorld w, string name = "mgr", params string[] extra)
    {
        var (u, m) = w.CompanyWideWith(name, w.CompanyRole("Manager " + name, [.. ManagerCodes, .. extra]));
        return await w.ActorOfAsync(u, m);
    }

    private static async Task<RoleActor> BranchAdminAsync(RbacWorld w, Branch branch, string name = "badmin")
    {
        var role = w.BranchRole("Admin " + name, branch, "Branch.Roles.View", "Branch.Roles.Manage", "Branch.Users.AssignRoles", "Sales.View", "Sales.Create");
        var (u, m) = w.BranchMemberWith(name, role, branch);
        return await w.ActorOfAsync(u, m);
    }

    // ---- create ----

    [Fact]
    public async Task CreateAsync_CompanyRole_IsStampedWithTheCompany_Trimmed_AndNotSystem()
    {
        var (w, admin) = await AdminWorldAsync();

        var role = await w.Roles.CreateAsync(admin, Input("  Cashier  ", ["Sales.View", "Sales.Create", "Sales.View"]));

        Assert.Equal(("Cashier", RoleScope.Company, (Guid?)null, false, true), (role.Name, role.ScopeType, role.BranchId, role.IsSystem, role.IsActive));
        Assert.Equal(TestCompany.DefaultId, role.CompanyId);
        Assert.Equal(["Sales.Create", "Sales.View"], await w.CodesOfAsync(role.Id));
        Assert.Equal(admin.UserId, role.CreatedByUserId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_BlankName_IsRejectedOnName(string name)
    {
        var (w, admin) = await AdminWorldAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.CreateAsync(admin, Input(name, [])));

        Assert.Equal("name", ex.Field);
    }

    [Fact]
    public async Task CreateAsync_DuplicateNameIgnoringCase_IsRejected_ButTheSameNameAtAnotherScopeIsAllowed()
    {
        var (w, admin) = await AdminWorldAsync();
        await w.Roles.CreateAsync(admin, Input("Cashier", ["Sales.View"]));

        var dup = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.CreateAsync(admin, Input("CASHIER", ["Sales.View"])));
        var atBranch = await w.Roles.CreateAsync(admin, Input("Cashier", ["Sales.View"], RoleScope.Branch, w.B1.Id));
        var dupAtBranch = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.CreateAsync(admin, Input("cashier", ["Sales.View"], RoleScope.Branch, w.B1.Id)));
        var otherBranch = await w.Roles.CreateAsync(admin, Input("Cashier", ["Sales.View"], RoleScope.Branch, w.B2.Id));

        Assert.Equal("name", dup.Field);
        Assert.Equal("name", dupAtBranch.Field);
        Assert.NotEqual(atBranch.Id, otherBranch.Id);
    }

    [Fact]
    public async Task CreateAsync_UnknownPermissionCode_IsRejectedOnPermissionCodes()
    {
        var (w, admin) = await AdminWorldAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.CreateAsync(admin, Input("X", ["Sales.View", "Nope.Nothing"])));

        Assert.Equal("permissionCodes", ex.Field);
        Assert.Contains("Nope.Nothing", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_GovernanceCodes_AreUnknownToRoles()
    {
        var (w, admin) = await AdminWorldAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.CreateAsync(admin, Input("Gov", ["Company.Governance.ManageOwners"])));

        Assert.Equal("permissionCodes", ex.Field);
    }

    [Fact]
    public async Task CreateAsync_ByAnActorWithoutTheCodes_ListsTheUnheldOnes_R1()
    {
        var w = new RbacWorld();
        var manager = await ManagerAsync(w);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.CreateAsync(manager, Input("Greedy", ["Sales.View", "Reports.Profit", "Inventory.Adjust"])));

        Assert.Equal(("permissionCodes", "You can only grant permissions you hold yourself. Not held: Inventory.Adjust, Reports.Profit."), (ex.Field, ex.Message));
        Assert.Empty(w.Db.Roles.Where(r => r.Name == "Greedy"));
    }

    [Fact]
    public async Task CreateAsync_BranchRole_RejectsCompanyOnlyCodes_AndInvalidBranches()
    {
        var (w, admin) = await AdminWorldAsync();
        var inactive = BranchBuilder.Extra("OLD", active: false);
        w.Db.Branches.Add(inactive);
        await w.Db.SaveChangesAsync();

        var companyOnly = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.CreateAsync(admin, Input("A", ["Sales.View", "Users.View", "Masters.Edit"], RoleScope.Branch, w.B1.Id)));
        var noBranch = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.CreateAsync(admin, Input("B", ["Sales.View"], RoleScope.Branch, null)));
        var companyWithBranch = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.CreateAsync(admin, Input("C", ["Sales.View"], RoleScope.Company, w.B1.Id)));
        var unknown = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.CreateAsync(admin, Input("D", ["Sales.View"], RoleScope.Branch, Guid.NewGuid())));
        var inactiveBranch = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.CreateAsync(admin, Input("E", ["Sales.View"], RoleScope.Branch, inactive.Id)));

        Assert.Equal("permissionCodes", companyOnly.Field);
        Assert.Contains("Masters.Edit, Users.View", companyOnly.Message);
        Assert.All(new[] { noBranch, companyWithBranch, unknown, inactiveBranch }, e => Assert.Equal("branchId", e.Field));
    }

    [Fact]
    public async Task CreateAsync_BranchRole_ByTheAdministrator_AcceptsEveryBranchEligibleCode()
    {
        var (w, admin) = await AdminWorldAsync();
        var eligible = Sellora.Domain.Permissions.PermissionCatalog.All.Where(p => Sellora.Domain.Permissions.PermissionCatalog.IsBranchEligible(p.Code)).Select(p => p.Code).ToList();

        var role = await w.Roles.CreateAsync(admin, Input("All eligible", eligible, RoleScope.Branch, w.B1.Id));

        Assert.Equal(eligible.Order(), await w.CodesOfAsync(role.Id));
        Assert.Equal((RoleScope.Branch, w.B1.Id), (role.ScopeType, role.BranchId));
    }

    [Fact]
    public async Task CreateAsync_CompanyRole_ByAnAssignedBranchesActor_IsForbidden_Q1()
    {
        var w = new RbacWorld();
        var (u, m) = w.BranchMemberWith("scoped", w.CompanyRole("Manager", ManagerCodes), w.B1);
        var actor = await w.ActorOfAsync(u, m);

        var ex = await Assert.ThrowsAsync<AccessManagementForbiddenException>(() => w.Roles.CreateAsync(actor, Input("X", ["Sales.View"])));

        Assert.Equal(RoleErrorCodes.RoleManagementForbidden, ex.Code);
    }

    [Fact]
    public async Task CreateAsync_ByAnActorWithoutRolesCreate_IsForbidden()
    {
        var w = new RbacWorld();
        var (u, m) = w.CompanyWideWith("viewer", w.CompanyRole("Roles viewer", "Roles.View", "Sales.View"));
        var actor = await w.ActorOfAsync(u, m);

        var ex = await Assert.ThrowsAsync<AccessManagementForbiddenException>(() => w.Roles.CreateAsync(actor, Input("X", ["Sales.View"])));

        Assert.Equal(RoleErrorCodes.RoleManagementForbidden, ex.Code);
    }

    // ---- branch administrators (R3) ----

    [Fact]
    public async Task BranchAdmin_CreatesBranchRolesOfItsBranch_WithinItsOwnCodes()
    {
        var w = new RbacWorld();
        var actor = await BranchAdminAsync(w, w.B1);

        var role = await w.Roles.CreateAsync(actor, Input("Clerk", ["Sales.View"], RoleScope.Branch, w.B1.Id));
        var notHeld = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.CreateAsync(actor, Input("Greedy", ["Sales.View", "Sales.Cancel"], RoleScope.Branch, w.B1.Id)));

        Assert.Equal(w.B1.Id, role.BranchId);
        Assert.Equal("permissionCodes", notHeld.Field);
        Assert.Contains("Sales.Cancel", notHeld.Message);
    }

    [Fact]
    public async Task BranchAdmin_CannotCreateCompanyRoles_OrRolesOfAnotherBranch()
    {
        var w = new RbacWorld();
        var actor = await BranchAdminAsync(w, w.B1);

        var company = await Assert.ThrowsAsync<AccessManagementForbiddenException>(() => w.Roles.CreateAsync(actor, Input("Wide", ["Sales.View"])));
        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => w.Roles.CreateAsync(actor, Input("Other", ["Sales.View"], RoleScope.Branch, w.B2.Id)));

        Assert.Equal(RoleErrorCodes.RoleManagementForbidden, company.Code);
    }

    [Fact]
    public async Task BranchAdmin_CannotUpdateDuplicateOrDeactivateRolesOfAnotherBranch_NotFound()
    {
        var w = new RbacWorld();
        var actor = await BranchAdminAsync(w, w.B1);
        var other = w.BranchRole("B2 role", w.B2, "Sales.View");

        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => w.Roles.UpdateAsync(actor, other.Id, new UpdateRoleInput("x", null, ["Sales.View"], null, null)));
        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => w.Roles.DuplicateAsync(actor, other.Id));
        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => w.Roles.SetActiveAsync(actor, other.Id, false));
    }

    [Fact]
    public async Task BranchAdmin_CannotSeeCompanyRoles_AsTheyAreNotVisibleWithoutCompanyRolesView()
    {
        var w = new RbacWorld();
        var actor = await BranchAdminAsync(w, w.B1);
        var company = w.CompanyRole("Company role", "Sales.View");

        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => w.Roles.UpdateAsync(actor, company.Id, new UpdateRoleInput("x", null, ["Sales.View"], null, null)));
        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => w.Roles.DuplicateAsync(actor, company.Id));
    }

    [Fact]
    public async Task BranchAdmin_WhoseAllocationWasEnded_NoLongerReachesTheBranch()
    {
        var w = new RbacWorld();
        var role = w.BranchRole("B1 admin", w.B1, "Branch.Roles.View", "Branch.Roles.Manage", "Sales.View");
        var (u, m) = w.BranchMemberWith("moved", role, w.B1);
        var row = w.Db.UserBranchAssignments.AsEnumerable().Single(a => a.UserCompanyMembershipId == m.Id);
        row.Status = AssignmentStatus.Ended;
        row.EndAt = DateTimeOffset.UtcNow;
        await w.Db.SaveChangesAsync();
        var actor = await w.ActorOfAsync(u, m);

        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => w.Roles.CreateAsync(actor, Input("X", ["Sales.View"], RoleScope.Branch, w.B1.Id)));
    }

    [Fact]
    public async Task CompanyAdministrator_ManagesBranchRolesOfAnyBranch()
    {
        var (w, admin) = await AdminWorldAsync();
        var manager = await ManagerAsync(w);

        var byAdmin = await w.Roles.CreateAsync(admin, Input("Admin made", ["Sales.View"], RoleScope.Branch, w.B2.Id));
        var byManager = await w.Roles.CreateAsync(manager, Input("Manager made", ["Sales.View"], RoleScope.Branch, w.B2.Id)); // company path: Roles.Create + CompanyWide, codes held company-wide

        Assert.Equal([byAdmin.BranchId, byManager.BranchId], [w.B2.Id, w.B2.Id]);
    }

    // ---- update ----

    [Fact]
    public async Task UpdateAsync_ScopeAndBranchAreImmutable()
    {
        var (w, admin) = await AdminWorldAsync();
        var company = await w.Roles.CreateAsync(admin, Input("Company", ["Sales.View"]));
        var branch = await w.Roles.CreateAsync(admin, Input("Branch", ["Sales.View"], RoleScope.Branch, w.B1.Id));

        var toBranch = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.UpdateAsync(admin, company.Id, new UpdateRoleInput("Company", null, ["Sales.View"], RoleScope.Branch, w.B1.Id)));
        var toCompany = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.UpdateAsync(admin, branch.Id, new UpdateRoleInput("Branch", null, ["Sales.View"], RoleScope.Company, null)));
        var otherBranch = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.UpdateAsync(admin, branch.Id, new UpdateRoleInput("Branch", null, ["Sales.View"], RoleScope.Branch, w.B2.Id)));
        var same = await w.Roles.UpdateAsync(admin, branch.Id, new UpdateRoleInput("Branch", "d", ["Sales.View", "Sales.Create"], RoleScope.Branch, w.B1.Id));

        Assert.Equal("scopeType", toBranch.Field);
        Assert.Equal("scopeType", toCompany.Field);
        Assert.Equal("branchId", otherBranch.Field);
        Assert.Equal(["Sales.Create", "Sales.View"], await w.CodesOfAsync(same.Id));
    }

    [Fact]
    public async Task UpdateAsync_SystemRole_IsProtected()
    {
        var (w, admin) = await AdminWorldAsync();

        var ex = await Assert.ThrowsAsync<RoleRuleException>(() => w.Roles.UpdateAsync(admin, w.Administrator.Id, new UpdateRoleInput("Administrator", null, [], null, null)));

        Assert.Equal(RoleErrorCodes.SystemRoleProtected, ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_AddingUnheldCodes_IsRejected_ButKeepingOrRemovingThemIsAllowed()
    {
        var w = new RbacWorld();
        var manager = await ManagerAsync(w);
        var seller = w.CompanyRole("Seller", "Sales.View", "Sales.Create"); // Sales.Create is not held by the manager

        var add = await Assert.ThrowsAsync<DomainValidationException>(() => w.Roles.UpdateAsync(manager, seller.Id, new UpdateRoleInput("Seller", null, ["Sales.View", "Sales.Create", "Purchase.View"], null, null)));
        var keep = await w.Roles.UpdateAsync(manager, seller.Id, new UpdateRoleInput("Seller 2", null, ["Sales.View", "Sales.Create"], null, null));
        var drop = await w.Roles.UpdateAsync(manager, seller.Id, new UpdateRoleInput("Seller 2", null, ["Sales.View"], null, null));

        Assert.Contains("Purchase.View", add.Message);
        Assert.Equal("Seller 2", keep.Name);
        Assert.Equal(["Sales.View"], await w.CodesOfAsync(drop.Id));
    }

    [Fact]
    public async Task UpdateAsync_ChangingPermissions_BumpsEveryActiveHolder_AndNobodyElse()
    {
        var (w, admin) = await AdminWorldAsync();
        var role = w.CompanyRole("Seller", "Sales.View");
        var (_, holder1) = w.CompanyWideWith("h1", role);
        var (_, holder2) = w.BranchMemberWith("h2", role, w.B1);
        var (_, other) = w.CompanyWideWith("other", w.CompanyRole("Other", "Sales.View"));
        var (_, ended) = w.CompanyWideWith("ended", role);
        var row = (await w.AssignmentsOfAsync(ended)).Single();
        w.Db.ChangeTracker.Clear();
        var tracked = w.Db.UserRoleAssignments.AsEnumerable().Single(a => a.Id == row.Id);
        tracked.Status = AssignmentStatus.Ended;
        tracked.EndAt = DateTimeOffset.UtcNow;
        await w.Db.SaveChangesAsync();
        var before = new[] { await w.VersionOfAsync(holder1), await w.VersionOfAsync(holder2), await w.VersionOfAsync(other), await w.VersionOfAsync(ended) };

        await w.Roles.UpdateAsync(admin, role.Id, new UpdateRoleInput("Seller", null, ["Sales.View", "Sales.Create"], null, null));

        Assert.Equal([before[0] + 1, before[1] + 1, before[2], before[3]],
            [await w.VersionOfAsync(holder1), await w.VersionOfAsync(holder2), await w.VersionOfAsync(other), await w.VersionOfAsync(ended)]);
    }

    [Fact]
    public async Task UpdateAsync_RenameOnly_DoesNotBumpHolders()
    {
        var (w, admin) = await AdminWorldAsync();
        var role = w.CompanyRole("Seller", "Sales.View");
        var (_, holder) = w.CompanyWideWith("h1", role);
        var before = await w.VersionOfAsync(holder);

        await w.Roles.UpdateAsync(admin, role.Id, new UpdateRoleInput("Seller Renamed", "text", ["Sales.View"], null, null));

        Assert.Equal(before, await w.VersionOfAsync(holder));
    }

    // ---- duplicate / status ----

    [Fact]
    public async Task DuplicateAsync_CopiesCodesAtTheSameScope_AsNonSystem_WithAnUniqueName()
    {
        var (w, admin) = await AdminWorldAsync();
        var branch = await w.Roles.CreateAsync(admin, Input("Clerk", ["Sales.View"], RoleScope.Branch, w.B1.Id));

        var first = await w.Roles.DuplicateAsync(admin, branch.Id);
        var second = await w.Roles.DuplicateAsync(admin, branch.Id);
        var ofAdmin = await w.Roles.DuplicateAsync(admin, w.Administrator.Id);

        Assert.Equal(("Clerk copy", "Clerk copy 2"), (first.Name, second.Name));
        Assert.Equal((RoleScope.Branch, w.B1.Id, false), (first.ScopeType, first.BranchId, first.IsSystem));
        Assert.Equal(["Sales.View"], await w.CodesOfAsync(first.Id));
        Assert.False(ofAdmin.IsSystem);
        Assert.Equal(Sellora.Domain.Permissions.PermissionCatalog.All.Count, (await w.CodesOfAsync(ofAdmin.Id)).Count);
    }

    [Fact]
    public async Task DuplicateAsync_OfARoleWithUnheldCodes_IsRoleEscalationForbidden()
    {
        var w = new RbacWorld();
        var manager = await ManagerAsync(w);
        var cashier = w.CompanyRole("Cashier", "Sales.View", "Sales.Create");

        var ex = await Assert.ThrowsAsync<AccessManagementForbiddenException>(() => w.Roles.DuplicateAsync(manager, cashier.Id));

        Assert.Equal(RoleErrorCodes.RoleEscalationForbidden, ex.Code);
        Assert.Contains("Sales.Create", ex.Message);
    }

    [Fact]
    public async Task SetActiveAsync_SystemRoleIsProtected_RoleWithActiveHoldersIsInUse_AndDisabledHoldersDoNotCount()
    {
        var (w, admin) = await AdminWorldAsync();
        var role = w.CompanyRole("Seller", "Sales.View");
        var (user, _) = w.CompanyWideWith("h1", role);

        var system = await Assert.ThrowsAsync<RoleRuleException>(() => w.Roles.SetActiveAsync(admin, w.Administrator.Id, false));
        var inUse = await Assert.ThrowsAsync<RoleRuleException>(() => w.Roles.SetActiveAsync(admin, role.Id, false));
        w.Db.ChangeTracker.Clear();
        w.Db.Users.AsEnumerable().Single(x => x.Id == user.Id).Status = UserStatus.Disabled;
        await w.Db.SaveChangesAsync();
        await w.Roles.SetActiveAsync(admin, role.Id, false);

        Assert.Equal(RoleErrorCodes.SystemRoleProtected, system.Code);
        Assert.Equal("role_in_use", inUse.Code);
        Assert.False(w.Db.Roles.AsEnumerable().Single(r => r.Id == role.Id).IsActive);
    }

    [Fact]
    public async Task SetActiveAsync_Reactivating_BumpsRemainingHolders()
    {
        var (w, admin) = await AdminWorldAsync();
        var role = w.CompanyRole("Seller", "Sales.View");
        var (user, membership) = w.CompanyWideWith("h1", role);
        w.Db.ChangeTracker.Clear();
        w.Db.Users.AsEnumerable().Single(x => x.Id == user.Id).Status = UserStatus.Disabled;
        await w.Db.SaveChangesAsync();
        await w.Roles.SetActiveAsync(admin, role.Id, false);
        var before = await w.VersionOfAsync(membership);

        await w.Roles.SetActiveAsync(admin, role.Id, true);

        Assert.True(await w.VersionOfAsync(membership) > before);
    }
}
