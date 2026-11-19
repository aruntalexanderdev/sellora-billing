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
using Sellora.Domain.Roles;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Api;

/// <summary>
/// Phase 01b (FI-D44, FI-D45): branch-admin user administration through <see cref="UsersController"/> (branch path, branch-limited context as the
/// permission handler leaves it) and the company-only grant of branch administration codes through the role and assignment services.
/// </summary>
public class BranchUserAdministrationTests
{
    private const string ValidPassword = "Str0ng!Passw0rd";

    private static readonly string[] AdminCodes =
    [
        "Branch.Users.View", "Branch.Users.Create", "Branch.Users.Edit", "Branch.Users.Disable", "Branch.Users.ResetPassword", "Branch.Users.AssignRoles",
    ];

    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => "hash:" + password;
        public bool Verify(string password, string hash) => hash == "hash:" + password;
    }

    private sealed class Env
    {
        public RbacWorld W { get; } = new();
        public Branch A => W.B1;
        public Branch B => W.B2;
        public Role AdminRoleA = null!, StaffRoleA = null!, StaffRoleB = null!, CompanyCashier = null!, ManagerRoleA = null!;
        public User AdminA = null!, StaffA = null!, StaffB = null!, CompanyUser = null!, PeerAdmin = null!, CompanyAdmin = null!, RoleManager = null!;
        public UserCompanyMembership AdminAM = null!, StaffAM = null!, PeerAdminM = null!, CompanyAdminM = null!, RoleManagerM = null!;
    }

    private static Env Seed()
    {
        var e = new Env();
        var w = e.W;
        e.AdminRoleA = w.BranchRole("Branch A Admin", e.A, [.. AdminCodes, "Sales.View", "Sales.Create"]);
        e.StaffRoleA = w.BranchRole("Branch A Staff", e.A, "Sales.View", "Sales.Create");
        e.StaffRoleB = w.BranchRole("Branch B Staff", e.B, "Sales.View");
        e.CompanyCashier = w.CompanyRole("Company Cashier", "Sales.View");
        e.ManagerRoleA = w.BranchRole("Branch A Role Manager", e.A, "Branch.Roles.View", "Branch.Roles.Manage", "Branch.Users.Create", "Sales.View", "Sales.Create");

        (e.AdminA, e.AdminAM) = w.BranchMemberWith("adminA", e.AdminRoleA, e.A);
        (e.PeerAdmin, e.PeerAdminM) = w.BranchMemberWith("peerA", e.AdminRoleA, e.A);
        (e.StaffA, e.StaffAM) = w.BranchMemberWith("staffA", e.StaffRoleA, e.A);
        (e.StaffB, _) = w.BranchMemberWith("staffB", e.StaffRoleB, e.B);
        (e.CompanyUser, _) = w.CompanyWideWith("companyUser", e.CompanyCashier);
        (e.CompanyAdmin, e.CompanyAdminM) = w.CompanyWideWith("companyAdmin", w.Administrator);
        (e.RoleManager, e.RoleManagerM) = w.BranchMemberWith("roleMgr", e.ManagerRoleA, e.A);
        return e;
    }

    /// <summary>A context over the same store limited to branch A, as the middleware plus <c>PermissionAuthorizationHandler</c> leave it.</summary>
    private static SelloraDbContext BranchDb(Env e, Branch branch)
    {
        var ctx = new BranchContext();
        var set = new HashSet<Guid> { branch.Id };
        ctx.Set(branch.Id, set, branch.Id, branch.Id);
        ctx.Narrow(set);
        return new SelloraDbContext(e.W.Options, TestCompany.Context, ctx);
    }

    private static UsersController Users(SelloraDbContext db, User actor)
    {
        var access = new UserAccessService(db);
        return new UsersController(db, new FakeHasher(), access, new RoleAssignmentService(db, access), Rbac.Effective(db, Rbac.ContextOf(db, actor.Id)))
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

    private static UsersController AsBranchAdmin(Env e, User? actor = null) => Users(BranchDb(e, e.A), actor ?? e.AdminA);

    private static UsersController AsCompanyAdmin(Env e) => Users(e.W.Db, e.CompanyAdmin);

    private static CreateUserRequest NewUser(Guid roleId, string name = "anu", CompanyAccessScope? scope = null, Guid[]? branches = null, Guid? primary = null) =>
        new("Anu", "Varghese", name, $"{name}@selloramart.in", null, ValidPassword, ValidPassword, roleId, scope, branches, primary);

    private static UpdateUserRequest Edit(User user, Guid roleId) => new(user.FirstName, user.LastName, user.Username, "new-" + user.Email, null, roleId);

    private static string FieldError(IActionResult? result, string field)
    {
        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        return Assert.Single(problem.Errors[field]);
    }

    private static string ForbiddenCode(IActionResult? result)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, obj.StatusCode);
        return (string)Assert.IsType<ProblemDetails>(obj.Value).Extensions["code"]!;
    }

    private static async Task<List<string>> ListedUsernamesAsync(UsersController controller)
    {
        var result = await controller.List(null, null, null, 1, 100, default);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<PagedResult<UserListItemDto>>(ok.Value).Items.Select(u => u.Username).ToList();
    }

    // ---- 1. Branch admin creates a user ----

    [Fact]
    public async Task Create_BranchAdminWithBranchRole_CreatesAssignedBranchesMemberAllocatedToTheBranchWithAudit()
    {
        var e = Seed();

        var result = await AsBranchAdmin(e).Create(NewUser(e.StaffRoleA.Id), default);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var db = e.W.Db;
        db.ChangeTracker.Clear();
        var user = await db.Users.SingleAsync(u => u.Username == "anu");
        var m = await db.UserCompanyMemberships.SingleAsync(x => x.UserId == user.Id);
        Assert.Equal(CompanyAccessScope.AssignedBranches, m.AccessScope);
        var allocation = Assert.Single(await db.UserBranchAssignments.Where(a => a.UserCompanyMembershipId == m.Id && a.Status == AssignmentStatus.Active).ToListAsync());
        Assert.Equal(e.A.Id, allocation.BranchId);
        Assert.True(allocation.IsPrimary);
        var role = Assert.Single(await db.UserRoleAssignments.Where(a => a.UserCompanyMembershipId == m.Id && a.Status == AssignmentStatus.Active).ToListAsync());
        Assert.Equal((e.StaffRoleA.Id, RoleScope.Branch, e.A.Id), (role.RoleId, role.ScopeType, role.BranchId));
        Assert.Single(await e.W.AuditAsync(AuditActions.MembershipCreated), a => a.SubjectUserId == user.Id);
        Assert.Single(await e.W.AuditAsync(AuditActions.BranchAssignmentCreated), a => a.SubjectUserId == user.Id);
        Assert.Single(await e.W.AuditAsync(AuditActions.RoleAssignmentCreated), a => a.SubjectUserId == user.Id);
    }

    [Fact]
    public async Task Create_BranchAdminSupplyingScopeAndBranches_IgnoresThemAndStaysInTheActiveBranch()
    {
        var e = Seed();

        var result = await AsBranchAdmin(e).Create(NewUser(e.StaffRoleA.Id, scope: CompanyAccessScope.CompanyWide, branches: [e.B.Id], primary: e.B.Id), default);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var db = e.W.Db;
        db.ChangeTracker.Clear();
        var user = await db.Users.SingleAsync(u => u.Username == "anu");
        var m = await db.UserCompanyMemberships.SingleAsync(x => x.UserId == user.Id);
        Assert.Equal(CompanyAccessScope.AssignedBranches, m.AccessScope);
        var allocation = Assert.Single(await db.UserBranchAssignments.Where(a => a.UserCompanyMembershipId == m.Id).ToListAsync());
        Assert.Equal(e.A.Id, allocation.BranchId);
    }

    // ---- 2. Create validation ----

    [Fact]
    public async Task Create_BranchAdminWithCompanyRole_IsRejectedOnRoleId()
    {
        var e = Seed();

        var result = await AsBranchAdmin(e).Create(NewUser(e.CompanyCashier.Id), default);

        Assert.NotEmpty(FieldError(result.Result, "roleId"));
        Assert.False(await e.W.Db.Users.AnyAsync(u => u.Username == "anu"));
    }

    [Fact]
    public async Task Create_BranchAdminWithAnotherBranchsRole_IsRejectedOnRoleId()
    {
        var e = Seed();

        var result = await AsBranchAdmin(e).Create(NewUser(e.StaffRoleB.Id), default);

        Assert.NotEmpty(FieldError(result.Result, "roleId"));
        Assert.False(await e.W.Db.Users.AnyAsync(u => u.Username == "anu"));
    }

    [Fact]
    public async Task Create_BranchAdminWithRoleHoldingACodeTheyLack_IsForbiddenAsEscalation()
    {
        var e = Seed();
        var richer = e.W.BranchRole("Branch A Returns", e.A, "Sales.View", "Sales.Return");

        var result = await AsBranchAdmin(e).Create(NewUser(richer.Id), default);

        Assert.Equal("role_escalation_forbidden", ForbiddenCode(result.Result));
        Assert.False(await e.W.Db.Users.AnyAsync(u => u.Username == "anu"));
    }

    [Theory]
    [InlineData("Branch.Roles.Manage")]
    [InlineData("Branch.Users.Create")]
    [InlineData("Branch.Users.AssignRoles")]
    public async Task Create_BranchAdminWithRoleHoldingBranchAdministrationCode_IsForbiddenCompanyOnly(string code)
    {
        var e = Seed();
        var adminish = e.W.BranchRole("Another Admin", e.A, "Sales.View", code);

        var result = await AsBranchAdmin(e).Create(NewUser(adminish.Id), default);

        Assert.Equal("branch_administration_company_only", ForbiddenCode(result.Result));
        Assert.False(await e.W.Db.Users.AnyAsync(u => u.Username == "anu"));
    }

    // ---- 3. Target visibility ----

    [Fact]
    public async Task List_BranchAdmin_ShowsOnlyBranchStaffNotOthersPeersCompanyUsersOrSelf()
    {
        var e = Seed();

        var names = await ListedUsernamesAsync(AsBranchAdmin(e));

        Assert.Contains("staffA", names);
        Assert.DoesNotContain("staffB", names);
        Assert.DoesNotContain("companyUser", names);
        Assert.DoesNotContain("companyAdmin", names);
        Assert.DoesNotContain("peerA", names);
        Assert.DoesNotContain("adminA", names);
    }

    [Fact]
    public async Task Get_BranchAdminOnBranchStaff_ReturnsTheUser()
    {
        var e = Seed();

        var result = await AsBranchAdmin(e).Get(e.StaffA.Id, default);

        Assert.Equal(e.StaffA.Id, Assert.IsType<UserDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Id);
    }

    [Fact]
    public async Task Get_BranchAdminOnOutOfReachUsers_IsNotFound()
    {
        var e = Seed();

        foreach (var target in new[] { e.StaffB, e.CompanyUser, e.PeerAdmin, e.AdminA, e.CompanyAdmin })
        {
            Assert.IsType<NotFoundResult>((await AsBranchAdmin(e).Get(target.Id, default)).Result);
        }
    }

    [Fact]
    public async Task Update_BranchAdminOnBranchStaff_EditsProfileAndKeepsRoles()
    {
        var e = Seed();

        var result = await AsBranchAdmin(e).Update(e.StaffA.Id, Edit(e.StaffA, e.StaffRoleA.Id), default);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("new-staffA@selloramart.in", (await e.W.Db.Users.AsNoTracking().SingleAsync(u => u.Id == e.StaffA.Id)).Email);
        Assert.Equal([e.StaffRoleA.Id], await Rbac.ActiveRoleIdsAsync(e.W.Db, e.StaffA.Id));
    }

    [Fact]
    public async Task Update_BranchAdminSendingACompanyRoleId_DoesNotChangeRoles()
    {
        var e = Seed();

        await AsBranchAdmin(e).Update(e.StaffA.Id, Edit(e.StaffA, e.CompanyCashier.Id), default);

        Assert.Equal([e.StaffRoleA.Id], await Rbac.ActiveRoleIdsAsync(e.W.Db, e.StaffA.Id));
    }

    [Fact]
    public async Task Update_BranchAdminOnOutOfReachUsers_IsNotFound()
    {
        var e = Seed();

        foreach (var target in new[] { e.StaffB, e.CompanyUser, e.PeerAdmin, e.AdminA })
        {
            Assert.IsType<NotFoundResult>((await AsBranchAdmin(e).Update(target.Id, Edit(target, e.StaffRoleA.Id), default)).Result);
        }
    }

    [Fact]
    public async Task SetStatus_BranchAdminOnBranchStaff_DisablesThem()
    {
        var e = Seed();

        var result = await AsBranchAdmin(e).SetStatus(e.StaffA.Id, new SetUserStatusRequest(UserStatus.Disabled), default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(UserStatus.Disabled, (await e.W.Db.Users.AsNoTracking().SingleAsync(u => u.Id == e.StaffA.Id)).Status);
    }

    [Fact]
    public async Task SetStatus_BranchAdminOnOutOfReachUsers_IsNotFoundAndChangesNothing()
    {
        var e = Seed();

        foreach (var target in new[] { e.StaffB, e.CompanyUser, e.PeerAdmin, e.AdminA })
        {
            Assert.IsType<NotFoundResult>(await AsBranchAdmin(e).SetStatus(target.Id, new SetUserStatusRequest(UserStatus.Disabled), default));
            Assert.Equal(UserStatus.Active, (await e.W.Db.Users.AsNoTracking().SingleAsync(u => u.Id == target.Id)).Status);
        }
    }

    [Fact]
    public async Task ResetPassword_BranchAdminOnBranchStaff_ReturnsATemporaryPassword()
    {
        var e = Seed();

        var result = await AsBranchAdmin(e).ResetPassword(e.StaffA.Id, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.False(string.IsNullOrEmpty(Assert.IsType<ResetPasswordResponse>(ok.Value).TemporaryPassword));
        Assert.StartsWith("hash:", (await e.W.Db.Users.AsNoTracking().SingleAsync(u => u.Id == e.StaffA.Id)).PasswordHash);
    }

    [Fact]
    public async Task ResetPassword_BranchAdminOnOutOfReachUsers_IsNotFound()
    {
        var e = Seed();

        foreach (var target in new[] { e.StaffB, e.CompanyUser, e.PeerAdmin, e.AdminA })
        {
            Assert.IsType<NotFoundResult>((await AsBranchAdmin(e).ResetPassword(target.Id, default)).Result);
            Assert.Equal("x", (await e.W.Db.Users.AsNoTracking().SingleAsync(u => u.Id == target.Id)).PasswordHash);
        }
    }

    // ---- 4. FI-D45: branch administration is granted by the company only ----

    private static CreateRoleInput NewBranchRole(Env e, params string[] codes) => new("Fresh Role", null, codes, RoleScope.Branch, e.A.Id);

    private static async Task<AccessManagementForbiddenException> Forbidden(Func<Task> action) =>
        await Assert.ThrowsAsync<AccessManagementForbiddenException>(action);

    [Fact]
    public async Task CreateRole_BranchPathActorWithBranchUsersCreate_IsForbiddenCompanyOnly()
    {
        var e = Seed();
        var actor = await e.W.ActorOfAsync(e.RoleManager, e.RoleManagerM);

        var ex = await Forbidden(() => e.W.Roles.CreateAsync(actor, NewBranchRole(e, "Branch.Users.Create"), default));

        Assert.Equal(RoleErrorCodes.BranchAdministrationCompanyOnly, ex.Code);
    }

    [Fact]
    public async Task CreateRole_BranchPathActorWithOrdinaryCodes_Succeeds()
    {
        var e = Seed();
        var actor = await e.W.ActorOfAsync(e.RoleManager, e.RoleManagerM);

        var role = await e.W.Roles.CreateAsync(actor, NewBranchRole(e, "Sales.View"), default);

        Assert.Equal(e.A.Id, role.BranchId);
    }

    [Fact]
    public async Task UpdateRole_BranchPathActorOnRoleHoldingAdminCodes_IsForbiddenCompanyOnly()
    {
        var e = Seed();
        var actor = await e.W.ActorOfAsync(e.RoleManager, e.RoleManagerM);
        var input = new UpdateRoleInput("Branch A Admin", null, ["Sales.View"], null, null);

        var ex = await Forbidden(() => e.W.Roles.UpdateAsync(actor, e.AdminRoleA.Id, input, default));

        Assert.Equal(RoleErrorCodes.BranchAdministrationCompanyOnly, ex.Code);
    }

    [Fact]
    public async Task UpdateRole_BranchPathActorAddingAdminCodeToOrdinaryRole_IsForbiddenCompanyOnly()
    {
        var e = Seed();
        var actor = await e.W.ActorOfAsync(e.RoleManager, e.RoleManagerM);
        var input = new UpdateRoleInput("Branch A Staff", null, ["Sales.View", "Branch.Users.Create"], null, null);

        var ex = await Forbidden(() => e.W.Roles.UpdateAsync(actor, e.StaffRoleA.Id, input, default));

        Assert.Equal(RoleErrorCodes.BranchAdministrationCompanyOnly, ex.Code);
    }

    [Fact]
    public async Task DuplicateRole_BranchPathActorOnRoleHoldingAdminCodes_IsForbiddenCompanyOnly()
    {
        var e = Seed();
        var actor = await e.W.ActorOfAsync(e.RoleManager, e.RoleManagerM);

        var ex = await Forbidden(() => e.W.Roles.DuplicateAsync(actor, e.ManagerRoleA.Id, default));

        Assert.Equal(RoleErrorCodes.BranchAdministrationCompanyOnly, ex.Code);
    }

    [Fact]
    public async Task SetRoleActive_BranchPathActorOnRoleHoldingAdminCodes_IsForbiddenCompanyOnly()
    {
        var e = Seed();
        var spare = e.W.BranchRole("Spare Admin", e.A, "Sales.View", "Branch.Users.View");
        var actor = await e.W.ActorOfAsync(e.RoleManager, e.RoleManagerM);

        var ex = await Forbidden(() => e.W.Roles.SetActiveAsync(actor, spare.Id, false, default));

        Assert.Equal(RoleErrorCodes.BranchAdministrationCompanyOnly, ex.Code);
        Assert.True((await e.W.Db.Roles.AsNoTracking().SingleAsync(r => r.Id == spare.Id)).IsActive);
    }

    [Fact]
    public async Task AssignRole_BranchPathActorAssigningRoleWithAdminCodes_IsForbiddenCompanyOnly()
    {
        var e = Seed();
        var actor = await e.W.ActorOfAsync(e.AdminA, e.AdminAM);

        var ex = await Forbidden(() => e.W.Assignments.AssignAsync(actor, e.StaffA.Id, e.AdminRoleA.Id, null, default));

        Assert.Equal(RoleErrorCodes.BranchAdministrationCompanyOnly, ex.Code);
    }

    [Fact]
    public async Task EndAssignment_BranchPathActorEndingRoleWithAdminCodes_IsForbiddenCompanyOnly()
    {
        var e = Seed();
        var actor = await e.W.ActorOfAsync(e.AdminA, e.AdminAM);
        var peerAssignment = (await e.W.AssignmentsOfAsync(e.PeerAdminM)).Single(a => a.RoleId == e.AdminRoleA.Id);

        var ex = await Forbidden(() => e.W.Assignments.EndAsync(actor, e.PeerAdmin.Id, peerAssignment.Id, "cleanup", default));

        Assert.Equal(RoleErrorCodes.BranchAdministrationCompanyOnly, ex.Code);
    }

    [Fact]
    public async Task CompanyAdministrator_CanGrantAssignAndEndBranchAdministrationRoles()
    {
        var e = Seed();
        var actor = await e.W.ActorOfAsync(e.CompanyAdmin, e.CompanyAdminM);

        var created = await e.W.Roles.CreateAsync(actor, NewBranchRole(e, "Branch.Users.Create", "Branch.Users.View"), default);
        var duplicate = await e.W.Roles.DuplicateAsync(actor, e.AdminRoleA.Id, default);
        var updated = await e.W.Roles.UpdateAsync(actor, created.Id, new UpdateRoleInput("Fresh Role", null, ["Branch.Users.Create", "Branch.Roles.Manage"], null, null), default);
        var assignment = await e.W.Assignments.AssignAsync(actor, e.StaffA.Id, created.Id, null, default);
        await e.W.Assignments.EndAsync(actor, e.StaffA.Id, assignment.Id, "done", default);
        await e.W.Roles.SetActiveAsync(actor, duplicate.Id, false, default);

        Assert.Equal(e.A.Id, created.BranchId);
        Assert.Equal(["Branch.Roles.Manage", "Branch.Users.Create"], await e.W.CodesOfAsync(updated.Id));
        Assert.False((await e.W.Db.Roles.AsNoTracking().SingleAsync(r => r.Id == duplicate.Id)).IsActive);
        Assert.DoesNotContain(await e.W.AssignmentsOfAsync(e.StaffAM), a => a.RoleId == created.Id && a.Status == AssignmentStatus.Active);
    }

    // ---- 5. Regression: company path ----

    [Fact]
    public async Task Create_CompanyAdministratorWithCompanyRole_StillCreatesCompanyPathUser()
    {
        var e = Seed();

        var result = await AsCompanyAdmin(e).Create(NewUser(e.CompanyCashier.Id), default);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var db = e.W.Db;
        db.ChangeTracker.Clear();
        var user = await db.Users.SingleAsync(u => u.Username == "anu");
        Assert.Equal([e.CompanyCashier.Id], await Rbac.ActiveRoleIdsAsync(db, user.Id));
        var m = await db.UserCompanyMemberships.SingleAsync(x => x.UserId == user.Id);
        Assert.Equal(CompanyAccessScope.AssignedBranches, m.AccessScope);
    }

    [Fact]
    public async Task Create_CompanyAdministratorWithExplicitAccess_HonoursScopeAndBranches()
    {
        var e = Seed();

        var result = await AsCompanyAdmin(e).Create(NewUser(e.CompanyCashier.Id, scope: CompanyAccessScope.AssignedBranches, branches: [e.B.Id], primary: e.B.Id), default);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var db = e.W.Db;
        db.ChangeTracker.Clear();
        var user = await db.Users.SingleAsync(u => u.Username == "anu");
        var m = await db.UserCompanyMemberships.SingleAsync(x => x.UserId == user.Id);
        Assert.Equal(e.B.Id, Assert.Single(await db.UserBranchAssignments.Where(a => a.UserCompanyMembershipId == m.Id).ToListAsync()).BranchId);
    }

    [Fact]
    public async Task List_CompanyAdministrator_SeesEveryUserIncludingBranchAdmins()
    {
        var e = Seed();

        var names = await ListedUsernamesAsync(AsCompanyAdmin(e));

        Assert.Subset(new HashSet<string>(names), new HashSet<string> { "adminA", "peerA", "staffA", "staffB", "companyUser", "companyAdmin" });
    }
}
