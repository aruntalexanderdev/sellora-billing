using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Infrastructure.Data;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Api;

/// <summary>
/// Phase H privilege-escalation rules for user and role management (arch §7.6), run against
/// <see cref="UsersController"/> / <see cref="RolesController"/> on EF InMemory:
/// H1 only Administrators assign the Administrator role or touch Administrator accounts; H2 nobody changes their own
/// role (so the last active Administrator cannot be removed); H3 nobody grants a permission they do not hold
/// (create, edit, duplicate); H4 a role with active users cannot be deactivated and an inactive role grants nothing.
/// </summary>
public class AccessManagementRulesTests
{
    private const string ValidPassword = "Str0ng!Passw0rd";

    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => "hash:" + password;
        public bool Verify(string password, string hash) => hash == "hash:" + password;
    }

    private sealed record Fixture(SelloraDbContext Db, Role Admin, Role Manager, Role Cashier, User AdminUser, User ManagerUser, User CashierUser);

    /// <summary>Administrator (system), User Manager (user/role admin + sales view) and Cashier (sales) roles, one user each.</summary>
    private static async Task<Fixture> SeedAsync()
    {
        var db = TestDb.NewInMemory();
        var permissions = PermissionCatalog.All
            .Select(p => new Permission { Id = Guid.NewGuid(), Code = p.Code, Module = p.Module, Action = p.Action, Description = p.Description })
            .ToDictionary(p => p.Code);
        db.Permissions.AddRange(permissions.Values);

        var admin = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var manager = RoleWith("User Manager", permissions,
            "Users.View", "Users.Create", "Users.Edit", "Users.Disable", "Users.ResetPassword",
            "Roles.View", "Roles.Create", "Roles.Edit", "Roles.Duplicate", "Roles.Disable", "Sales.View");
        var cashier = RoleWith("Cashier", permissions, "Sales.View", "Sales.Create");

        var adminUser = UserIn(admin, "admin");
        var managerUser = UserIn(manager, "manager");
        var cashierUser = UserIn(cashier, "cashier");
        db.AddRange(admin, manager, cashier, adminUser, managerUser, cashierUser);
        db.UserCompanyMemberships.AddRange(new[] { adminUser, managerUser, cashierUser }.Select(u => TestCompany.MembershipFor(u)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Fixture(db, admin, manager, cashier, adminUser, managerUser, cashierUser);
    }

    private static Role RoleWith(string name, IReadOnlyDictionary<string, Permission> permissions, params string[] codes)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = name };
        foreach (var code in codes)
        {
            role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permissions[code].Id });
        }
        return role;
    }

    private static User UserIn(Role role, string username) => new()
    {
        Id = Guid.NewGuid(), FirstName = username, LastName = "Test", Username = username, Email = $"{username}@selloramart.in",
        PasswordHash = "x", RoleId = role.Id, Status = UserStatus.Active,
    };

    private static ControllerContext As(User actor) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString())], "test")),
        },
    };

    private static UsersController Users(Fixture f, User actor)
    {
        f.Db.ChangeTracker.Clear();
        return new UsersController(f.Db, new FakeHasher(), new Sellora.Infrastructure.Services.UserAccessService(f.Db)) { ControllerContext = As(actor) };
    }

    private static RolesController Roles(Fixture f, User actor)
    {
        f.Db.ChangeTracker.Clear();
        return new RolesController(f.Db) { ControllerContext = As(actor) };
    }

    private static CreateUserRequest NewUser(Guid roleId) =>
        new("Anu", "Varghese", "anu", "anu@selloramart.in", null, ValidPassword, ValidPassword, roleId);

    private static UpdateUserRequest Edit(User user, Guid roleId) =>
        new(user.FirstName, user.LastName, user.Username, user.Email, null, roleId);

    private static string FieldError(IActionResult? result, string field)
    {
        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        return Assert.Single(problem.Errors[field]);
    }

    private static string Title(IActionResult? result, int status)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode);
        return Assert.IsType<ProblemDetails>(obj.Value).Title!;
    }

    private static async Task SetRoleActiveDirectlyAsync(Fixture f, Role role, bool active)
    {
        var tracked = await f.Db.Roles.SingleAsync(r => r.Id == role.Id);
        tracked.IsActive = active;
        await f.Db.SaveChangesAsync();
    }

    // ---- H1: Administrator role and accounts ----

    [Fact]
    public async Task CreateUser_NonAdministratorAssigningAdministratorRole_IsRejectedOnRoleId()
    {
        var f = await SeedAsync();

        var result = await Users(f, f.ManagerUser).Create(NewUser(f.Admin.Id), default);

        Assert.Equal("Only Administrators can assign the Administrator role.", FieldError(result.Result, "RoleId"));
        Assert.False(await f.Db.Users.AnyAsync(u => u.Username == "anu"));
    }

    [Fact]
    public async Task CreateUser_AdministratorAssigningAdministratorRole_Succeeds()
    {
        var f = await SeedAsync();

        var result = await Users(f, f.AdminUser).Create(NewUser(f.Admin.Id), default);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(f.Admin.Id, (await f.Db.Users.SingleAsync(u => u.Username == "anu")).RoleId);
    }

    [Fact]
    public async Task UpdateUser_NonAdministratorPromotingSomeoneToAdministrator_IsRejected()
    {
        var f = await SeedAsync();

        var result = await Users(f, f.ManagerUser).Update(f.CashierUser.Id, Edit(f.CashierUser, f.Admin.Id), default);

        Assert.Equal("Only Administrators can assign the Administrator role.", FieldError(result.Result, "RoleId"));
        Assert.Equal(f.Cashier.Id, (await f.Db.Users.SingleAsync(u => u.Id == f.CashierUser.Id)).RoleId);
    }

    [Fact]
    public async Task NonAdministrator_CannotEditDisableOrResetAnAdministratorAccount()
    {
        var f = await SeedAsync();
        const string forbidden = "Only Administrators can change Administrator accounts.";

        var edit = await Users(f, f.ManagerUser).Update(f.AdminUser.Id, Edit(f.AdminUser, f.Admin.Id) with { Email = "taken@evil.test" }, default);
        var disable = await Users(f, f.ManagerUser).SetStatus(f.AdminUser.Id, new SetUserStatusRequest(UserStatus.Disabled), default);
        var reset = await Users(f, f.ManagerUser).ResetPassword(f.AdminUser.Id, default);

        Assert.Equal(forbidden, Title(edit.Result, 403));
        Assert.Equal(forbidden, Title(disable, 403));
        Assert.Equal(forbidden, Title(reset.Result, 403));
        var admin = await f.Db.Users.AsNoTracking().SingleAsync(u => u.Id == f.AdminUser.Id);
        Assert.Equal((UserStatus.Active, "admin@selloramart.in", "x"), (admin.Status, admin.Email, admin.PasswordHash));
    }

    [Fact]
    public async Task Administrator_CanManageAnotherAdministratorAccount()
    {
        var f = await SeedAsync();
        var second = UserIn(f.Admin, "admin2");
        f.Db.Users.Add(second);
        f.Db.UserCompanyMemberships.Add(TestCompany.MembershipFor(second));
        await f.Db.SaveChangesAsync();

        var disable = await Users(f, f.AdminUser).SetStatus(second.Id, new SetUserStatusRequest(UserStatus.Disabled), default);
        var demote = await Users(f, f.AdminUser).Update(second.Id, Edit(second, f.Cashier.Id), default);

        Assert.IsType<NoContentResult>(disable);
        Assert.IsType<OkObjectResult>(demote.Result);
    }

    [Fact]
    public async Task NonAdministrator_CanStillManageOrdinaryUsers()
    {
        var f = await SeedAsync();

        var reset = await Users(f, f.ManagerUser).ResetPassword(f.CashierUser.Id, default);
        var disable = await Users(f, f.ManagerUser).SetStatus(f.CashierUser.Id, new SetUserStatusRequest(UserStatus.Disabled), default);

        Assert.IsType<OkObjectResult>(reset.Result);
        Assert.IsType<NoContentResult>(disable);
    }

    [Fact]
    public async Task AssigningARoleWithPermissionsTheCallerDoesNotHold_IsRejected_OnCreateAndUpdate()
    {
        var f = await SeedAsync();
        // Cashier holds Sales.Create, which the User Manager does not: assigning it would grant that to the new account.
        var create = await Users(f, f.ManagerUser).Create(NewUser(f.Cashier.Id), default);
        var target = UserIn(f.Manager, "colleague");
        f.Db.Users.Add(target);
        f.Db.UserCompanyMemberships.Add(TestCompany.MembershipFor(target));
        await f.Db.SaveChangesAsync();
        var update = await Users(f, f.ManagerUser).Update(target.Id, Edit(target, f.Cashier.Id), default);

        const string expected = "You can only assign a role whose permissions you hold yourself. Not held: Sales.Create.";
        Assert.Equal(expected, FieldError(create.Result, "RoleId"));
        Assert.Equal(expected, FieldError(update.Result, "RoleId"));
        Assert.False(await f.Db.Users.AnyAsync(u => u.Username == "anu"));
    }

    [Fact]
    public async Task AssigningARoleWithinTheCallersPermissions_Succeeds()
    {
        var f = await SeedAsync();
        var viewer = new Role { Id = Guid.NewGuid(), Name = "Viewer" };
        viewer.RolePermissions.Add(new RolePermission { RoleId = viewer.Id, PermissionId = (await f.Db.Permissions.SingleAsync(p => p.Code == "Sales.View")).Id });
        f.Db.Roles.Add(viewer);
        await f.Db.SaveChangesAsync();

        var result = await Users(f, f.ManagerUser).Create(NewUser(viewer.Id), default);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task DisabledCaller_HoldsNothing_EvenAsAnAdministratorWithAStillValidToken()
    {
        var f = await SeedAsync();
        var second = UserIn(f.Admin, "admin2");
        second.Status = UserStatus.Disabled;
        f.Db.Users.Add(second);
        f.Db.UserCompanyMemberships.Add(TestCompany.MembershipFor(second));
        await f.Db.SaveChangesAsync();

        var demote = await Users(f, second).Update(f.AdminUser.Id, Edit(f.AdminUser, f.Cashier.Id), default);
        var role = await Roles(f, second).Create(new CreateRoleRequest("Viewer", null, ["Sales.View"]), default);

        Assert.Equal("Only Administrators can change Administrator accounts.", Title(demote.Result, 403));
        Assert.Equal("You can only grant permissions you hold yourself. Not held: Sales.View.", FieldError(role.Result, "PermissionCodes"));
    }

    [Fact]
    public async Task UserWhoAlsoBelongsToAnotherCompany_CannotBeEditedDisabledOrReset_FromThisCompany()
    {
        var f = await SeedAsync();
        f.Db.UserCompanyMemberships.Add(TestCompany.MembershipFor(f.CashierUser, TestCompany.OtherId));
        await f.Db.SaveChangesAsync();
        const string shared = "This user also belongs to another company and cannot be changed from here.";

        var edit = await Users(f, f.AdminUser).Update(f.CashierUser.Id, Edit(f.CashierUser, f.Cashier.Id) with { Email = "x@evil.test" }, default);
        var disable = await Users(f, f.AdminUser).SetStatus(f.CashierUser.Id, new SetUserStatusRequest(UserStatus.Disabled), default);
        var reset = await Users(f, f.AdminUser).ResetPassword(f.CashierUser.Id, default);

        Assert.Equal(shared, Title(edit.Result, 403));
        Assert.Equal(shared, Title(disable, 403));
        Assert.Equal(shared, Title(reset.Result, 403));
        var cashier = await f.Db.Users.AsNoTracking().SingleAsync(u => u.Id == f.CashierUser.Id);
        Assert.Equal((UserStatus.Active, "cashier@selloramart.in", "x"), (cashier.Status, cashier.Email, cashier.PasswordHash));
    }

    // ---- H2: own role / last Administrator ----

    [Fact]
    public async Task UpdateUser_ChangingOwnRole_IsRejected()
    {
        var f = await SeedAsync();

        var result = await Users(f, f.ManagerUser).Update(f.ManagerUser.Id, Edit(f.ManagerUser, f.Cashier.Id), default);

        Assert.Equal("You cannot change your own role.", FieldError(result.Result, "RoleId"));
    }

    [Fact]
    public async Task UpdateUser_EditingOwnDetailsWithoutRoleChange_Succeeds()
    {
        var f = await SeedAsync();

        var result = await Users(f, f.ManagerUser).Update(f.ManagerUser.Id, Edit(f.ManagerUser, f.Manager.Id) with { FirstName = "Renamed" }, default);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task SoleAdministrator_CannotDemoteOrDisableThemselves()
    {
        var f = await SeedAsync();

        var demote = await Users(f, f.AdminUser).Update(f.AdminUser.Id, Edit(f.AdminUser, f.Cashier.Id), default);
        var disable = await Users(f, f.AdminUser).SetStatus(f.AdminUser.Id, new SetUserStatusRequest(UserStatus.Disabled), default);

        Assert.Equal("You cannot change your own role.", FieldError(demote.Result, "RoleId"));
        Assert.Equal("You cannot disable your own account.", Title(disable, 400));
        var admin = await f.Db.Users.AsNoTracking().SingleAsync(u => u.Id == f.AdminUser.Id);
        Assert.Equal((f.Admin.Id, UserStatus.Active), (admin.RoleId, admin.Status));
    }

    // ---- Inactive roles cannot be assigned ----

    [Fact]
    public async Task AssigningAnInactiveRole_IsRejected_ButKeepingAnUnchangedInactiveRoleIsAllowed()
    {
        var f = await SeedAsync();
        var legacy = new Role { Id = Guid.NewGuid(), Name = "Legacy", IsActive = false };
        var legacyUser = UserIn(legacy, "legacy");
        f.Db.AddRange(legacy, legacyUser);
        f.Db.UserCompanyMemberships.Add(TestCompany.MembershipFor(legacyUser));
        await f.Db.SaveChangesAsync();

        var assign = await Users(f, f.AdminUser).Update(f.CashierUser.Id, Edit(f.CashierUser, legacy.Id), default);
        var keep = await Users(f, f.AdminUser).Update(legacyUser.Id, Edit(legacyUser, legacy.Id) with { FirstName = "Kept" }, default);

        Assert.Equal("This role is inactive. Activate it or choose another role.", FieldError(assign.Result, "RoleId"));
        Assert.IsType<OkObjectResult>(keep.Result);
    }

    // ---- H3: grant only what you hold ----

    [Fact]
    public async Task CreateRole_WithPermissionsTheCallerDoesNotHold_IsRejectedAndListsThem()
    {
        var f = await SeedAsync();

        var result = await Roles(f, f.ManagerUser).Create(new CreateRoleRequest("Auditor", null, ["Sales.View", "Reports.Profit", "Inventory.Adjust"]), default);

        Assert.Equal("You can only grant permissions you hold yourself. Not held: Inventory.Adjust, Reports.Profit.",
            FieldError(result.Result, "PermissionCodes"));
        Assert.False(await f.Db.Roles.AnyAsync(r => r.Name == "Auditor"));
    }

    [Fact]
    public async Task CreateRole_WithHeldPermissions_Succeeds_AndAdministratorMayGrantAnything()
    {
        var f = await SeedAsync();

        var byManager = await Roles(f, f.ManagerUser).Create(new CreateRoleRequest("Viewer", null, ["Sales.View"]), default);
        var byAdmin = await Roles(f, f.AdminUser).Create(new CreateRoleRequest("Auditor", null, ["Reports.Profit"]), default);

        Assert.IsType<CreatedAtActionResult>(byManager.Result);
        Assert.IsType<CreatedAtActionResult>(byAdmin.Result);
    }

    [Fact]
    public async Task UpdateRole_CallerCannotAddUnheldPermissions_IncludingToTheirOwnRole()
    {
        var f = await SeedAsync();
        var current = await f.Db.RolePermissions.Where(rp => rp.RoleId == f.Manager.Id).Select(rp => rp.Permission.Code).ToArrayAsync();

        var result = await Roles(f, f.ManagerUser).Update(f.Manager.Id, new UpdateRoleRequest("User Manager", null, [.. current, "Purchase.Cancel"]), default);

        Assert.Equal("You can only grant permissions you hold yourself. Not held: Purchase.Cancel.", FieldError(result.Result, "PermissionCodes"));
        Assert.DoesNotContain("Purchase.Cancel", await f.Db.RolePermissions.Where(rp => rp.RoleId == f.Manager.Id).Select(rp => rp.Permission.Code).ToListAsync());
    }

    [Fact]
    public async Task UpdateRole_KeepingOrRemovingPermissionsTheCallerDoesNotHold_IsAllowed()
    {
        var f = await SeedAsync();
        // Cashier holds Sales.Create, which the manager does not hold; renaming keeps it, then dropping it is a reduction.
        var keep = await Roles(f, f.ManagerUser).Update(f.Cashier.Id, new UpdateRoleRequest("Counter Cashier", null, ["Sales.View", "Sales.Create"]), default);
        var drop = await Roles(f, f.ManagerUser).Update(f.Cashier.Id, new UpdateRoleRequest("Counter Cashier", null, ["Sales.View"]), default);

        Assert.IsType<OkObjectResult>(keep.Result);
        Assert.IsType<OkObjectResult>(drop.Result);
    }

    [Fact]
    public async Task DuplicateRole_NonAdministratorCopyingAdministratorOrUnheldRole_IsForbidden()
    {
        var f = await SeedAsync();

        var copyAdmin = await Roles(f, f.ManagerUser).Duplicate(f.Admin.Id, default);
        var copyCashier = await Roles(f, f.ManagerUser).Duplicate(f.Cashier.Id, default);
        var copyOwn = await Roles(f, f.ManagerUser).Duplicate(f.Manager.Id, default);

        Assert.StartsWith("You cannot duplicate this role.", Title(copyAdmin.Result, 403));
        Assert.Equal("You cannot duplicate this role. You can only grant permissions you hold yourself. Not held: Sales.Create.", Title(copyCashier.Result, 403));
        Assert.IsType<CreatedAtActionResult>(copyOwn.Result);
        Assert.Equal(4, await f.Db.Roles.CountAsync());
    }

    [Fact]
    public async Task DuplicateRole_AdministratorCopyingAdministrator_GetsAllPermissionsOnANonSystemRole()
    {
        var f = await SeedAsync();

        var result = await Roles(f, f.AdminUser).Duplicate(f.Admin.Id, default);

        var copy = Assert.IsType<RoleDetailDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.False(copy.IsSystem);
        Assert.Equal(PermissionCatalog.All.Count, copy.PermissionCodes.Length);
    }

    // ---- H4: deactivation and inactive roles ----

    [Fact]
    public async Task DeactivateRole_WithActiveUsers_IsRejected()
    {
        var f = await SeedAsync();

        var result = await Roles(f, f.AdminUser).SetActive(f.Cashier.Id, new SetRoleActiveRequest(false), default);

        Assert.Equal("This role is assigned to 1 active user(s). Reassign them to another role first.", Title(result, 400));
        Assert.True((await f.Db.Roles.AsNoTracking().SingleAsync(r => r.Id == f.Cashier.Id)).IsActive);
    }

    [Fact]
    public async Task DeactivateRole_WhoseUsersAreAllDisabled_Succeeds_AndReactivatingIsAlwaysAllowed()
    {
        var f = await SeedAsync();
        var cashier = await f.Db.Users.SingleAsync(u => u.Id == f.CashierUser.Id);
        cashier.Status = UserStatus.Disabled;
        await f.Db.SaveChangesAsync();

        var off = await Roles(f, f.AdminUser).SetActive(f.Cashier.Id, new SetRoleActiveRequest(false), default);
        var on = await Roles(f, f.AdminUser).SetActive(f.Cashier.Id, new SetRoleActiveRequest(true), default);

        Assert.IsType<NoContentResult>(off);
        Assert.IsType<NoContentResult>(on);
    }

    [Fact]
    public async Task InactiveRole_GrantsNoPermissions_AndItsHolderIsNoLongerTreatedAsHoldingThem()
    {
        var f = await SeedAsync();
        await SetRoleActiveDirectlyAsync(f, f.Manager, false);

        var role = await f.Db.Roles.Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission).SingleAsync(r => r.Id == f.Manager.Id);
        Assert.Empty(PermissionResolver.Resolve(role));

        // Even with a still-valid token, management actions check the caller's current permissions.
        var result = await Roles(f, f.ManagerUser).Create(new CreateRoleRequest("Viewer", null, ["Sales.View"]), default);
        Assert.Equal("You can only grant permissions you hold yourself. Not held: Sales.View.", FieldError(result.Result, "PermissionCodes"));
    }
}
