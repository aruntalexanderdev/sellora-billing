using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Roles;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Roles;

/// <summary>
/// Phase 7 D9 defence in depth: the last active company-wide Administrator cannot be suspended, revoked (membership status) or disabled (user status),
/// even by an Administrator who does not count towards the remaining set. The user-disable rule is the same helper the users endpoint applies.
/// </summary>
public class LastAdministratorGuardTests
{
    // A company-wide member who is flagged Administrator but holds no Administrator assignment, so it never counts as the "remaining" administrator.
    private static AccessActor OutsiderOf(RbacWorld w)
    {
        var (user, _) = w.CompanyWideWith("outsider", w.CompanyRole("Plain", "Sales.View"));
        return new AccessActor(user.Id, true, true, "test");
    }

    private static void AssertLastAdministrator(RoleRuleException ex) => Assert.Equal(RoleErrorCodes.LastAdministratorProtected, ex.Code);

    [Theory]
    [InlineData(MembershipStatus.Suspended)]
    [InlineData(MembershipStatus.Revoked)]
    public async Task SetStatusAsync_TheOnlyAdministrator_IsRefused(MembershipStatus status)
    {
        var w = new RbacWorld();
        var (user, _) = w.CompanyWideWith("solo", w.Administrator);
        w.Db.ChangeTracker.Clear();

        var ex = await Assert.ThrowsAsync<RoleRuleException>(() =>
            new UserAccessService(w.Db).SetStatusAsync(OutsiderOf(w), user.Id, new SetMembershipStatusInput(status, "leaving", null, null, null)));

        AssertLastAdministrator(ex);
    }

    [Theory]
    [InlineData(MembershipStatus.Suspended)]
    [InlineData(MembershipStatus.Revoked)]
    public async Task SetStatusAsync_AnAdministratorWhileAnotherRemains_IsAllowed(MembershipStatus status)
    {
        var w = new RbacWorld();
        var (first, _) = w.CompanyWideWith("first", w.Administrator);
        w.CompanyWideWith("second", w.Administrator);
        w.Db.ChangeTracker.Clear();

        await new UserAccessService(w.Db).SetStatusAsync(OutsiderOf(w), first.Id, new SetMembershipStatusInput(status, "leaving", null, null, null));

        w.Db.ChangeTracker.Clear();
        Assert.Equal(status, w.Db.UserCompanyMemberships.Single(m => m.UserId == first.Id).Status);
    }

    [Fact]
    public async Task SetStatusAsync_SuspendingANonAdministrator_NeverTouchesTheGuard()
    {
        var w = new RbacWorld();
        var cashier = w.CompanyRole("Cashier", "Sales.View");
        var (user, _) = w.CompanyWideWith("cashier", cashier);
        w.Db.ChangeTracker.Clear();

        await new UserAccessService(w.Db).SetStatusAsync(OutsiderOf(w), user.Id, new SetMembershipStatusInput(MembershipStatus.Suspended, "break", null, null, null));

        w.Db.ChangeTracker.Clear();
        Assert.Equal(MembershipStatus.Suspended, w.Db.UserCompanyMemberships.Single(m => m.UserId == user.Id).Status);
    }

    [Fact]
    public async Task EnsureAdministratorRemainsAsync_AfterDisablingTheOnlyAdministrator_Throws()
    {
        var w = new RbacWorld();
        var (user, _) = w.CompanyWideWith("solo", w.Administrator);
        var tracked = w.Db.Users.Single(u => u.Id == user.Id);
        tracked.Status = UserStatus.Disabled;
        await w.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<RoleRuleException>(() => RoleRules.EnsureAdministratorRemainsAsync(w.Db, default));

        AssertLastAdministrator(ex);
    }

    [Fact]
    public async Task EnsureAdministratorRemainsAsync_AfterDisablingOneOfTwoAdministrators_Passes()
    {
        var w = new RbacWorld();
        var (first, _) = w.CompanyWideWith("first", w.Administrator);
        w.CompanyWideWith("second", w.Administrator);
        w.Db.Users.Single(u => u.Id == first.Id).Status = UserStatus.Disabled;
        await w.Db.SaveChangesAsync();

        await RoleRules.EnsureAdministratorRemainsAsync(w.Db, default);
    }
}
