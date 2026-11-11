using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Roles;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Roles;

/// <summary>
/// Phase 7 assignment rules (D3, D9, D10 R4) on EF InMemory: path rights per scope (Q1), codes held at the role's scope, self change, Administrator only by Administrators,
/// branch roles need an allocation, close-only history with audit and AccessVersion bumps, the last Administrator, and the RoleId compat entry points.
/// </summary>
public class RoleAssignmentServiceTests
{
    private static readonly string[] AssignerCodes = ["Company.Users.AssignRoles", "Users.View", "Sales.View", "Roles.View"];

    private sealed record World(RbacWorld W, RoleActor Admin, User AdminUser, UserCompanyMembership AdminMembership);

    private static async Task<World> NewAsync()
    {
        var w = new RbacWorld();
        var (u, m) = w.CompanyWideWith("admin", w.Administrator);
        return new World(w, await w.ActorOfAsync(u, m), u, m);
    }

    private static async Task<RoleActor> AssignerAsync(RbacWorld w, string name = "assigner", CompanyAccessScope scope = CompanyAccessScope.CompanyWide, params string[] extra)
    {
        var role = w.CompanyRole("Assigner " + name, [.. AssignerCodes, .. extra]);
        var (u, m) = scope == CompanyAccessScope.CompanyWide ? w.CompanyWideWith(name, role) : w.BranchMemberWith(name, role, w.B1);
        return await w.ActorOfAsync(u, m);
    }

    private static (User User, UserCompanyMembership Membership) Target(RbacWorld w, string name = "target") => w.CompanyWideWith(name, w.CompanyRole("Empty " + name));

    [Fact]
    public async Task AssignAsync_CompanyRole_AddsAnActiveRow_BumpsTheVersion_AndAudits()
    {
        var x = await NewAsync();
        var (user, m) = Target(x.W);
        var role = x.W.CompanyRole("Seller", "Sales.View");
        var before = await x.W.VersionOfAsync(m);

        var row = await x.W.Assignments.AssignAsync(x.Admin, user.Id, role.Id, "  hired  ");

        Assert.Equal((role.Id, RoleScope.Company, AssignmentStatus.Active, "hired", x.Admin.UserId), (row.RoleId, row.ScopeType, row.Status, row.AssignmentReason, row.CreatedByUserId));
        Assert.Equal(before + 1, await x.W.VersionOfAsync(m));
        var audit = Assert.Single(await x.W.AuditAsync(AuditActions.RoleAssignmentCreated));
        Assert.Equal((user.Id, x.Admin.UserId), (audit.SubjectUserId, audit.ActorUserId));
    }

    [Fact]
    public async Task AssignAsync_TheSameRoleTwice_AndAnInactiveRole_AreRejectedOnRoleId()
    {
        var x = await NewAsync();
        var (user, _) = Target(x.W);
        var role = x.W.CompanyRole("Seller", "Sales.View");
        var retired = x.W.CompanyRole("Retired", "Sales.View");
        await x.W.SetRoleActiveAsync(retired, false);
        await x.W.Assignments.AssignAsync(x.Admin, user.Id, role.Id, null);

        var twice = await Assert.ThrowsAsync<DomainValidationException>(() => x.W.Assignments.AssignAsync(x.Admin, user.Id, role.Id, null));
        var inactive = await Assert.ThrowsAsync<DomainValidationException>(() => x.W.Assignments.AssignAsync(x.Admin, user.Id, retired.Id, null));

        Assert.All(new[] { twice, inactive }, e => Assert.Equal("roleId", e.Field));
    }

    [Fact]
    public async Task AssignAsync_UnknownRole_IsRejectedOnRoleId_UnknownUserIsNotFound_AndRevokedMembershipIsNotFound()
    {
        var x = await NewAsync();
        var (user, m) = Target(x.W, "gone");
        w_Revoke(x.W, m);
        var role = x.W.CompanyRole("Seller", "Sales.View");
        var (live, _) = Target(x.W, "live");

        var unknownRole = await Assert.ThrowsAsync<DomainValidationException>(() => x.W.Assignments.AssignAsync(x.Admin, live.Id, Guid.NewGuid(), null));
        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => x.W.Assignments.AssignAsync(x.Admin, Guid.NewGuid(), role.Id, null));
        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => x.W.Assignments.AssignAsync(x.Admin, user.Id, role.Id, null));

        Assert.Equal("roleId", unknownRole.Field);
    }

    private static void w_Revoke(RbacWorld w, UserCompanyMembership m)
    {
        var tracked = w.Db.UserCompanyMemberships.AsEnumerable().Single(x => x.Id == m.Id);
        tracked.Status = MembershipStatus.Revoked;
        w.Db.SaveChanges();
    }

    [Fact]
    public async Task AssignAsync_SuspendedMembership_CanStillBeManaged()
    {
        var x = await NewAsync();
        var (user, m) = Target(x.W, "susp");
        var tracked = x.W.Db.UserCompanyMemberships.AsEnumerable().Single(y => y.Id == m.Id);
        tracked.Status = MembershipStatus.Suspended;
        x.W.Db.SaveChanges();

        var row = await x.W.Assignments.AssignAsync(x.Admin, user.Id, x.W.CompanyRole("Seller", "Sales.View").Id, null);

        Assert.Equal(AssignmentStatus.Active, row.Status);
    }

    [Fact]
    public async Task AssignAsync_ByAnActorWithoutAssignRoles_IsRoleAssignmentForbidden_EvenWithUsersEdit()
    {
        var w = new RbacWorld();
        var (u, m) = w.CompanyWideWith("legacy", w.CompanyRole("User editor", "Users.View", "Users.Edit", "Roles.View", "Sales.View"));
        var actor = await w.ActorOfAsync(u, m);
        var (target, _) = Target(w);
        var role = w.CompanyRole("Seller", "Sales.View");

        var ex = await Assert.ThrowsAsync<AccessManagementForbiddenException>(() => w.Assignments.AssignAsync(actor, target.Id, role.Id, null));

        Assert.Equal(RoleErrorCodes.RoleAssignmentForbidden, ex.Code);
    }

    [Fact]
    public async Task AssignAsync_ByAnAssignedBranchesActor_IsRoleAssignmentForbidden_Q1()
    {
        var w = new RbacWorld();
        var actor = await AssignerAsync(w, "scoped", CompanyAccessScope.AssignedBranches);
        var (target, _) = Target(w);
        var role = w.CompanyRole("Seller", "Sales.View");

        var ex = await Assert.ThrowsAsync<AccessManagementForbiddenException>(() => w.Assignments.AssignAsync(actor, target.Id, role.Id, null));

        Assert.Equal(RoleErrorCodes.RoleAssignmentForbidden, ex.Code);
    }

    [Fact]
    public async Task AssignAsync_RoleWithCodesTheActorLacks_IsRoleEscalationForbidden_AndLeavesNoRow()
    {
        var w = new RbacWorld();
        var actor = await AssignerAsync(w);
        var (target, m) = Target(w);
        var cashier = w.CompanyRole("Cashier", "Sales.View", "Sales.Create");

        var ex = await Assert.ThrowsAsync<AccessManagementForbiddenException>(() => w.Assignments.AssignAsync(actor, target.Id, cashier.Id, null));

        Assert.Equal(RoleErrorCodes.RoleEscalationForbidden, ex.Code);
        Assert.Contains("Sales.Create", ex.Message);
        Assert.DoesNotContain(await w.AssignmentsOfAsync(m), a => a.RoleId == cashier.Id);
    }

    [Fact]
    public async Task AssignAsync_TheAdministratorRole_OnlyByAnAdministrator()
    {
        var w = new RbacWorld();
        var actor = await AssignerAsync(w);
        var (target, _) = Target(w);

        var ex = await Assert.ThrowsAsync<AccessManagementForbiddenException>(() => w.Assignments.AssignAsync(actor, target.Id, w.Administrator.Id, null));

        Assert.Equal(RoleErrorCodes.AdministratorAssignmentForbidden, ex.Code);
    }

    [Fact]
    public async Task AssignAsync_ToAnAdministratorTarget_ByANonAdministrator_IsAdministratorAssignmentForbidden()
    {
        var w = new RbacWorld();
        var actor = await AssignerAsync(w);
        var (admin, _) = w.CompanyWideWith("boss", w.Administrator);
        var role = w.CompanyRole("Seller", "Sales.View");

        var ex = await Assert.ThrowsAsync<AccessManagementForbiddenException>(() => w.Assignments.AssignAsync(actor, admin.Id, role.Id, null));

        Assert.Equal(RoleErrorCodes.AdministratorAssignmentForbidden, ex.Code);
    }

    [Fact]
    public async Task AssignAsync_Administrator_PromotesAnAssignedBranchesTargetToCompanyWide()
    {
        var x = await NewAsync();
        var (user, m) = x.W.BranchMemberWith("scoped", x.W.CompanyRole("Empty"), x.W.B1);

        await x.W.Assignments.AssignAsync(x.Admin, user.Id, x.W.Administrator.Id, null);

        x.W.Db.ChangeTracker.Clear();
        Assert.Equal(CompanyAccessScope.CompanyWide, x.W.Db.UserCompanyMemberships.AsEnumerable().Single(y => y.Id == m.Id).AccessScope);
    }

    [Fact]
    public async Task AssignAsync_ToYourself_IsSelfRoleChangeForbidden()
    {
        var x = await NewAsync();
        var role = x.W.CompanyRole("Seller", "Sales.View");

        var ex = await Assert.ThrowsAsync<AccessManagementForbiddenException>(() => x.W.Assignments.AssignAsync(x.Admin, x.AdminUser.Id, role.Id, null));

        Assert.Equal(RoleErrorCodes.SelfRoleChangeForbidden, ex.Code);
    }

    [Fact]
    public async Task AssignAsync_Reason_IsTrimmedAndLimitedTo500()
    {
        var x = await NewAsync();
        var (user, _) = Target(x.W);
        var role = x.W.CompanyRole("Seller", "Sales.View");

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => x.W.Assignments.AssignAsync(x.Admin, user.Id, role.Id, new string('x', 501)));

        Assert.Equal("reason", ex.Field);
    }

    // ---- branch roles ----

    [Fact]
    public async Task AssignAsync_BranchRole_NeedsAnAssignedBranchesMemberAllocatedToTheBranch()
    {
        var x = await NewAsync();
        var role = x.W.BranchRole("B1 seller", x.W.B1, "Sales.View");
        var (wide, _) = x.W.CompanyWideWith("wide", x.W.CompanyRole("Empty wide"));
        var (atB2, _) = x.W.BranchMemberWith("atb2", x.W.CompanyRole("Empty b2"), x.W.B2);
        var (atB1, m1) = x.W.BranchMemberWith("atb1", x.W.CompanyRole("Empty b1"), x.W.B1);

        var wideEx = await Assert.ThrowsAsync<DomainValidationException>(() => x.W.Assignments.AssignAsync(x.Admin, wide.Id, role.Id, null));
        var b2Ex = await Assert.ThrowsAsync<DomainValidationException>(() => x.W.Assignments.AssignAsync(x.Admin, atB2.Id, role.Id, null));
        var row = await x.W.Assignments.AssignAsync(x.Admin, atB1.Id, role.Id, null);

        Assert.All(new[] { wideEx, b2Ex }, e => Assert.Equal("branchId", e.Field));
        Assert.Equal((RoleScope.Branch, x.W.B1.Id), (row.ScopeType, row.BranchId));
        Assert.Contains(await x.W.AssignmentsOfAsync(m1), a => a.Id == row.Id);
    }

    [Fact]
    public async Task AssignAsync_BranchAdmin_AssignsRolesOfItsBranchOnly_NeverCompanyRoles()
    {
        var w = new RbacWorld();
        var adminRole = w.BranchRole("B1 admin", w.B1, "Branch.Roles.View", "Branch.Users.AssignRoles", "Sales.View");
        var (adminUser, adminM) = w.BranchMemberWith("ba", adminRole, w.B1);
        var actor = await w.ActorOfAsync(adminUser, adminM);
        var clerk = w.BranchRole("B1 clerk", w.B1, "Sales.View");
        var other = w.BranchRole("B2 clerk", w.B2, "Sales.View");
        var company = w.CompanyRole("Company seller", "Sales.View");
        var (member, _) = w.BranchMemberWith("member", w.CompanyRole("Empty member"), w.B1, w.B2);

        var ok = await w.Assignments.AssignAsync(actor, member.Id, clerk.Id, null);
        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => w.Assignments.AssignAsync(actor, member.Id, other.Id, null));
        var companyEx = await Assert.ThrowsAsync<AccessManagementForbiddenException>(() => w.Assignments.AssignAsync(actor, member.Id, company.Id, null));
        var adminEx = await Assert.ThrowsAsync<AccessManagementForbiddenException>(() => w.Assignments.AssignAsync(actor, member.Id, w.Administrator.Id, null));

        Assert.Equal(RoleScope.Branch, ok.ScopeType);
        Assert.Equal(RoleErrorCodes.RoleAssignmentForbidden, companyEx.Code);
        Assert.Equal(RoleErrorCodes.RoleAssignmentForbidden, adminEx.Code);
    }

    // ---- end ----

    [Fact]
    public async Task EndAsync_ClosesTheRowOnce_WithReason_Audit_AndBump_ThenItIsNotFound()
    {
        var x = await NewAsync();
        var (user, m) = Target(x.W);
        var role = x.W.CompanyRole("Seller", "Sales.View");
        var row = await x.W.Assignments.AssignAsync(x.Admin, user.Id, role.Id, null);
        var before = await x.W.VersionOfAsync(m);

        await x.W.Assignments.EndAsync(x.Admin, user.Id, row.Id, "left");

        var ended = (await x.W.AssignmentsOfAsync(m)).Single(a => a.Id == row.Id);
        Assert.Equal((AssignmentStatus.Ended, "left", x.Admin.UserId), (ended.Status, ended.EndReason, ended.EndedByUserId));
        Assert.NotNull(ended.EndAt);
        Assert.Equal(before + 1, await x.W.VersionOfAsync(m));
        Assert.Single(await x.W.AuditAsync(AuditActions.RoleAssignmentEnded));
        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => x.W.Assignments.EndAsync(x.Admin, user.Id, row.Id, "again"));
    }

    [Fact]
    public async Task EndAsync_NeedsAReason_AndForeignAssignmentIdsAreNotFound()
    {
        var x = await NewAsync();
        var (user, _) = Target(x.W);
        var (other, otherM) = Target(x.W, "other");
        var role = x.W.CompanyRole("Seller", "Sales.View");
        var row = await x.W.Assignments.AssignAsync(x.Admin, user.Id, role.Id, null);

        var blank = await Assert.ThrowsAsync<DomainValidationException>(() => x.W.Assignments.EndAsync(x.Admin, user.Id, row.Id, "  "));
        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => x.W.Assignments.EndAsync(x.Admin, other.Id, row.Id, "x")); // the row belongs to another membership

        Assert.Equal("reason", blank.Field);
        _ = otherM;
    }

    [Fact]
    public async Task EndAsync_Self_AndAdministratorByNonAdministrator_AreForbidden()
    {
        var w = new RbacWorld();
        var actor = await AssignerAsync(w);
        var (boss, bossM) = w.CompanyWideWith("boss", w.Administrator);
        var bossRow = (await w.AssignmentsOfAsync(bossM)).Single();
        var selfRow = (await w.AssignmentsOfAsync(w.Db.UserCompanyMemberships.AsEnumerable().Single(m => m.UserId == actor.UserId))).First();

        var admin = await Assert.ThrowsAsync<AccessManagementForbiddenException>(() => w.Assignments.EndAsync(actor, boss.Id, bossRow.Id, "x"));
        var self = await Assert.ThrowsAsync<AccessManagementForbiddenException>(() => w.Assignments.EndAsync(actor, actor.UserId, selfRow.Id, "x"));

        Assert.Equal(RoleErrorCodes.AdministratorAssignmentForbidden, admin.Code);
        Assert.Equal(RoleErrorCodes.SelfRoleChangeForbidden, self.Code);
    }

    [Fact]
    public async Task EndAsync_TheLastAdministrator_IsLastAdministratorProtected_AndAnotherRemainingIsAllowed()
    {
        var x = await NewAsync();
        var (second, secondM) = x.W.CompanyWideWith("second", x.W.Administrator);
        var secondRow = (await x.W.AssignmentsOfAsync(secondM)).Single();

        await x.W.Assignments.EndAsync(x.Admin, second.Id, secondRow.Id, "demoted"); // the actor remains an Administrator

        // now only the actor holds it: a second actor with the Administrator role elsewhere would be needed to end it, so end it through a disabled-owner scenario
        var (third, thirdM) = x.W.CompanyWideWith("third", x.W.Administrator);
        var thirdRow = (await x.W.AssignmentsOfAsync(thirdM)).Single();
        x.W.Db.ChangeTracker.Clear();
        x.W.Db.Users.AsEnumerable().Single(u => u.Id == x.AdminUser.Id).Status = UserStatus.Disabled; // the actor no longer counts as a remaining administrator
        x.W.Db.SaveChanges();

        var ex = await Assert.ThrowsAsync<RoleRuleException>(() => x.W.Assignments.EndAsync(x.Admin, third.Id, thirdRow.Id, "oops"));

        Assert.Equal(RoleErrorCodes.LastAdministratorProtected, ex.Code);
    }

    // ---- compat entry points ----

    [Fact]
    public async Task SetSingleCompanyRoleAsync_ReplacesTheCompanyRole_KeepsBranchRoles_AndIsANoOpWhenUnchanged()
    {
        var x = await NewAsync();
        var first = x.W.CompanyRole("First", "Sales.View");
        var second = x.W.CompanyRole("Second", "Purchase.View");
        var branchRole = x.W.BranchRole("B1 clerk", x.W.B1, "Sales.View");
        var (user, m) = x.W.BranchMemberWith("u", first, x.W.B1);
        await x.W.Assignments.AssignAsync(x.Admin, user.Id, branchRole.Id, null);

        var before = await x.W.VersionOfAsync(m);
        await x.W.Assignments.SetSingleCompanyRoleAsync(x.Admin, user.Id, first.Id); // unchanged: no-op
        var afterNoOp = await x.W.VersionOfAsync(m);
        Assert.Equal(before, afterNoOp);
        await x.W.Assignments.SetSingleCompanyRoleAsync(x.Admin, user.Id, second.Id);

        var rows = await x.W.AssignmentsOfAsync(m);
        Assert.True(await x.W.VersionOfAsync(m) > afterNoOp);
        Assert.Equal(new[] { branchRole.Id, second.Id }.Order(), rows.Where(r => r.Status == AssignmentStatus.Active).Select(r => r.RoleId).Order());
        Assert.Contains(rows, r => r.RoleId == first.Id && r.Status == AssignmentStatus.Ended);
    }

    [Fact]
    public async Task SetSingleCompanyRoleAsync_BranchRoleId_IsRejectedOnRoleId()
    {
        var x = await NewAsync();
        var branchRole = x.W.BranchRole("B1 clerk", x.W.B1, "Sales.View");
        var (user, _) = Target(x.W);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => x.W.Assignments.SetSingleCompanyRoleAsync(x.Admin, user.Id, branchRole.Id));

        Assert.Equal("roleId", ex.Field);
    }

    [Fact]
    public async Task SetSingleCompanyRoleAsync_ReplacingTheOnlyAdministrator_KeepsTheLastAdministratorRule()
    {
        var x = await NewAsync();
        var (second, _) = x.W.CompanyWideWith("second", x.W.Administrator);
        var plain = x.W.CompanyRole("Plain", "Sales.View");
        x.W.Db.ChangeTracker.Clear();
        x.W.Db.Users.AsEnumerable().Single(u => u.Id == x.AdminUser.Id).Status = UserStatus.Disabled; // the actor no longer counts
        x.W.Db.SaveChanges();

        var ex = await Assert.ThrowsAsync<RoleRuleException>(() => x.W.Assignments.SetSingleCompanyRoleAsync(x.Admin, second.Id, plain.Id));

        Assert.Equal(RoleErrorCodes.LastAdministratorProtected, ex.Code);
    }

    [Fact]
    public async Task StageInitialCompanyRoleAsync_StagesWithoutSaving_AndAppliesTheSameRules()
    {
        var x = await NewAsync();
        var role = x.W.CompanyRole("Seller", "Sales.View");
        var branchRole = x.W.BranchRole("B1", x.W.B1, "Sales.View");
        var membership = new UserCompanyMembership { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), CompanyId = TestCompany.DefaultId, Status = MembershipStatus.Active };

        var service = x.W.Assignments;
        await service.StageInitialCompanyRoleAsync(x.Admin, membership, role.Id);
        var staged = x.W.Db.ChangeTracker.Entries<UserRoleAssignment>().Single(e => e.Entity.UserCompanyMembershipId == membership.Id);
        var branchEx = await Assert.ThrowsAsync<DomainValidationException>(() => service.StageInitialCompanyRoleAsync(x.Admin, membership, branchRole.Id));

        Assert.Equal(Microsoft.EntityFrameworkCore.EntityState.Added, staged.State); // staged, not saved: the caller saves it with the new membership
        Assert.Equal((role.Id, RoleScope.Company, "Initial role"), (staged.Entity.RoleId, staged.Entity.ScopeType, staged.Entity.AssignmentReason));
        Assert.Equal("roleId", branchEx.Field);
    }
}
