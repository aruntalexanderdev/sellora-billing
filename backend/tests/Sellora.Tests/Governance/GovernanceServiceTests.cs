using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Tests.TestData;
using static Sellora.Tests.TestData.GovernanceWorld;

namespace Sellora.Tests.Governance;

/// <summary>
/// Phase 6 T5/T8 on EF InMemory: governance rules R1 (no self change), R2 (delegation ceiling), R3 (Owner changes need ManageOwners),
/// R4 (the company keeps a root owner), titles grant nothing, reason on every write, audit and AccessVersion on every write,
/// delegated administration. InMemory has no transaction, so "R4 rolls back" is proved in the PostgreSQL GovernanceApiTests.
/// </summary>
public class GovernanceServiceTests
{
    private static async Task<AccessManagementForbiddenException> ForbiddenAsync(Func<Task> action, string code)
    {
        var ex = await Assert.ThrowsAsync<AccessManagementForbiddenException>(action);
        Assert.Equal(code, ex.Code);
        return ex;
    }

    private static async Task<GovernanceRuleException> RuleAsync(Func<Task> action, string code)
    {
        var ex = await Assert.ThrowsAsync<GovernanceRuleException>(action);
        Assert.Equal(code, ex.Code);
        return ex;
    }

    private static async Task<DomainValidationException> FieldAsync(Func<Task> action, string field)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(action);
        Assert.Equal(field, ex.Field);
        return ex;
    }

    /// <summary>Owner O (all five), a delegate D (Director holding View, ManageMembers, ManageOwners, AssignPermissions) and a plain staff user.</summary>
    private sealed record Setup(GovernanceWorld W, User Owner, CompanyGovernanceMember OwnerMember, User Delegate, CompanyGovernanceMember DelegateMember, User Staff);

    private static Setup NewSetup()
    {
        var w = new GovernanceWorld();
        var (owner, ownerMember) = w.AddOwner("owner1");
        var d = w.AddUser("delegate1");
        var dm = w.AddMember(d, GovernanceTitle.Director, View, Members, Owners, Assign);
        var staff = w.AddUser("staff1");
        return new Setup(w, owner, ownerMember, d, dm, staff);
    }

    /// <summary>Like <see cref="NewSetup"/> but the delegate holds every governance code (ending a member revokes all their grants: R2).</summary>
    private static Setup FullDelegateSetup()
    {
        var w = new GovernanceWorld();
        var (owner, ownerMember) = w.AddOwner("owner1");
        var d = w.AddUser("delegate1");
        var dm = w.AddMember(d, GovernanceTitle.Director, AllCodes);
        var staff = w.AddUser("staff1");
        return new Setup(w, owner, ownerMember, d, dm, staff);
    }

    // ---- R1: no self change ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddMember_Self_IsForbidden()
    {
        var s = NewSetup();

        await ForbiddenAsync(() => s.W.Service.AddMemberAsync(Actor(s.Delegate), new AddGovernanceMemberInput(s.Delegate.Id, GovernanceTitle.Director, null, "r"), default),
            GovernanceErrorCodes.SelfGovernanceChangeForbidden);
    }

    [Fact]
    public async Task ChangeTitle_Self_IsForbidden()
    {
        var s = NewSetup();

        await ForbiddenAsync(() => s.W.Service.ChangeTitleAsync(Actor(s.Delegate), s.DelegateMember.Id, GovernanceTitle.BoardMember, "r", default),
            GovernanceErrorCodes.SelfGovernanceChangeForbidden);
    }

    [Fact]
    public async Task SetPermissions_Self_IsForbidden()
    {
        var s = NewSetup();

        await ForbiddenAsync(() => s.W.Service.SetPermissionsAsync(Actor(s.Delegate), s.DelegateMember.Id, [View], "r", default),
            GovernanceErrorCodes.SelfGovernanceChangeForbidden);
    }

    [Fact]
    public async Task EndMember_Self_IsForbidden_EvenForTheOnlyOwner()
    {
        var s = NewSetup();

        await ForbiddenAsync(() => s.W.Service.EndMemberAsync(Actor(s.Owner), s.OwnerMember.Id, "r", default), GovernanceErrorCodes.SelfGovernanceChangeForbidden);
        Assert.Equal(GovernanceMemberStatus.Active, s.W.Reload(s.OwnerMember).Status);
    }

    [Fact]
    public async Task GrantAndRemoveAdministrator_Self_AreForbidden()
    {
        var w = new GovernanceWorld();
        var (owner, _) = w.AddOwner("owner1");
        var d = w.AddUser("delegate1");
        w.AddMember(d, GovernanceTitle.DelegatedAdministrator, View, Admins);

        await ForbiddenAsync(() => w.Service.GrantAdministratorAsync(Actor(d), d.Id, "r", default), GovernanceErrorCodes.SelfGovernanceChangeForbidden);
        await ForbiddenAsync(() => w.Service.RemoveAdministratorAsync(Actor(owner), owner.Id, w.StaffRole.Id, "r", default), GovernanceErrorCodes.SelfGovernanceChangeForbidden);
    }

    // ---- R2: delegation ceiling -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddMember_GrantingACodeTheActorDoesNotHold_IsDelegationExceeded()
    {
        var s = NewSetup(); // delegate holds View, Members, Owners, Assign - not Admins

        await ForbiddenAsync(() => s.W.Service.AddMemberAsync(Actor(s.Delegate), new AddGovernanceMemberInput(s.Staff.Id, GovernanceTitle.Director, [Admins], "r"), default),
            GovernanceErrorCodes.DelegationExceeded);
        Assert.Empty(s.W.Audit(AuditActions.GovernanceMemberAdded));
    }

    [Fact]
    public async Task AddMember_WithGrantsButWithoutAssignPermissions_IsPermissionRequired()
    {
        var w = new GovernanceWorld();
        w.AddOwner("owner1");
        var d = w.AddUser("delegate1");
        w.AddMember(d, GovernanceTitle.Director, View, Members); // no AssignPermissions
        var staff = w.AddUser("staff1");

        await ForbiddenAsync(() => w.Service.AddMemberAsync(Actor(d), new AddGovernanceMemberInput(staff.Id, GovernanceTitle.Director, [View], "r"), default),
            GovernanceErrorCodes.PermissionRequired);
    }

    [Fact]
    public async Task SetPermissions_RevokingACodeTheActorDoesNotHold_IsDelegationExceeded()
    {
        var s = NewSetup();
        var target = s.W.AddUser("director2");
        var member = s.W.AddMember(target, GovernanceTitle.Director, View, Admins);

        await ForbiddenAsync(() => s.W.Service.SetPermissionsAsync(Actor(s.Delegate), member.Id, [View], "r", default), GovernanceErrorCodes.DelegationExceeded);
        Assert.Equal(new[] { Admins, View }.Order(), s.W.CodesOf(member));
    }

    [Fact]
    public async Task SetPermissions_GrantingACodeTheActorDoesNotHold_IsDelegationExceeded()
    {
        var s = NewSetup();
        var target = s.W.AddUser("director2");
        var member = s.W.AddMember(target, GovernanceTitle.Director, View);

        await ForbiddenAsync(() => s.W.Service.SetPermissionsAsync(Actor(s.Delegate), member.Id, [View, Admins], "r", default), GovernanceErrorCodes.DelegationExceeded);
    }

    [Fact]
    public async Task SetPermissions_WithinTheActorsCodes_ReplacesTheGrantsAuditsAndBumpsTheVersionOnce()
    {
        var s = NewSetup();
        var target = s.W.AddUser("director2");
        var member = s.W.AddMember(target, GovernanceTitle.Director, View);
        var version = s.W.AccessVersion(target);

        await s.W.Service.SetPermissionsAsync(Actor(s.Delegate), member.Id, [Members, View], "adjust", default);

        Assert.Equal(new[] { Members, View }.Order(), s.W.CodesOf(member));
        Assert.Equal(version + 1, s.W.AccessVersion(target));
        var audit = Assert.Single(s.W.Audit(AuditActions.GovernanceMemberPermissionsChanged));
        Assert.Equal("adjust", audit.Reason);
        Assert.Equal(target.Id, audit.SubjectUserId);
        Assert.Equal(s.Delegate.Id, audit.ActorUserId);
        Assert.Contains(Members, audit.After);
        Assert.DoesNotContain(Members, audit.Before);
    }

    [Fact]
    public async Task SetPermissions_Unchanged_IsRejectedOnPermissionCodes()
    {
        var s = NewSetup();
        var target = s.W.AddUser("director2");
        var member = s.W.AddMember(target, GovernanceTitle.Director, View);

        await FieldAsync(() => s.W.Service.SetPermissionsAsync(Actor(s.Delegate), member.Id, [View], "r", default), "permissionCodes");
    }

    [Fact]
    public async Task SetPermissions_UnknownCode_IsRejectedOnPermissionCodes()
    {
        var s = NewSetup();
        var target = s.W.AddUser("director2");
        var member = s.W.AddMember(target, GovernanceTitle.Director, View);

        await FieldAsync(() => s.W.Service.SetPermissionsAsync(Actor(s.Delegate), member.Id, [View, "Company.Governance.Nope"], "r", default), "permissionCodes");
        await FieldAsync(() => s.W.Service.SetPermissionsAsync(Actor(s.Delegate), member.Id, [View, "Sales.View"], "r", default), "permissionCodes");
    }

    [Fact]
    public async Task EndMember_TargetHoldsCodesTheActorDoesNot_IsDelegationExceeded()
    {
        // Ending revokes every grant, so the R2 ceiling applies: a delegate cannot end a Director holding codes the delegate lacks.
        var w = new GovernanceWorld();
        w.AddOwner("owner1");
        var d = w.AddUser("delegate1");
        w.AddMember(d, GovernanceTitle.Director, View, Members);
        var target = w.AddUser("director2");
        var member = w.AddMember(target, GovernanceTitle.Director, View, Admins, Assign);

        await ForbiddenAsync(() => w.Service.EndMemberAsync(Actor(d), member.Id, "r", default), GovernanceErrorCodes.DelegationExceeded);

        Assert.Equal(GovernanceMemberStatus.Active, w.Reload(member).Status);
    }

    // ---- R3: Owner-touching needs ManageOwners --------------------------------------------------------------------------------

    private static Setup NoManageOwnersSetup()
    {
        var w = new GovernanceWorld();
        var (owner, ownerMember) = w.AddOwner("owner1");
        var d = w.AddUser("delegate1");
        var dm = w.AddMember(d, GovernanceTitle.Director, View, Members, Assign); // no ManageOwners
        return new Setup(w, owner, ownerMember, d, dm, w.AddUser("staff1"));
    }

    [Fact]
    public async Task AddOwner_WithoutManageOwners_IsPermissionRequired()
    {
        var s = NoManageOwnersSetup();

        await ForbiddenAsync(() => s.W.Service.AddMemberAsync(Actor(s.Delegate), new AddGovernanceMemberInput(s.Staff.Id, GovernanceTitle.Owner, null, "r"), default),
            GovernanceErrorCodes.PermissionRequired);
    }

    [Fact]
    public async Task EndOwner_WithoutManageOwners_IsPermissionRequired()
    {
        var s = NoManageOwnersSetup();

        await ForbiddenAsync(() => s.W.Service.EndMemberAsync(Actor(s.Delegate), s.OwnerMember.Id, "r", default), GovernanceErrorCodes.PermissionRequired);
        Assert.Equal(GovernanceMemberStatus.Active, s.W.Reload(s.OwnerMember).Status);
    }

    [Fact]
    public async Task ChangeTitle_FromOrToOwner_WithoutManageOwners_IsPermissionRequired()
    {
        var s = NoManageOwnersSetup();
        var other = s.W.AddUser("director2");
        var otherMember = s.W.AddMember(other, GovernanceTitle.Director, View);

        await ForbiddenAsync(() => s.W.Service.ChangeTitleAsync(Actor(s.Delegate), s.OwnerMember.Id, GovernanceTitle.Director, "r", default), GovernanceErrorCodes.PermissionRequired);
        await ForbiddenAsync(() => s.W.Service.ChangeTitleAsync(Actor(s.Delegate), otherMember.Id, GovernanceTitle.Owner, "r", default), GovernanceErrorCodes.PermissionRequired);
        Assert.Equal(GovernanceTitle.Director, s.W.Reload(otherMember).Title);
    }

    [Fact]
    public async Task SetPermissions_OnAnOwner_WithoutManageOwners_IsPermissionRequired()
    {
        var s = NoManageOwnersSetup();

        await ForbiddenAsync(() => s.W.Service.SetPermissionsAsync(Actor(s.Delegate), s.OwnerMember.Id, [View], "r", default), GovernanceErrorCodes.PermissionRequired);
        Assert.Equal(AllCodes.Order(), s.W.CodesOf(s.OwnerMember));
    }

    [Fact]
    public async Task NonOwnerOperations_WithoutManageMembers_ArePermissionRequired()
    {
        var w = new GovernanceWorld();
        w.AddOwner("owner1");
        var viewer = w.AddUser("viewer1");
        w.AddMember(viewer, GovernanceTitle.BoardMember, View);
        var target = w.AddUser("director2");
        var member = w.AddMember(target, GovernanceTitle.Director, View);
        var staff = w.AddUser("staff1");

        await ForbiddenAsync(() => w.Service.AddMemberAsync(Actor(viewer), new AddGovernanceMemberInput(staff.Id, GovernanceTitle.Director, null, "r"), default), GovernanceErrorCodes.PermissionRequired);
        await ForbiddenAsync(() => w.Service.ChangeTitleAsync(Actor(viewer), member.Id, GovernanceTitle.BoardMember, "r", default), GovernanceErrorCodes.PermissionRequired);
        await ForbiddenAsync(() => w.Service.EndMemberAsync(Actor(viewer), member.Id, "r", default), GovernanceErrorCodes.PermissionRequired);
        await ForbiddenAsync(() => w.Service.SetPermissionsAsync(Actor(viewer), member.Id, [], "r", default), GovernanceErrorCodes.PermissionRequired);
    }

    // ---- titles grant nothing -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OwnerWithZeroGrants_CanDoNothing()
    {
        var w = new GovernanceWorld();
        w.AddOwner("owner1");
        var (hollow, hollowMember) = w.AddOwnerWith("hollow");
        var staff = w.AddUser("staff1");

        Assert.Empty(await Sellora.Infrastructure.Services.GovernanceRules.HeldCodesAsync(w.Db, hollow.Id, default));
        await ForbiddenAsync(() => w.Service.AddMemberAsync(Actor(hollow), new AddGovernanceMemberInput(staff.Id, GovernanceTitle.Director, null, "r"), default), GovernanceErrorCodes.PermissionRequired);
        await ForbiddenAsync(() => w.Service.GrantAdministratorAsync(Actor(hollow), staff.Id, "r", default), GovernanceErrorCodes.PermissionRequired);
        await ForbiddenAsync(() => w.Service.SetPermissionsAsync(Actor(hollow), hollowMember.Id, [View], "r", default), GovernanceErrorCodes.PermissionRequired);
    }

    [Fact]
    public async Task SystemAdministratorWithoutGovernanceMembership_HoldsNoGovernanceCode()
    {
        var w = new GovernanceWorld();
        w.AddOwner("owner1");
        var admin = w.AddUser("admin2", w.AdminRole);
        var staff = w.AddUser("staff1");

        Assert.Empty(await Sellora.Infrastructure.Services.GovernanceRules.HeldCodesAsync(w.Db, admin.Id, default));
        await ForbiddenAsync(() => w.Service.AddMemberAsync(Actor(admin), new AddGovernanceMemberInput(staff.Id, GovernanceTitle.Director, null, "r"), default), GovernanceErrorCodes.PermissionRequired);
    }

    [Fact]
    public async Task HeldCodes_AreEmptyForEndedMembersAndNonActiveMemberships()
    {
        var w = new GovernanceWorld();
        w.AddOwner("owner1");
        var ended = w.AddUser("ended1");
        var endedMember = w.AddMember(ended, GovernanceTitle.Director, View);
        var suspended = w.AddUser("suspended1", membership: MembershipStatus.Suspended);
        w.AddMember(suspended, GovernanceTitle.Director, View);
        var tracked = w.Db.CompanyGovernanceMembers.Single(m => m.Id == endedMember.Id);
        tracked.Status = GovernanceMemberStatus.Ended;
        tracked.EndAt = DateTimeOffset.UtcNow;
        w.Db.SaveChanges();
        w.Db.ChangeTracker.Clear();

        Assert.Empty(await Sellora.Infrastructure.Services.GovernanceRules.HeldCodesAsync(w.Db, ended.Id, default));
        Assert.Empty(await Sellora.Infrastructure.Services.GovernanceRules.HeldCodesAsync(w.Db, suspended.Id, default));
    }

    // ---- R4: the company keeps a root owner -----------------------------------------------------------------------------------

    /// <summary>One root owner O and a delegate D holding View, ManageMembers, ManageOwners, AssignPermissions.</summary>
    [Fact]
    public async Task EndOwner_TheLastRootOwner_IsLastOwnerProtected()
    {
        var s = FullDelegateSetup();

        await RuleAsync(() => s.W.Service.EndMemberAsync(Actor(s.Delegate), s.OwnerMember.Id, "r", default), GovernanceErrorCodes.LastOwnerProtected);
    }

    [Fact]
    public async Task EndOwner_WhenAnotherRootOwnerRemains_Succeeds_AndRemovesTheGrants()
    {
        var s = FullDelegateSetup();
        var (_, second) = s.W.AddOwner("owner2");

        await s.W.Service.EndMemberAsync(Actor(s.Delegate), s.OwnerMember.Id, "left the board", default);

        var ended = s.W.Reload(s.OwnerMember);
        Assert.Equal(GovernanceMemberStatus.Ended, ended.Status);
        Assert.NotNull(ended.EndAt);
        Assert.Equal("left the board", ended.EndReason);
        Assert.Equal(s.Delegate.Id, ended.EndedByUserId);
        Assert.Empty(s.W.CodesOf(s.OwnerMember));
        Assert.Equal(AllCodes.Order(), s.W.CodesOf(second));
        var audit = Assert.Single(s.W.Audit(AuditActions.GovernanceMemberEnded));
        Assert.Equal(s.Owner.Id, audit.SubjectUserId);
        Assert.Equal(s.OwnerMember.Id, audit.TargetId);
    }

    [Fact]
    public async Task ChangeTitle_TheLastRootOwnerAwayFromOwner_IsLastOwnerProtected()
    {
        var s = NewSetup();

        await RuleAsync(() => s.W.Service.ChangeTitleAsync(Actor(s.Delegate), s.OwnerMember.Id, GovernanceTitle.Director, "r", default), GovernanceErrorCodes.LastOwnerProtected);
    }

    [Theory]
    [InlineData(View)]
    [InlineData(Owners)]
    [InlineData(Assign)]
    public async Task SetPermissions_RemovingARootCodeFromTheLastRootOwner_IsLastOwnerProtected(string removed)
    {
        var s = NewSetup();
        var desired = AllCodes.Where(c => c != removed).ToList();

        await RuleAsync(() => s.W.Service.SetPermissionsAsync(Actor(s.Delegate), s.OwnerMember.Id, desired, "r", default), GovernanceErrorCodes.LastOwnerProtected);
    }

    [Theory]
    [InlineData(Members)]
    [InlineData(Admins)]
    public async Task SetPermissions_RemovingANonRootCodeFromTheLastRootOwner_IsAllowed(string removed)
    {
        var w = new GovernanceWorld();
        var (owner, ownerMember) = w.AddOwner("owner1");
        var d = w.AddUser("delegate1");
        w.AddMember(d, GovernanceTitle.Director, AllCodes); // holds everything, so it may revoke anything
        var desired = AllCodes.Where(c => c != removed).ToList();

        await w.Service.SetPermissionsAsync(Actor(d), ownerMember.Id, desired, "r", default);

        Assert.Equal(desired.Order(), w.CodesOf(ownerMember));
        _ = owner;
    }

    [Fact]
    public async Task AddMember_Owner_WithTheRootCodes_ByAnotherOwner_Succeeds_AndAuditsOnce()
    {
        var s = NewSetup();
        var candidate = s.W.AddUser("candidate1");
        var version = s.W.AccessVersion(candidate);

        var id = await s.W.Service.AddMemberAsync(Actor(s.Delegate), new AddGovernanceMemberInput(candidate.Id, GovernanceTitle.Owner, [View, Owners, Assign], "new owner"), default);

        var member = s.W.Db.CompanyGovernanceMembers.Single(m => m.Id == id);
        Assert.Equal(GovernanceTitle.Owner, member.Title);
        Assert.Equal(s.Delegate.Id, member.CreatedByUserId);
        Assert.Equal(new[] { Assign, Owners, View }.Order(), s.W.CodesOf(member));
        Assert.Equal(version + 1, s.W.AccessVersion(candidate));
        var audit = Assert.Single(s.W.Audit(AuditActions.GovernanceMemberAdded));
        Assert.Equal("new owner", audit.Reason);
        Assert.Equal(candidate.Id, audit.SubjectUserId);
        Assert.Equal(s.Delegate.Id, audit.ActorUserId);
        Assert.Equal("test-correlation", audit.CorrelationId);
    }

    [Fact]
    public async Task EndLastOwner_ThrowsAfterSaving_SoTheCallersScopeMustRollBack_KnownOpenPointR4()
    {
        // R4 runs on the saved state: in InMemory (no transaction) the member is already Ended when the rule throws. In production the
        // AccessManagementScope transaction rolls the change back - proved in the PostgreSQL GovernanceApiTests.
        var s = FullDelegateSetup();

        await RuleAsync(() => s.W.Service.EndMemberAsync(Actor(s.Delegate), s.OwnerMember.Id, "r", default), GovernanceErrorCodes.LastOwnerProtected);

        Assert.Equal(GovernanceMemberStatus.Ended, s.W.Reload(s.OwnerMember).Status);
    }

    [Fact]
    public async Task EndMember_NonOwner_NeverTriggersR4EvenWithoutAnyOwner()
    {
        var w = new GovernanceWorld();
        var d = w.AddUser("delegate1");
        w.AddMember(d, GovernanceTitle.Director, View, Members);
        var target = w.AddUser("director2");
        var member = w.AddMember(target, GovernanceTitle.Director, View);

        // a company without any root owner is already inconsistent: the rule is checked after the write and reports it
        await RuleAsync(() => w.Service.EndMemberAsync(Actor(d), member.Id, "r", default), GovernanceErrorCodes.LastOwnerProtected);
    }

    // ---- add member -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddMember_UserOutsideTheCompanyOrUnknown_IsNotFound()
    {
        var s = NewSetup();

        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() =>
            s.W.Service.AddMemberAsync(Actor(s.Delegate), new AddGovernanceMemberInput(Guid.NewGuid(), GovernanceTitle.Director, null, "r"), default));
    }

    [Fact]
    public async Task AddMember_UserWithMembershipInAnotherCompanyOnly_IsNotFound()
    {
        var s = NewSetup();
        var foreign = s.W.AddUser("foreign1");
        var own = s.W.Db.UserCompanyMemberships.Single(m => m.UserId == foreign.Id);
        s.W.Db.UserRoleAssignments.RemoveRange(s.W.Db.UserRoleAssignments.Where(a => a.UserCompanyMembershipId == own.Id));
        s.W.Db.UserCompanyMemberships.Remove(own);
        s.W.Db.Companies.Add(new Company { Id = TestCompany.OtherId, Code = "B", Name = "B", IsActive = true });
        s.W.Db.UserCompanyMemberships.Add(new UserCompanyMembership { Id = Guid.NewGuid(), UserId = foreign.Id, CompanyId = TestCompany.OtherId, Status = MembershipStatus.Active });
        s.W.Db.SaveChanges();

        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() =>
            s.W.Service.AddMemberAsync(Actor(s.Delegate), new AddGovernanceMemberInput(foreign.Id, GovernanceTitle.Director, null, "r"), default));
    }

    [Theory]
    [InlineData("suspended")]
    [InlineData("disabled")]
    public async Task AddMember_InactiveUser_IsRejectedOnUserId(string how)
    {
        var s = NewSetup();
        var target = how == "suspended" ? s.W.AddUser("t1", membership: MembershipStatus.Suspended) : s.W.AddUser("t1", status: UserStatus.Disabled);

        await FieldAsync(() => s.W.Service.AddMemberAsync(Actor(s.Delegate), new AddGovernanceMemberInput(target.Id, GovernanceTitle.Director, null, "r"), default), "userId");
    }

    [Fact]
    public async Task AddMember_AlreadyActiveMember_IsRejectedOnUserId()
    {
        var s = NewSetup();

        await FieldAsync(() => s.W.Service.AddMemberAsync(Actor(s.Delegate), new AddGovernanceMemberInput(s.Owner.Id, GovernanceTitle.Director, null, "r"), default), "userId");
    }

    [Fact]
    public async Task AddMember_PlatformAdministrator_IsPlatformAccountProtected()
    {
        var s = NewSetup();
        var ops = s.W.AddUser("ops1");
        s.W.MakePlatformAdmin(ops);

        await ForbiddenAsync(() => s.W.Service.AddMemberAsync(Actor(s.Delegate), new AddGovernanceMemberInput(ops.Id, GovernanceTitle.Director, null, "r"), default),
            GovernanceErrorCodes.PlatformAccountProtected);
    }

    [Fact]
    public async Task AddMember_AfterAnEarlierMemberEnded_CreatesAFreshActiveRow()
    {
        var s = NewSetup();
        var target = s.W.AddUser("again1");
        var first = await s.W.Service.AddMemberAsync(Actor(s.Delegate), new AddGovernanceMemberInput(target.Id, GovernanceTitle.Director, null, "first"), default);
        await s.W.Service.EndMemberAsync(Actor(s.Delegate), first, "ended", default);

        var second = await s.W.Service.AddMemberAsync(Actor(s.Delegate), new AddGovernanceMemberInput(target.Id, GovernanceTitle.BoardMember, null, "second"), default);

        Assert.NotEqual(first, second);
        Assert.Equal(2, s.W.Db.CompanyGovernanceMembers.Count(m => m.Membership.UserId == target.Id));
        Assert.Single(s.W.Db.CompanyGovernanceMembers.Where(m => m.Membership.UserId == target.Id && m.Status == GovernanceMemberStatus.Active));
    }

    [Fact]
    public async Task AddMember_InvalidTitle_IsRejectedOnTitle()
    {
        var s = NewSetup();

        await FieldAsync(() => s.W.Service.AddMemberAsync(Actor(s.Delegate), new AddGovernanceMemberInput(s.Staff.Id, (GovernanceTitle)99, null, "r"), default), "title");
    }

    // ---- reason ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EveryWrite_WithoutAReason_IsRejectedOnReason(string? reason)
    {
        var s = NewSetup();
        var target = s.W.AddUser("director2");
        var member = s.W.AddMember(target, GovernanceTitle.Director, View);

        await FieldAsync(() => s.W.Service.AddMemberAsync(Actor(s.Delegate), new AddGovernanceMemberInput(s.Staff.Id, GovernanceTitle.Director, null, reason), default), "reason");
        await FieldAsync(() => s.W.Service.ChangeTitleAsync(Actor(s.Delegate), member.Id, GovernanceTitle.BoardMember, reason, default), "reason");
        await FieldAsync(() => s.W.Service.SetPermissionsAsync(Actor(s.Delegate), member.Id, [Members], reason, default), "reason");
        await FieldAsync(() => s.W.Service.EndMemberAsync(Actor(s.Delegate), member.Id, reason, default), "reason");
        await FieldAsync(() => s.W.Service.GrantAdministratorAsync(Actor(s.Delegate), s.Staff.Id, reason, default), "reason");
        await FieldAsync(() => s.W.Service.RemoveAdministratorAsync(Actor(s.Delegate), s.Staff.Id, s.W.StaffRole.Id, reason, default), "reason");
    }

    [Fact]
    public async Task Reason_LongerThan500Characters_IsRejectedOnReason()
    {
        var s = NewSetup();

        await FieldAsync(() => s.W.Service.AddMemberAsync(Actor(s.Delegate), new AddGovernanceMemberInput(s.Staff.Id, GovernanceTitle.Director, null, new string('x', 501)), default), "reason");
        await s.W.Service.AddMemberAsync(Actor(s.Delegate), new AddGovernanceMemberInput(s.Staff.Id, GovernanceTitle.Director, null, new string('x', 500)), default);
    }

    // ---- change title ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ChangeTitle_NonOwner_UpdatesTheTitleAuditsAndBumpsTheVersion_GrantsUntouched()
    {
        var s = NewSetup();
        var target = s.W.AddUser("director2");
        var member = s.W.AddMember(target, GovernanceTitle.Director, View, Members);
        var version = s.W.AccessVersion(target);

        await s.W.Service.ChangeTitleAsync(Actor(s.Delegate), member.Id, GovernanceTitle.BoardMember, "promotion", default);

        var reloaded = s.W.Reload(member);
        Assert.Equal(GovernanceTitle.BoardMember, reloaded.Title);
        Assert.Equal(s.Delegate.Id, reloaded.UpdatedByUserId);
        Assert.Equal(new[] { Members, View }.Order(), s.W.CodesOf(member)); // titles grant nothing and move nothing
        Assert.Equal(version + 1, s.W.AccessVersion(target));
        var audit = Assert.Single(s.W.Audit(AuditActions.GovernanceMemberTitleChanged));
        Assert.Contains("Director", audit.Before);
        Assert.Contains("BoardMember", audit.After);
    }

    [Fact]
    public async Task ChangeTitle_ToTheSameTitle_IsRejectedOnTitle()
    {
        var s = NewSetup();
        var target = s.W.AddUser("director2");
        var member = s.W.AddMember(target, GovernanceTitle.Director, View);

        await FieldAsync(() => s.W.Service.ChangeTitleAsync(Actor(s.Delegate), member.Id, GovernanceTitle.Director, "r", default), "title");
    }

    [Fact]
    public async Task ChangeTitle_UnknownOrEndedMember_IsNotFound()
    {
        var s = NewSetup();
        var target = s.W.AddUser("director2");
        var member = s.W.AddMember(target, GovernanceTitle.Director, View);
        await s.W.Service.EndMemberAsync(Actor(s.Delegate), member.Id, "r", default);

        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => s.W.Service.ChangeTitleAsync(Actor(s.Delegate), Guid.NewGuid(), GovernanceTitle.Director, "r", default));
        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => s.W.Service.ChangeTitleAsync(Actor(s.Delegate), member.Id, GovernanceTitle.BoardMember, "r", default));
    }

    // ---- T8: delegated administration -----------------------------------------------------------------------------------------

    private static (GovernanceWorld W, User Owner, User Delegate, User Staff, User ExtraAdmin) AdminSetup()
    {
        var w = new GovernanceWorld();
        var (owner, _) = w.AddOwner("owner1");
        var d = w.AddUser("delegate1");
        w.AddMember(d, GovernanceTitle.DelegatedAdministrator, View, Admins);
        var staff = w.AddUser("staff1");
        var extra = w.AddUser("extraadmin", w.AdminRole);
        return (w, owner, d, staff, extra);
    }

    [Fact]
    public async Task GrantAdministrator_PromotesToTheSystemRoleCompanyWide_AuditsAndBumpsTheVersion()
    {
        var (w, _, d, staff, _) = AdminSetup();
        var version = w.AccessVersion(staff);

        await w.Service.GrantAdministratorAsync(Actor(d), staff.Id, "promote", default);

        w.Db.ChangeTracker.Clear();
        Assert.Contains(w.AdminRole.Id, await Rbac.ActiveRoleIdsAsync(w.Db, staff.Id)); // additive: the Staff assignment stays
        Assert.Equal(CompanyAccessScope.CompanyWide, w.Db.UserCompanyMemberships.Single(m => m.UserId == staff.Id).AccessScope);
        Assert.True(w.AccessVersion(staff) > version);
        var audit = Assert.Single(w.Audit(AuditActions.GovernanceAdministratorGranted));
        Assert.Equal("promote", audit.Reason);
        Assert.Equal(staff.Id, audit.SubjectUserId);
        Assert.Equal(d.Id, audit.ActorUserId);
    }

    [Fact]
    public async Task GrantAdministrator_PromotesABranchScopedUserToCompanyWide()
    {
        var (w, _, d, _, _) = AdminSetup();
        var scoped = w.AddUser("scoped1", scope: CompanyAccessScope.AssignedBranches);

        await w.Service.GrantAdministratorAsync(Actor(d), scoped.Id, "promote", default);

        w.Db.ChangeTracker.Clear();
        Assert.Equal(CompanyAccessScope.CompanyWide, w.Db.UserCompanyMemberships.Single(m => m.UserId == scoped.Id).AccessScope);
    }

    [Fact]
    public async Task GrantAdministrator_WithoutManageAdministrators_IsPermissionRequired()
    {
        var (w, owner, _, staff, _) = AdminSetup();
        var viewer = w.AddUser("viewer1");
        w.AddMember(viewer, GovernanceTitle.Director, View, Members, Owners, Assign);

        await ForbiddenAsync(() => w.Service.GrantAdministratorAsync(Actor(viewer), staff.Id, "r", default), GovernanceErrorCodes.PermissionRequired);
        _ = owner;
    }

    [Fact]
    public async Task GrantAdministrator_TargetInAnotherCompanyToo_IsForbidden()
    {
        // Consistent with UsersController's shared-account rule (403).
        var (w, _, d, staff, _) = AdminSetup();
        w.Db.Companies.Add(new Company { Id = TestCompany.OtherId, Code = "B", Name = "B", IsActive = true });
        w.Db.UserCompanyMemberships.Add(new UserCompanyMembership { Id = Guid.NewGuid(), UserId = staff.Id, CompanyId = TestCompany.OtherId, Status = MembershipStatus.Active });
        w.Db.SaveChanges();

        await ForbiddenAsync(() => w.Service.GrantAdministratorAsync(Actor(d), staff.Id, "r", default), GovernanceErrorCodes.SharedAccountProtected);
    }

    [Fact]
    public async Task GrantAdministrator_UnknownDisabledOrAlreadyAdministrator_AreRejected()
    {
        var (w, _, d, _, extra) = AdminSetup();
        var disabled = w.AddUser("disabled1", status: UserStatus.Disabled);
        var suspended = w.AddUser("suspended1", membership: MembershipStatus.Suspended);

        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => w.Service.GrantAdministratorAsync(Actor(d), Guid.NewGuid(), "r", default));
        await FieldAsync(() => w.Service.GrantAdministratorAsync(Actor(d), disabled.Id, "r", default), "userId");
        await FieldAsync(() => w.Service.GrantAdministratorAsync(Actor(d), suspended.Id, "r", default), "userId");
        await FieldAsync(() => w.Service.GrantAdministratorAsync(Actor(d), extra.Id, "r", default), "userId");
    }

    [Fact]
    public async Task GrantAdministrator_PlatformAdministratorTarget_IsPlatformAccountProtected()
    {
        var (w, _, d, _, _) = AdminSetup();
        var ops = w.AddUser("ops1");
        w.MakePlatformAdmin(ops);

        await ForbiddenAsync(() => w.Service.GrantAdministratorAsync(Actor(d), ops.Id, "r", default), GovernanceErrorCodes.PlatformAccountProtected);
    }

    [Fact]
    public async Task GrantAdministrator_GovernanceMemberTarget_NeedsManageMembers()
    {
        var (w, _, d, _, _) = AdminSetup(); // d holds View + ManageAdministrators only
        var director = w.AddUser("director2");
        w.AddMember(director, GovernanceTitle.Director, View);

        await ForbiddenAsync(() => w.Service.GrantAdministratorAsync(Actor(d), director.Id, "r", default), GovernanceErrorCodes.MemberProtected);
    }

    [Fact]
    public async Task RemoveAdministrator_MovesTheUserToTheReplacementRole_Audits_AndKeepsTheLastAdministratorGuard()
    {
        var (w, owner, d, _, extra) = AdminSetup(); // administrators: owner (Owner) and extra
        var version = w.AccessVersion(extra);

        await w.Service.RemoveAdministratorAsync(Actor(d), extra.Id, w.StaffRole.Id, "demote", default);

        w.Db.ChangeTracker.Clear();
        Assert.Equal([w.StaffRole.Id], await Rbac.ActiveRoleIdsAsync(w.Db, extra.Id));
        Assert.Equal(version + 1, w.AccessVersion(extra));
        var audit = Assert.Single(w.Audit(AuditActions.GovernanceAdministratorRemoved));
        Assert.Equal("demote", audit.Reason);
        Assert.Equal(extra.Id, audit.SubjectUserId);
        _ = owner;
    }

    [Fact]
    public async Task RemoveAdministrator_TheLastCompanyWideAdministrator_IsLastAdministratorProtected()
    {
        var w = new GovernanceWorld();
        w.AddOwner("owner1");
        var d = w.AddUser("delegate1");
        w.AddMember(d, GovernanceTitle.Director, View, Admins, Members, Owners);
        // only administrator: the owner

        var owner = w.Db.Users.Single(u => u.Username == "owner1");
        // Phase 7: the last-Administrator rule lives in RoleRules (assignments); it surfaces as a RoleRuleException with the same code.
        var ex = await Assert.ThrowsAsync<Sellora.Domain.Roles.RoleRuleException>(() => w.Service.RemoveAdministratorAsync(Actor(d), owner.Id, w.StaffRole.Id, "r", default));
        Assert.Equal("last_administrator_protected", ex.Code);
    }

    [Fact]
    public async Task RemoveAdministrator_InvalidReplacementRole_IsRejectedOnReplacementRoleId()
    {
        var (w, _, d, _, extra) = AdminSetup();
        var inactive = new Role { Id = Guid.NewGuid(), Name = "Retired", IsSystem = false, IsActive = false };
        w.Db.Roles.Add(inactive);
        w.Db.SaveChanges();

        await FieldAsync(() => w.Service.RemoveAdministratorAsync(Actor(d), extra.Id, w.AdminRole.Id, "r", default), "replacementRoleId");
        await FieldAsync(() => w.Service.RemoveAdministratorAsync(Actor(d), extra.Id, inactive.Id, "r", default), "replacementRoleId");
        await FieldAsync(() => w.Service.RemoveAdministratorAsync(Actor(d), extra.Id, Guid.NewGuid(), "r", default), "replacementRoleId");
    }

    [Fact]
    public async Task RemoveAdministrator_NonAdministratorTarget_IsRejectedOnUserId()
    {
        var (w, _, d, staff, _) = AdminSetup();

        await FieldAsync(() => w.Service.RemoveAdministratorAsync(Actor(d), staff.Id, w.StaffRole.Id, "r", default), "userId");
    }

    [Fact]
    public async Task RemoveAdministrator_FromAnOwner_NeedsManageOwners_AndOtherwiseSucceeds_KnownOpenPoint()
    {
        // Known open point: removing the Administrator role from an Owner does not touch the governance membership, so an Owner can end up
        // without the role (the service only keeps one company-wide Administrator in the company).
        var w = new GovernanceWorld();
        var (owner1, _) = w.AddOwner("owner1");
        var (owner2, _) = w.AddOwner("owner2");
        var weak = w.AddUser("weak1");
        w.AddMember(weak, GovernanceTitle.Director, View, Admins, Members); // no ManageOwners
        var strong = w.AddUser("strong1");
        w.AddMember(strong, GovernanceTitle.Director, View, Admins, Members, Owners);

        await ForbiddenAsync(() => w.Service.RemoveAdministratorAsync(Actor(weak), owner1.Id, w.StaffRole.Id, "r", default), GovernanceErrorCodes.MemberProtected);
        await w.Service.RemoveAdministratorAsync(Actor(strong), owner1.Id, w.StaffRole.Id, "r", default);

        w.Db.ChangeTracker.Clear();
        Assert.Equal([w.StaffRole.Id], await Rbac.ActiveRoleIdsAsync(w.Db, owner1.Id));
        Assert.Equal(GovernanceMemberStatus.Active, w.Db.CompanyGovernanceMembers.Single(m => m.Membership.UserId == owner1.Id).Status);
        _ = owner2;
    }

    [Fact]
    public async Task RemoveAdministrator_TargetInAnotherCompanyToo_IsForbidden()
    {
        var (w, _, d, _, extra) = AdminSetup();
        w.Db.Companies.Add(new Company { Id = TestCompany.OtherId, Code = "B", Name = "B", IsActive = true });
        w.Db.UserCompanyMemberships.Add(new UserCompanyMembership { Id = Guid.NewGuid(), UserId = extra.Id, CompanyId = TestCompany.OtherId, Status = MembershipStatus.Active });
        w.Db.SaveChanges();

        await ForbiddenAsync(() => w.Service.RemoveAdministratorAsync(Actor(d), extra.Id, w.StaffRole.Id, "r", default), GovernanceErrorCodes.SharedAccountProtected);
    }

    [Fact]
    public async Task RemoveAdministrator_PlatformAdministratorTarget_IsPlatformAccountProtected()
    {
        var (w, _, d, _, _) = AdminSetup();
        var ops = w.AddUser("ops1", w.AdminRole);
        w.MakePlatformAdmin(ops);

        await ForbiddenAsync(() => w.Service.RemoveAdministratorAsync(Actor(d), ops.Id, w.StaffRole.Id, "r", default), GovernanceErrorCodes.PlatformAccountProtected);
    }

    // ---- R5 on the existing user/membership operations ------------------------------------------------------------------------

    [Fact]
    public async Task UserAccess_RevokingAGovernanceMembersMembership_EndsTheMemberRemovesGrantsAndAudits()
    {
        var s = NewSetup();
        var target = s.W.AddUser("director2");
        var member = s.W.AddMember(target, GovernanceTitle.Director, View, Members);
        var actor = new AccessActor(s.Delegate.Id, true, true, "c1");

        await s.W.Access.SetStatusAsync(actor, target.Id, new SetMembershipStatusInput(MembershipStatus.Revoked, "left", null, null, null), default);

        var ended = s.W.Reload(member);
        Assert.Equal(GovernanceMemberStatus.Ended, ended.Status);
        Assert.Equal("left", ended.EndReason);
        Assert.Empty(s.W.CodesOf(member));
        var audit = Assert.Single(s.W.Audit(AuditActions.GovernanceMemberEnded));
        Assert.Equal(member.Id, audit.TargetId);
    }

    [Fact]
    public async Task UserAccess_SuspendingAGovernanceMembersMembership_KeepsTheMemberButHeldCodesDropToNothing()
    {
        var s = NewSetup();
        var target = s.W.AddUser("director2");
        var member = s.W.AddMember(target, GovernanceTitle.Director, View);
        var actor = new AccessActor(s.Delegate.Id, true, true);

        await s.W.Access.SetStatusAsync(actor, target.Id, new SetMembershipStatusInput(MembershipStatus.Suspended, "pause", null, null, null), default);

        Assert.Equal(GovernanceMemberStatus.Active, s.W.Reload(member).Status);
        Assert.Empty(await Sellora.Infrastructure.Services.GovernanceRules.HeldCodesAsync(s.W.Db, target.Id, default));
    }

    [Theory]
    [InlineData(MembershipStatus.Suspended)]
    [InlineData(MembershipStatus.Revoked)]
    public async Task UserAccess_SuspendingOrRevokingTheLastRootOwner_IsLastOwnerProtected(MembershipStatus status)
    {
        var s = NewSetup();
        var actor = new AccessActor(s.Delegate.Id, true, true);

        await RuleAsync(() => s.W.Access.SetStatusAsync(actor, s.Owner.Id, new SetMembershipStatusInput(status, "r", null, null, null), default), GovernanceErrorCodes.LastOwnerProtected);
    }

    [Fact]
    public async Task UserAccess_ChangingAGovernanceMember_WithoutManageMembers_IsMemberProtected_AndOwnerNeedsManageOwners()
    {
        var w = new GovernanceWorld();
        var (owner, _) = w.AddOwner("owner1");
        var admin = w.AddUser("admin2", w.AdminRole); // administrator, no governance
        var withMembers = w.AddUser("members1", w.AdminRole);
        w.AddMember(withMembers, GovernanceTitle.Director, View, Members);
        var director = w.AddUser("director2");
        w.AddMember(director, GovernanceTitle.Director, View);

        await ForbiddenAsync(() => w.Access.SetStatusAsync(new AccessActor(admin.Id, true, true), director.Id,
            new SetMembershipStatusInput(MembershipStatus.Suspended, "r", null, null, null), default), GovernanceErrorCodes.MemberProtected);
        await ForbiddenAsync(() => w.Access.SetStatusAsync(new AccessActor(withMembers.Id, true, true), owner.Id,
            new SetMembershipStatusInput(MembershipStatus.Suspended, "r", null, null, null), default), GovernanceErrorCodes.MemberProtected); // Owner target: ManageOwners
        await w.Access.SetStatusAsync(new AccessActor(withMembers.Id, true, true), director.Id,
            new SetMembershipStatusInput(MembershipStatus.Suspended, "r", null, null, null), default); // ManageMembers suffices for a non-owner
    }

    [Fact]
    public async Task UserAccess_ChangingAPlatformAdministrator_IsPlatformAccountProtected_EvenForAnOwnerWithEverything()
    {
        var s = NewSetup();
        var ops = s.W.AddUser("ops1");
        s.W.MakePlatformAdmin(ops);
        var actor = new AccessActor(s.Owner.Id, true, true);

        await ForbiddenAsync(() => s.W.Access.SetStatusAsync(actor, ops.Id, new SetMembershipStatusInput(MembershipStatus.Suspended, "r", null, null, null), default),
            GovernanceErrorCodes.PlatformAccountProtected);
    }

    [Fact]
    public async Task UserAccess_RevokedPlatformAdministrator_IsNoLongerProtected()
    {
        var s = NewSetup();
        var ex = s.W.AddUser("ex1");
        s.W.MakePlatformAdmin(ex, PlatformAdminStatus.Revoked);
        var actor = new AccessActor(s.Owner.Id, true, true);

        await s.W.Access.SetStatusAsync(actor, ex.Id, new SetMembershipStatusInput(MembershipStatus.Suspended, "r", null, null, null), default);
    }
}
