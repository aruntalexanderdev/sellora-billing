using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;
using static Sellora.Tests.TestData.GovernanceWorld;

namespace Sellora.Tests.Governance;

/// <summary>
/// Phase 6 T7 / R5 through <see cref="UsersController"/> (edit, status, reset-password): platform administrators are never changeable from a company
/// session; governance members only by an actor holding ManageMembers (Owner target: ManageOwners); the ordinary rules stay in place for everyone else.
/// R4 on user disable returns the 400 <c>last_owner_protected</c> problem.
/// </summary>
public class UserAdministrationProtectionTests
{
    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => "hash:" + password;
        public bool Verify(string password, string hash) => hash == "hash:" + password;
    }

    private static UsersController Users(GovernanceWorld w, User actor)
    {
        w.Db.ChangeTracker.Clear();
        return new UsersController(w.Db, new FakeHasher(), new UserAccessService(w.Db), new RoleAssignmentService(w.Db, new UserAccessService(w.Db)),
            Rbac.Effective(w.Db, Rbac.ContextOf(w.Db, actor.Id)))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString())], "test")),
                },
            },
        };
    }

    private static UpdateUserRequest Edit(User user, string firstName = "Changed") =>
        new(firstName, user.LastName, user.Username, user.Email, null, user.RoleId);

    private static string CodeOf(IActionResult? result, int status)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode);
        return (string)Assert.IsType<ProblemDetails>(obj.Value).Extensions["code"]!;
    }

    private static async Task<IActionResult?> UpdateAsync(GovernanceWorld w, User actor, User target) => (await Users(w, actor).Update(target.Id, Edit(target), default)).Result;

    /// <summary>Administrator actor "ops" without governance, an Owner, a Director and a platform administrator, all administrators or staff in the same company.</summary>
    private sealed record Setup(GovernanceWorld W, User Ops, User Owner, User Director, User PlatformAdmin, User Plain, User ManagerOfMembers, User ManagerOfOwners);

    private static Setup NewSetup()
    {
        var w = new GovernanceWorld();
        var ops = w.AddUser("ops1", w.AdminRole);
        var (owner, _) = w.AddOwner("owner1");
        var director = w.AddUser("director1");
        w.AddMember(director, GovernanceTitle.Director, View);
        var platform = w.AddUser("platform1", w.AdminRole);
        w.MakePlatformAdmin(platform);
        var plain = w.AddUser("plain1");
        var members = w.AddUser("members1", w.AdminRole);
        w.AddMember(members, GovernanceTitle.DelegatedAdministrator, View, Members);
        var owners = w.AddUser("owners1", w.AdminRole);
        w.AddMember(owners, GovernanceTitle.Director, View, Members, Owners);
        return new Setup(w, ops, owner, director, platform, plain, members, owners);
    }

    // ---- platform administrators ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_PlatformAdministratorTarget_IsPlatformAccountProtected_EvenForAnOwnerActor()
    {
        var s = NewSetup();

        Assert.Equal(GovernanceErrorCodes.PlatformAccountProtected, CodeOf(await UpdateAsync(s.W, s.Ops, s.PlatformAdmin), 403));
        Assert.Equal(GovernanceErrorCodes.PlatformAccountProtected, CodeOf(await UpdateAsync(s.W, s.Owner, s.PlatformAdmin), 403));
    }

    [Fact]
    public async Task SetStatus_PlatformAdministratorTarget_IsPlatformAccountProtected_AndNothingChanges()
    {
        var s = NewSetup();

        var result = await Users(s.W, s.Ops).SetStatus(s.PlatformAdmin.Id, new SetUserStatusRequest(UserStatus.Disabled), default);

        Assert.Equal(GovernanceErrorCodes.PlatformAccountProtected, CodeOf(result, 403));
        s.W.Db.ChangeTracker.Clear();
        Assert.Equal(UserStatus.Active, s.W.Db.Users.Single(u => u.Id == s.PlatformAdmin.Id).Status);
    }

    [Fact]
    public async Task ResetPassword_PlatformAdministratorTarget_IsPlatformAccountProtected_AndThePasswordIsUntouched()
    {
        var s = NewSetup();

        var result = await Users(s.W, s.Owner).ResetPassword(s.PlatformAdmin.Id, default);

        Assert.Equal(GovernanceErrorCodes.PlatformAccountProtected, CodeOf(result.Result, 403));
        s.W.Db.ChangeTracker.Clear();
        Assert.Equal("x", s.W.Db.Users.Single(u => u.Id == s.PlatformAdmin.Id).PasswordHash);
    }

    [Fact]
    public async Task Update_RevokedPlatformAdministrator_IsAnOrdinaryUserAgain()
    {
        var s = NewSetup();
        var ex = s.W.AddUser("ex1");
        s.W.MakePlatformAdmin(ex, PlatformAdminStatus.Revoked);

        var result = await UpdateAsync(s.W, s.Ops, ex);

        Assert.IsType<OkObjectResult>(result);
    }

    // ---- governance members ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_GovernanceMemberTarget_WithoutManageMembers_IsMemberProtected()
    {
        var s = NewSetup();

        Assert.Equal(GovernanceErrorCodes.MemberProtected, CodeOf(await UpdateAsync(s.W, s.Ops, s.Director), 403));
        Assert.Equal(GovernanceErrorCodes.MemberProtected, CodeOf(await UpdateAsync(s.W, s.Ops, s.Owner), 403));
    }

    [Fact]
    public async Task Update_GovernanceMemberTarget_WithManageMembers_Succeeds_ButAnOwnerNeedsManageOwners()
    {
        var s = NewSetup();

        Assert.IsType<OkObjectResult>(await UpdateAsync(s.W, s.ManagerOfMembers, s.Director));
        Assert.Equal(GovernanceErrorCodes.MemberProtected, CodeOf(await UpdateAsync(s.W, s.ManagerOfMembers, s.Owner), 403));
        Assert.IsType<OkObjectResult>(await UpdateAsync(s.W, s.ManagerOfOwners, s.Owner));
    }

    [Fact]
    public async Task SetStatus_GovernanceMemberTarget_FollowsTheSameRule()
    {
        var s = NewSetup();

        Assert.Equal(GovernanceErrorCodes.MemberProtected, CodeOf(await Users(s.W, s.Ops).SetStatus(s.Director.Id, new SetUserStatusRequest(UserStatus.Disabled), default), 403));
        Assert.Equal(GovernanceErrorCodes.MemberProtected, CodeOf(await Users(s.W, s.ManagerOfMembers).SetStatus(s.Owner.Id, new SetUserStatusRequest(UserStatus.Disabled), default), 403));
        Assert.IsType<NoContentResult>(await Users(s.W, s.ManagerOfMembers).SetStatus(s.Director.Id, new SetUserStatusRequest(UserStatus.Disabled), default));
        s.W.Db.ChangeTracker.Clear();
        Assert.Equal(UserStatus.Disabled, s.W.Db.Users.Single(u => u.Id == s.Director.Id).Status);
    }

    [Fact]
    public async Task ResetPassword_GovernanceMemberTarget_FollowsTheSameRule_AndOrdinaryUsersAreUnaffected()
    {
        var s = NewSetup();

        Assert.Equal(GovernanceErrorCodes.MemberProtected, CodeOf((await Users(s.W, s.Ops).ResetPassword(s.Director.Id, default)).Result, 403));
        Assert.Equal(GovernanceErrorCodes.MemberProtected, CodeOf((await Users(s.W, s.Ops).ResetPassword(s.Owner.Id, default)).Result, 403));
        Assert.IsType<OkObjectResult>((await Users(s.W, s.ManagerOfOwners).ResetPassword(s.Owner.Id, default)).Result);
        Assert.IsType<OkObjectResult>((await Users(s.W, s.Ops).ResetPassword(s.Plain.Id, default)).Result);
    }

    [Fact]
    public async Task Update_OrdinaryUser_IsNotAffectedByGovernance()
    {
        var s = NewSetup();

        Assert.IsType<OkObjectResult>(await UpdateAsync(s.W, s.Ops, s.Plain));
    }

    [Fact]
    public async Task Update_EndedGovernanceMember_IsNoLongerProtected()
    {
        var s = NewSetup();
        var tracked = s.W.Db.CompanyGovernanceMembers.Single(m => m.Membership.UserId == s.Director.Id);
        tracked.Status = GovernanceMemberStatus.Ended;
        tracked.EndAt = DateTimeOffset.UtcNow;
        s.W.Db.SaveChanges();

        Assert.IsType<OkObjectResult>(await UpdateAsync(s.W, s.Ops, s.Director));
    }

    // ---- R4 on user disable ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetStatus_DisablingTheLastRootOwner_Is400LastOwnerProtected()
    {
        var w = new GovernanceWorld();
        var (owner, _) = w.AddOwner("owner1");
        var actor = w.AddUser("owners1", w.AdminRole);
        w.AddMember(actor, GovernanceTitle.Director, View, Members, Owners);

        var result = await Users(w, actor).SetStatus(owner.Id, new SetUserStatusRequest(UserStatus.Disabled), default);

        Assert.Equal(GovernanceErrorCodes.LastOwnerProtected, CodeOf(result, 400));
    }

    [Fact]
    public async Task SetStatus_DisablingAnOwnerWhenAnotherRootOwnerRemains_Succeeds()
    {
        var w = new GovernanceWorld();
        var (owner, _) = w.AddOwner("owner1");
        w.AddOwner("owner2");
        var actor = w.AddUser("owners1", w.AdminRole);
        w.AddMember(actor, GovernanceTitle.Director, View, Members, Owners);

        var result = await Users(w, actor).SetStatus(owner.Id, new SetUserStatusRequest(UserStatus.Disabled), default);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Update_DemotingAnOwnerFromTheAdministratorRole_ByAManageOwnersActor_Succeeds_KnownOpenPoint()
    {
        // Known open point: the Administrator role is not part of the root-owner definition, so an Owner can lose the role.
        var w = new GovernanceWorld();
        var (owner, _) = w.AddOwner("owner1");
        var actor = w.AddUser("owners1", w.AdminRole);
        w.AddMember(actor, GovernanceTitle.Director, View, Members, Owners);

        var result = await Users(w, actor).Update(owner.Id, new UpdateUserRequest(owner.FirstName, owner.LastName, owner.Username, owner.Email, null, w.StaffRole.Id), default);

        Assert.IsType<OkObjectResult>(result.Result);
    }
}
