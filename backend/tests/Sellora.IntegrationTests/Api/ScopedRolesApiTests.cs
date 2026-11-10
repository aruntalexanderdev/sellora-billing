using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Phase 7 scoped RBAC through the real HTTP pipeline on PostgreSQL (design T1-T7, T9, T12): the escalation matrix R1-R4, scope immutability and branch-eligible codes,
/// branch administrators confined to their branch, narrowing of branch-role holders, immediate revocation with the same token (AccessVersion read per request),
/// E2 closing of branch-role assignments, governance/operational separation, the RoleId compatibility paths and the stable ProblemDetails codes.
/// Users, roles and branches are created through the API with unique names; the host is shared.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ScopedRolesApiTests(PostgresFixture postgres)
{
    private const string Pw = "Passw0rd!99";
    private static int _seq;

    private static string N(string prefix) => prefix + Interlocked.Increment(ref _seq) + Guid.NewGuid().ToString("N")[..4];

    private static readonly string[] ManagerCodes =
        ["Users.View", "Users.Edit", "Roles.View", "Roles.Create", "Roles.Edit", "Roles.Duplicate", "Roles.Disable", "Company.Users.AssignRoles", "Sales.View"];

    private static readonly string[] BranchAdminCodes =
        ["Branch.Roles.View", "Branch.Roles.Manage", "Branch.Users.AssignRoles", "Sales.View", "Sales.Create"];

    private sealed record Member(Guid Id, string Username, HttpClient Client);

    private static Task<HttpResponseMessage> Send(BranchApiWorld w, HttpMethod m, string route, object? body = null, HttpClient? client = null, Guid? branch = null) =>
        w.SendAsync(m, route, body, branch, client);

    private static async Task<Guid> RoleAsync(BranchApiWorld w, string name, string[] codes, RoleScope? scope = null, Guid? branch = null, HttpClient? client = null)
    {
        var response = await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(name, null, codes, scope, branch), client);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"create role {name}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<Guid> EmptyRoleAsync(BranchApiWorld w) => RoleAsync(w, N("Empty"), []);

    private static async Task<Guid> AdministratorRoleIdAsync(BranchApiWorld w)
    {
        var roles = await JsonAsync(await Send(w, HttpMethod.Get, "/api/v1/roles"));
        return roles.EnumerateArray().Single(r => r.GetProperty("isSystem").GetBoolean()).GetProperty("id").GetGuid();
    }

    /// <summary>Creates a user (initial company role = <paramref name="companyRole"/>) with the given access and logs it in.</summary>
    private static async Task<Member> MemberAsync(BranchApiWorld w, Guid companyRole, CompanyAccessScope scope = CompanyAccessScope.CompanyWide, Guid[]? branches = null, Guid? primary = null)
    {
        var username = N("u");
        var response = await Send(w, HttpMethod.Post, "/api/v1/users",
            new CreateUserRequest("Test", "User", username, username + "@selloramart.in", null, Pw, Pw, companyRole, scope, branches, primary));
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"create user: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var id = (await JsonAsync(response)).GetProperty("id").GetGuid();
        var auth = await w.Host.LoginAsync(username, Pw);
        return new Member(id, username, w.Host.NewClient(auth.AccessToken));
    }

    private static async Task<HttpResponseMessage> AssignAsync(BranchApiWorld w, Guid userId, Guid roleId, HttpClient? client = null) =>
        await Send(w, HttpMethod.Post, $"/api/v1/users/{userId}/roles", new AssignRoleRequest(roleId, "test"), client);

    private static async Task<Guid> AssignOkAsync(BranchApiWorld w, Guid userId, Guid roleId, HttpClient? client = null)
    {
        var response = await AssignAsync(w, userId, roleId, client);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"assign: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await JsonAsync(response)).GetProperty("current").EnumerateArray().Single(a => a.GetProperty("roleId").GetGuid() == roleId).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> RolesOfAsync(BranchApiWorld w, Guid userId) => await JsonAsync(await Send(w, HttpMethod.Get, $"/api/v1/users/{userId}/roles"));

    private static async Task AssertCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await response.ProblemCodeAsync());
    }

    private static async Task AssertFieldAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field.ToLowerInvariant(), await ErrorFieldsAsync(response));
    }

    private static async Task<Member> ManagerAsync(BranchApiWorld w) => await MemberAsync(w, await RoleAsync(w, N("Manager"), ManagerCodes));

    private async Task<BranchApiWorld> WorldAsync() => await GetAsync(postgres);

    // ---- T1: escalation matrix (R1) ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task R1_CreateRoleWithCodesTheActorDoesNotHold_Is400OnPermissionCodes_AndNothingIsCreated()
    {
        var w = await WorldAsync();
        var manager = await ManagerAsync(w);
        var name = N("Greedy");

        var response = await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(name, null, ["Sales.View", "Sales.Create"]), manager.Client);

        await AssertFieldAsync(response, "permissionCodes");
        var all = await JsonAsync(await Send(w, HttpMethod.Get, "/api/v1/roles"));
        Assert.DoesNotContain(all.EnumerateArray(), r => r.GetProperty("name").GetString() == name);
    }

    [Fact]
    public async Task R1_CreateRoleWithHeldCodes_Succeeds_AndUpdateCannotAddUnheldCodes()
    {
        var w = await WorldAsync();
        var manager = await ManagerAsync(w);
        var role = await RoleAsync(w, N("Viewer"), ["Sales.View"], client: manager.Client);

        var add = await Send(w, HttpMethod.Put, $"/api/v1/roles/{role}", new UpdateRoleRequest(N("Viewer"), null, ["Sales.View", "Purchase.View"]), manager.Client);
        var keepAndRemove = await Send(w, HttpMethod.Put, $"/api/v1/roles/{role}", new UpdateRoleRequest(N("Viewer"), null, []), manager.Client);

        await AssertFieldAsync(add, "permissionCodes");
        Assert.Equal(HttpStatusCode.OK, keepAndRemove.StatusCode);
    }

    [Fact]
    public async Task R1_DuplicateARoleHoldingUnheldCodes_Is403RoleEscalationForbidden()
    {
        var w = await WorldAsync();
        var manager = await ManagerAsync(w);
        var cashier = await RoleAsync(w, N("Cashier"), ["Sales.View", "Sales.Create"]);
        var administrator = await AdministratorRoleIdAsync(w);

        await AssertCodeAsync(await Send(w, HttpMethod.Post, $"/api/v1/roles/{cashier}/duplicate", client: manager.Client), HttpStatusCode.Forbidden, "role_escalation_forbidden");
        await AssertCodeAsync(await Send(w, HttpMethod.Post, $"/api/v1/roles/{administrator}/duplicate", client: manager.Client), HttpStatusCode.Forbidden, "role_escalation_forbidden");
    }

    [Fact]
    public async Task R1_AdministratorMayGrantAnyCatalogueCode_ButGovernanceCodesAreNotCatalogueCodes()
    {
        var w = await WorldAsync();

        var any = await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(N("Auditor"), null, ["Reports.Profit", "Inventory.Adjust", "Company.Users.AssignRoles"]));
        var governance = await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(N("Gov"), null, ["Company.Governance.View"]));

        Assert.Equal(HttpStatusCode.Created, any.StatusCode);
        await AssertFieldAsync(governance, "permissionCodes"); // governance codes live in their own table and are never assignable through roles
    }

    // ---- R2: company roles need a CompanyWide actor (Q1) -------------------------------------------------------------------------

    [Fact]
    public async Task R2_RoleManagementWithACompanyRole_ButAssignedBranchesMembership_Is403RoleManagementForbidden()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var scoped = await MemberAsync(w, await RoleAsync(w, N("Manager"), ManagerCodes), CompanyAccessScope.AssignedBranches, [b1], b1);

        var list = await Send(w, HttpMethod.Get, "/api/v1/roles", client: scoped.Client);
        var create = await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(N("X"), null, ["Sales.View"]), scoped.Client);
        var duplicate = await Send(w, HttpMethod.Post, $"/api/v1/roles/{await AdministratorRoleIdAsync(w)}/duplicate", client: scoped.Client);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode); // viewing is not management
        await AssertCodeAsync(create, HttpStatusCode.Forbidden, "role_management_forbidden");
        Assert.Equal(HttpStatusCode.Forbidden, duplicate.StatusCode);
    }

    [Fact]
    public async Task R2_ActorWithoutAnyRoleCode_IsForbiddenToListAndManageRoles()
    {
        var w = await WorldAsync();
        var nobody = await MemberAsync(w, await EmptyRoleAsync(w));

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, "/api/v1/roles", client: nobody.Client)).StatusCode);
        await AssertCodeAsync(await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(N("X"), null, []), nobody.Client), HttpStatusCode.Forbidden, "role_management_forbidden");
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w, HttpMethod.Get, $"/api/v1/roles/{Guid.NewGuid()}", client: nobody.Client)).StatusCode);
    }

    [Fact]
    public async Task EveryServiceCheckedRoleRoute_RefusesASignedInUserWithoutAnyCode()
    {
        var w = await WorldAsync();
        var nobody = await MemberAsync(w, await EmptyRoleAsync(w));
        var target = await MemberAsync(w, await RoleAsync(w, N("Held"), ["Sales.View"]));
        var role = await RoleAsync(w, N("Some"), ["Sales.View"]);
        var row = (await RolesOfAsync(w, target.Id)).GetProperty("current").EnumerateArray().Single().GetProperty("id").GetGuid();
        var (b1, _) = await w.NewBranchAsync();

        var attempts = new (string Name, HttpResponseMessage Response)[]
        {
            ("GET roles", await Send(w, HttpMethod.Get, "/api/v1/roles", client: nobody.Client)),
            ("GET roles?scope=Branch", await Send(w, HttpMethod.Get, $"/api/v1/roles?scope=Branch&branchId={b1}", client: nobody.Client)),
            ("GET roles/id", await Send(w, HttpMethod.Get, $"/api/v1/roles/{role}", client: nobody.Client)),
            ("POST roles", await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(N("X"), null, []), nobody.Client)),
            ("POST roles (branch)", await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(N("X"), null, [], RoleScope.Branch, b1), nobody.Client)),
            ("PUT roles/id", await Send(w, HttpMethod.Put, $"/api/v1/roles/{role}", new UpdateRoleRequest(N("X"), null, []), nobody.Client)),
            ("POST roles/id/duplicate", await Send(w, HttpMethod.Post, $"/api/v1/roles/{role}/duplicate", client: nobody.Client)),
            ("POST roles/id/status", await Send(w, HttpMethod.Post, $"/api/v1/roles/{role}/status", new SetRoleActiveRequest(false), nobody.Client)),
            ("GET users/id/roles", await Send(w, HttpMethod.Get, $"/api/v1/users/{target.Id}/roles", client: nobody.Client)),
            ("POST users/id/roles", await AssignAsync(w, target.Id, role, nobody.Client)),
            ("POST users/id/roles/id/end", await Send(w, HttpMethod.Post, $"/api/v1/users/{target.Id}/roles/{row}/end", new EndRoleAssignmentRequest("x"), nobody.Client)),
            ("GET branches/id/members", await Send(w, HttpMethod.Get, $"/api/v1/branches/{b1}/members", client: nobody.Client)),
        };

        foreach (var (name, response) in attempts)
            Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"{name}: {(int)response.StatusCode}");
        Assert.Single((await RolesOfAsync(w, target.Id)).GetProperty("current").EnumerateArray()); // nothing was assigned or ended
    }

    // ---- R4: assignment rules ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task R4_AssignRoleWithCodesNotHeld_Is403RoleEscalationForbidden()
    {
        var w = await WorldAsync();
        var manager = await ManagerAsync(w);
        var target = await MemberAsync(w, await EmptyRoleAsync(w));
        var cashier = await RoleAsync(w, N("Cashier"), ["Sales.View", "Sales.Create"]);

        var response = await AssignAsync(w, target.Id, cashier, manager.Client);

        await AssertCodeAsync(response, HttpStatusCode.Forbidden, "role_escalation_forbidden");
        Assert.DoesNotContain((await RolesOfAsync(w, target.Id)).GetProperty("current").EnumerateArray(), a => a.GetProperty("roleId").GetGuid() == cashier);
    }

    [Fact]
    public async Task R4_AssignRoleWithinHeldCodes_Succeeds_IsAuditedAndBumpsTheTargetsAccessVersion()
    {
        var w = await WorldAsync();
        var manager = await ManagerAsync(w);
        var target = await MemberAsync(w, await EmptyRoleAsync(w));
        var viewer = await RoleAsync(w, N("Viewer"), ["Sales.View"]);
        var before = await ScalarAsync<long>(w.Host.Database, $"""SELECT "AccessVersion" FROM "UserCompanyMemberships" WHERE "UserId" = '{target.Id}'""");

        await AssignOkAsync(w, target.Id, viewer, manager.Client);

        Assert.True(await ScalarAsync<long>(w.Host.Database, $"""SELECT "AccessVersion" FROM "UserCompanyMemberships" WHERE "UserId" = '{target.Id}'""") > before);
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'role_assignment.created' AND "SubjectUserId" = '{target.Id}' AND "ActorUserId" = '{manager.Id}'"""));
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: target.Client)).StatusCode);
    }

    [Fact]
    public async Task R4_AdministratorRoleByANonAdministrator_Is403AdministratorAssignmentForbidden_AndEndingItToo()
    {
        var w = await WorldAsync();
        var manager = await ManagerAsync(w);
        var target = await MemberAsync(w, await EmptyRoleAsync(w));
        var administrator = await AdministratorRoleIdAsync(w);
        var otherAdmin = await MemberAsync(w, administrator);

        var assign = await AssignAsync(w, target.Id, administrator, manager.Client);
        var adminRow = (await RolesOfAsync(w, otherAdmin.Id)).GetProperty("current").EnumerateArray().Single(a => a.GetProperty("isSystem").GetBoolean()).GetProperty("id").GetGuid();
        var end = await Send(w, HttpMethod.Post, $"/api/v1/users/{otherAdmin.Id}/roles/{adminRow}/end", new EndRoleAssignmentRequest("no"), manager.Client);
        var editAdmin = await Send(w, HttpMethod.Put, $"/api/v1/users/{otherAdmin.Id}", new UpdateUserRequest("X", "Y", otherAdmin.Username, otherAdmin.Username + "@selloramart.in", null, await EmptyRoleAsync(w)), manager.Client);

        await AssertCodeAsync(assign, HttpStatusCode.Forbidden, "administrator_assignment_forbidden");
        await AssertCodeAsync(end, HttpStatusCode.Forbidden, "administrator_assignment_forbidden");
        Assert.Equal(HttpStatusCode.Forbidden, editAdmin.StatusCode);
    }

    [Fact]
    public async Task R4_AdministratorMayAssignTheAdministratorRole_AndPromotionForcesCompanyWide()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var scoped = await MemberAsync(w, await EmptyRoleAsync(w), CompanyAccessScope.AssignedBranches, [b1], b1);

        await AssignOkAsync(w, scoped.Id, await AdministratorRoleIdAsync(w));

        Assert.Equal("CompanyWide", await ScalarAsync<string>(w.Host.Database, $"""SELECT "AccessScope" FROM "UserCompanyMemberships" WHERE "UserId" = '{scoped.Id}'"""));
    }

    [Fact]
    public async Task R4_NobodyChangesTheirOwnRoles_Is403SelfRoleChangeForbidden()
    {
        var w = await WorldAsync();
        var manager = await ManagerAsync(w);
        var viewer = await RoleAsync(w, N("Viewer"), ["Sales.View"]);

        var assign = await AssignAsync(w, manager.Id, viewer, manager.Client);
        var own = (await RolesOfAsync(w, manager.Id)).GetProperty("current").EnumerateArray().First().GetProperty("id").GetGuid();
        var end = await Send(w, HttpMethod.Post, $"/api/v1/users/{manager.Id}/roles/{own}/end", new EndRoleAssignmentRequest("mine"), manager.Client);

        await AssertCodeAsync(assign, HttpStatusCode.Forbidden, "self_role_change_forbidden");
        await AssertCodeAsync(end, HttpStatusCode.Forbidden, "self_role_change_forbidden");
    }

    [Fact]
    public async Task R4_UsersEditWithoutCompanyUsersAssignRoles_CannotAssignOrChangeRoles()
    {
        var w = await WorldAsync();
        var legacy = await MemberAsync(w, await RoleAsync(w, N("UserEditor"), ["Users.View", "Users.Edit", "Roles.View", "Sales.View"]));
        var target = await MemberAsync(w, await EmptyRoleAsync(w));
        var viewer = await RoleAsync(w, N("Viewer"), ["Sales.View"]);

        var assign = await AssignAsync(w, target.Id, viewer, legacy.Client);
        var put = await Send(w, HttpMethod.Put, $"/api/v1/users/{target.Id}", new UpdateUserRequest("A", "B", target.Username, target.Username + "@selloramart.in", null, viewer), legacy.Client);

        await AssertCodeAsync(assign, HttpStatusCode.Forbidden, "role_assignment_forbidden");
        await AssertCodeAsync(put, HttpStatusCode.Forbidden, "role_assignment_forbidden");
    }

    [Fact]
    public async Task R4_AssignedBranchesActorHoldingAssignRoles_CannotAssignCompanyRoles()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var scoped = await MemberAsync(w, await RoleAsync(w, N("Manager"), ManagerCodes), CompanyAccessScope.AssignedBranches, [b1], b1);
        var target = await MemberAsync(w, await EmptyRoleAsync(w));

        var response = await AssignAsync(w, target.Id, await RoleAsync(w, N("Viewer"), ["Sales.View"]), scoped.Client);

        await AssertCodeAsync(response, HttpStatusCode.Forbidden, "role_assignment_forbidden");
    }

    [Fact]
    public async Task R4_AssigningTheSameRoleTwice_AndAnInactiveRole_AreRejectedOnRoleId()
    {
        var w = await WorldAsync();
        var target = await MemberAsync(w, await EmptyRoleAsync(w));
        var viewer = await RoleAsync(w, N("Viewer"), ["Sales.View"]);
        var retired = await RoleAsync(w, N("Retired"), ["Sales.View"]);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(w, HttpMethod.Post, $"/api/v1/roles/{retired}/status", new SetRoleActiveRequest(false))).StatusCode);
        await AssignOkAsync(w, target.Id, viewer);

        await AssertFieldAsync(await AssignAsync(w, target.Id, viewer), "roleId");
        await AssertFieldAsync(await AssignAsync(w, target.Id, retired), "roleId");
        Assert.Equal(HttpStatusCode.NotFound, (await AssignAsync(w, Guid.NewGuid(), viewer)).StatusCode);
    }

    // ---- role lifecycle codes ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SystemRole_CannotBeEditedOrDeactivated_AndADuplicateIsAPlainRole()
    {
        var w = await WorldAsync();
        var administrator = await AdministratorRoleIdAsync(w);

        await AssertCodeAsync(await Send(w, HttpMethod.Put, $"/api/v1/roles/{administrator}", new UpdateRoleRequest("Administrator", null, [])), HttpStatusCode.BadRequest, "system_role_protected");
        await AssertCodeAsync(await Send(w, HttpMethod.Post, $"/api/v1/roles/{administrator}/status", new SetRoleActiveRequest(false)), HttpStatusCode.BadRequest, "system_role_protected");
        var copy = await JsonAsync(await Send(w, HttpMethod.Post, $"/api/v1/roles/{administrator}/duplicate"));
        Assert.False(copy.GetProperty("isSystem").GetBoolean());
        Assert.Equal("Company", copy.GetProperty("scopeType").GetString());
    }

    [Fact]
    public async Task DeactivatingARoleWithActiveHolders_Is400RoleInUse_AndRoleListCountsHoldersOfThisCompanyOnly()
    {
        var w = await WorldAsync();
        var role = await RoleAsync(w, N("Held"), ["Sales.View"]);
        var holder = await MemberAsync(w, role);

        var response = await Send(w, HttpMethod.Post, $"/api/v1/roles/{role}/status", new SetRoleActiveRequest(false));

        await AssertCodeAsync(response, HttpStatusCode.BadRequest, "role_in_use");
        var listed = (await JsonAsync(await Send(w, HttpMethod.Get, "/api/v1/roles"))).EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == role);
        Assert.Equal(1, listed.GetProperty("userCount").GetInt32());
        _ = holder;
    }

    // ---- T4: scope rules ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BranchRole_CannotHoldCompanyOnlyCodes_AndNeedsAValidBranch()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();

        var companyOnly = await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(N("Bad"), null, ["Sales.View", "Users.View"], RoleScope.Branch, b1));
        var noBranch = await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(N("Bad"), null, ["Sales.View"], RoleScope.Branch, null));
        var companyWithBranch = await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(N("Bad"), null, ["Sales.View"], RoleScope.Company, b1));
        var unknownBranch = await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(N("Bad"), null, ["Sales.View"], RoleScope.Branch, Guid.NewGuid()));

        await AssertFieldAsync(companyOnly, "permissionCodes");
        await AssertFieldAsync(noBranch, "branchId");
        await AssertFieldAsync(companyWithBranch, "branchId");
        await AssertFieldAsync(unknownBranch, "branchId");
    }

    [Fact]
    public async Task BranchRole_AcceptsEveryBranchEligibleCode_AndDuplicateKeepsScopeAndBranch()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var eligible = Sellora.Domain.Permissions.PermissionCatalog.All.Where(p => Sellora.Domain.Permissions.PermissionCatalog.IsBranchEligible(p.Code)).Select(p => p.Code).ToArray();
        Assert.Contains("Sales.View", eligible);
        Assert.DoesNotContain("Reports.Profit", eligible); // Q2: conservative list

        var role = await RoleAsync(w, N("AllEligible"), eligible, RoleScope.Branch, b1);
        var copy = await JsonAsync(await Send(w, HttpMethod.Post, $"/api/v1/roles/{role}/duplicate"));

        Assert.Equal(("Branch", b1), (copy.GetProperty("scopeType").GetString(), copy.GetProperty("branchId").GetGuid()));
        Assert.Equal(eligible.Order(), copy.GetProperty("permissionCodes").EnumerateArray().Select(c => c.GetString()!).Order());
    }

    [Fact]
    public async Task Role_ScopeAndBranchAreImmutable_Is400OnScopeTypeOrBranchId()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var (b2, _) = await w.NewBranchAsync();
        var branchRole = await RoleAsync(w, N("BR"), ["Sales.View"], RoleScope.Branch, b1);
        var companyRole = await RoleAsync(w, N("CR"), ["Sales.View"]);

        var toCompany = await Send(w, HttpMethod.Put, $"/api/v1/roles/{branchRole}", new UpdateRoleRequest(N("BR"), null, ["Sales.View"], RoleScope.Company, null));
        var otherBranch = await Send(w, HttpMethod.Put, $"/api/v1/roles/{branchRole}", new UpdateRoleRequest(N("BR"), null, ["Sales.View"], RoleScope.Branch, b2));
        var toBranch = await Send(w, HttpMethod.Put, $"/api/v1/roles/{companyRole}", new UpdateRoleRequest(N("CR"), null, ["Sales.View"], RoleScope.Branch, b1));
        var sameScope = await Send(w, HttpMethod.Put, $"/api/v1/roles/{branchRole}", new UpdateRoleRequest(N("BR"), null, ["Sales.View", "Sales.Create"], RoleScope.Branch, b1));

        await AssertFieldAsync(toCompany, "scopeType");
        await AssertFieldAsync(otherBranch, "branchId");
        await AssertFieldAsync(toBranch, "scopeType");
        Assert.Equal(HttpStatusCode.OK, sameScope.StatusCode);
    }

    [Fact]
    public async Task BranchRole_Assignment_NeedsAnAssignedBranchesMemberAllocatedToThatBranch()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var (b2, _) = await w.NewBranchAsync();
        var role = await RoleAsync(w, N("B1Sales"), ["Sales.View"], RoleScope.Branch, b1);
        var wide = await MemberAsync(w, await EmptyRoleAsync(w));
        var atB2 = await MemberAsync(w, await EmptyRoleAsync(w), CompanyAccessScope.AssignedBranches, [b2], b2);
        var atB1 = await MemberAsync(w, await EmptyRoleAsync(w), CompanyAccessScope.AssignedBranches, [b1], b1);

        await AssertFieldAsync(await AssignAsync(w, wide.Id, role), "branchId");
        await AssertFieldAsync(await AssignAsync(w, atB2.Id, role), "branchId");
        await AssignOkAsync(w, atB1.Id, role);
    }

    // ---- T2 / T5 / T6: effective semantics and narrowing ------------------------------------------------------------------------------------

    private static async Task<List<Guid>> SaleIdsAsync(BranchApiWorld w, HttpClient client, Guid? branch)
    {
        var response = await Send(w, HttpMethod.Get, "/api/v1/sales?pageSize=100", client: client, branch: branch);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
    }

    [Fact]
    public async Task BranchRoleAtB1_SeesOnlyB1Invoices_AndB2IsForbiddenOrNotFound_EvenThoughAllocatedToBoth()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var (b2, _) = await w.NewBranchAsync();
        var sale1 = (await JsonAsync(await w.SaleAsync(b1))).GetProperty("id").GetGuid();
        var sale2 = (await JsonAsync(await w.SaleAsync(b2))).GetProperty("id").GetGuid();
        var user = await MemberAsync(w, await EmptyRoleAsync(w), CompanyAccessScope.AssignedBranches, [b1, b2], b1);
        await AssignOkAsync(w, user.Id, await RoleAsync(w, N("B1Viewer"), ["Sales.View"], RoleScope.Branch, b1));

        var viaHeader = await SaleIdsAsync(w, user.Client, b1);
        var viaPrimary = await SaleIdsAsync(w, user.Client, null);

        Assert.Contains(sale1, viaHeader);
        Assert.DoesNotContain(sale2, viaHeader);
        Assert.Equal(viaHeader.Order(), viaPrimary.Order());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: user.Client, branch: b2)).StatusCode); // no code at B2
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, $"/api/v1/sales/{sale2}", client: user.Client, branch: b2)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w, HttpMethod.Get, $"/api/v1/sales/{sale2}", client: user.Client, branch: b1)).StatusCode); // narrowed: B2 documents do not exist for this role
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, $"/api/v1/sales/{sale1}", client: user.Client, branch: b1)).StatusCode);
    }

    [Fact]
    public async Task BranchRole_GrantsNothingAtOtherBranches_ActionsToo()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var (b2, _) = await w.NewBranchAsync();
        var user = await MemberAsync(w, await EmptyRoleAsync(w), CompanyAccessScope.AssignedBranches, [b1, b2], b1);
        await AssignOkAsync(w, user.Id, await RoleAsync(w, N("B1Cashier"), ["Sales.View", "Sales.Create"], RoleScope.Branch, b1));

        var atB1 = await w.SaleAsync(b1);
        var atB2 = await Send(w, HttpMethod.Post, "/api/v1/sales", new CreateSaleRequest(w.CustomerId, Today, PaymentMethod.Cash, 0m, [new CreateSaleLineRequest(w.ProductId, w.ProductUnitId, 1m, 20m, 0)], null), user.Client, b2);
        var atB1AsUser = await Send(w, HttpMethod.Post, "/api/v1/sales", new CreateSaleRequest(w.CustomerId, Today, PaymentMethod.Cash, 0m, [new CreateSaleLineRequest(w.ProductId, w.ProductUnitId, 1m, 20m, 0)], null), user.Client, b1);

        Assert.True(atB1.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, atB2.StatusCode);
        Assert.True(atB1AsUser.IsSuccessStatusCode, await atB1AsUser.Content.ReadAsStringAsync());
        Assert.Equal(b1, (await JsonAsync(atB1AsUser)).GetProperty("branchId").GetGuid());
    }

    [Fact]
    public async Task CompanyRole_WithAssignedBranchesMembership_AppliesAtEveryAllocatedBranch_ButNotAtUnallocatedOnes()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var (b2, _) = await w.NewBranchAsync();
        var (b3, _) = await w.NewBranchAsync();
        var user = await MemberAsync(w, await RoleAsync(w, N("Viewer"), ["Sales.View"]), CompanyAccessScope.AssignedBranches, [b1, b2], b1);

        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: user.Client, branch: b1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: user.Client, branch: b2)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: user.Client, branch: b3)).StatusCode); // branch_access_denied
    }

    [Fact]
    public async Task MultipleRoles_AreUnioned_CompanyAndBranchTogether_AndAllocationAloneGrantsNothing()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var (b2, _) = await w.NewBranchAsync();
        var user = await MemberAsync(w, await RoleAsync(w, N("Buyer"), ["Purchase.View"]), CompanyAccessScope.AssignedBranches, [b1, b2], b1);
        var alloneOnly = await MemberAsync(w, await EmptyRoleAsync(w), CompanyAccessScope.AssignedBranches, [b1], b1);
        await AssignOkAsync(w, user.Id, await RoleAsync(w, N("B1Sales"), ["Sales.View"], RoleScope.Branch, b1));

        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/purchases", client: user.Client, branch: b2)).StatusCode); // company role: every allocated branch
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: user.Client, branch: b1)).StatusCode);   // branch role at B1
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: user.Client, branch: b2)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: alloneOnly.Client, branch: b1)).StatusCode);
    }

    [Fact]
    public async Task LoginResponse_CarriesPermissionsOfTheWorkingBranchAndTheRolesList_NotTokenClaims()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var (b2, _) = await w.NewBranchAsync();
        var username = N("u");
        var companyRole = await RoleAsync(w, N("Buyer"), ["Purchase.View"]);
        var created = await Send(w, HttpMethod.Post, "/api/v1/users", new CreateUserRequest("T", "U", username, username + "@selloramart.in", null, Pw, Pw, companyRole, CompanyAccessScope.AssignedBranches, [b1, b2], b2));
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var branchRole = await RoleAsync(w, N("B2Sales"), ["Sales.View"], RoleScope.Branch, b2);
        var other = await RoleAsync(w, N("B1Sales"), ["Sales.Create"], RoleScope.Branch, b1);
        await AssignOkAsync(w, id, branchRole);
        await AssignOkAsync(w, id, other);

        var auth = await w.Host.LoginAsync(username, Pw);

        Assert.Equal(["Purchase.View", "Sales.View"], auth.User.Permissions.Order().ToArray()); // working branch = primary B2: company codes + B2 codes only
        Assert.Equal(3, auth.User.Roles!.Length);
        Assert.Contains(auth.User.Roles, r => r.ScopeType == RoleScope.Branch && r.BranchId == b2);
        Assert.Equal(companyRole.ToString(), auth.User.Roles.Single(r => r.ScopeType == RoleScope.Company).Id.ToString());
        Assert.False(string.IsNullOrEmpty(auth.User.RoleName));
        Assert.Null(ApiHost.ClaimOf(auth.AccessToken, "permission"));
        Assert.Null(ApiHost.ClaimOf(auth.AccessToken, "role_name"));
    }

    // ---- T2: branch administrators ---------------------------------------------------------------------------------------------------

    private sealed record BranchAdmin(Member User, Guid B1, Guid B2, Guid B1Role, Guid B2Role);

    private static async Task<BranchAdmin> BranchAdminAsync(BranchApiWorld w)
    {
        var (b1, _) = await w.NewBranchAsync();
        var (b2, _) = await w.NewBranchAsync();
        var b1Role = await RoleAsync(w, N("B1Admin"), BranchAdminCodes, RoleScope.Branch, b1);
        var b2Role = await RoleAsync(w, N("B2Admin"), BranchAdminCodes, RoleScope.Branch, b2);
        var user = await MemberAsync(w, await EmptyRoleAsync(w), CompanyAccessScope.AssignedBranches, [b1], b1);
        await AssignOkAsync(w, user.Id, b1Role);
        // the role was assigned after login: sign in again so the response lists the new permissions (the API itself re-evaluates per request)
        var auth = await w.Host.LoginAsync(user.Username, Pw);
        return new BranchAdmin(user with { Client = w.Host.NewClient(auth.AccessToken) }, b1, b2, b1Role, b2Role);
    }

    [Fact]
    public async Task BranchAdmin_ListsAndReadsOnlyRolesOfItsOwnBranch_OtherBranchesAre404()
    {
        var w = await WorldAsync();
        var a = await BranchAdminAsync(w);

        var own = await Send(w, HttpMethod.Get, $"/api/v1/roles?scope=Branch&branchId={a.B1}", client: a.User.Client);
        var other = await Send(w, HttpMethod.Get, $"/api/v1/roles?scope=Branch&branchId={a.B2}", client: a.User.Client);
        var allBranch = await JsonAsync(await Send(w, HttpMethod.Get, "/api/v1/roles?scope=Branch", client: a.User.Client));

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Contains((await JsonAsync(own)).EnumerateArray(), r => r.GetProperty("id").GetGuid() == a.B1Role);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.DoesNotContain(allBranch.EnumerateArray(), r => r.GetProperty("branchId").GetGuid() == a.B2);
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, $"/api/v1/roles/{a.B1Role}", client: a.User.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w, HttpMethod.Get, $"/api/v1/roles/{a.B2Role}", client: a.User.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, "/api/v1/roles", client: a.User.Client)).StatusCode); // company roles: no company-level Roles.View
    }

    [Fact]
    public async Task BranchAdmin_CannotEditDuplicateDeactivateOrCreateRolesOfAnotherBranch()
    {
        var w = await WorldAsync();
        var a = await BranchAdminAsync(w);

        var put = await Send(w, HttpMethod.Put, $"/api/v1/roles/{a.B2Role}", new UpdateRoleRequest("x", null, ["Sales.View"]), a.User.Client);
        var dup = await Send(w, HttpMethod.Post, $"/api/v1/roles/{a.B2Role}/duplicate", client: a.User.Client);
        var status = await Send(w, HttpMethod.Post, $"/api/v1/roles/{a.B2Role}/status", new SetRoleActiveRequest(false), a.User.Client);
        var create = await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(N("X"), null, ["Sales.View"], RoleScope.Branch, a.B2), a.User.Client);

        Assert.All(new[] { put, dup, status, create }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
    }

    [Fact]
    public async Task BranchAdmin_ManagesRolesOfItsBranch_WithinItsOwnCodes_AndNeverCompanyRoles()
    {
        var w = await WorldAsync();
        var a = await BranchAdminAsync(w);

        var ok = await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(N("B1Clerk"), null, ["Sales.View"], RoleScope.Branch, a.B1), a.User.Client);
        var notHeld = await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(N("B1Greedy"), null, ["Sales.View", "Sales.Cancel"], RoleScope.Branch, a.B1), a.User.Client);
        var companyOnly = await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(N("B1Wide"), null, ["Users.View"], RoleScope.Branch, a.B1), a.User.Client);
        var companyRole = await Send(w, HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(N("Wide"), null, ["Sales.View"]), a.User.Client);
        var created = await JsonAsync(ok);

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        await AssertFieldAsync(notHeld, "permissionCodes");
        await AssertFieldAsync(companyOnly, "permissionCodes");
        await AssertCodeAsync(companyRole, HttpStatusCode.Forbidden, "role_management_forbidden");
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Put, $"/api/v1/roles/{id}", new UpdateRoleRequest(N("B1Clerk"), null, ["Sales.View", "Sales.Create"]), a.User.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(w, HttpMethod.Post, $"/api/v1/roles/{id}/duplicate", client: a.User.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(w, HttpMethod.Post, $"/api/v1/roles/{id}/status", new SetRoleActiveRequest(false), a.User.Client)).StatusCode);
    }

    [Fact]
    public async Task BranchAdmin_AssignsBranchRolesOfItsBranchToMembersAllocatedThere_NothingElse()
    {
        var w = await WorldAsync();
        var a = await BranchAdminAsync(w);
        var clerkRole = await RoleAsync(w, N("B1Clerk"), ["Sales.View"], RoleScope.Branch, a.B1);
        var atB1 = await MemberAsync(w, await EmptyRoleAsync(w), CompanyAccessScope.AssignedBranches, [a.B1], a.B1);
        var atB2 = await MemberAsync(w, await EmptyRoleAsync(w), CompanyAccessScope.AssignedBranches, [a.B2], a.B2);
        var companyRole = await RoleAsync(w, N("Viewer"), ["Sales.View"]);

        await AssignOkAsync(w, atB1.Id, clerkRole, a.User.Client);
        await AssertFieldAsync(await AssignAsync(w, atB2.Id, clerkRole, a.User.Client), "branchId"); // not allocated at B1
        Assert.Equal(HttpStatusCode.NotFound, (await AssignAsync(w, atB2.Id, a.B2Role, a.User.Client)).StatusCode); // B2 role unreachable
        await AssertCodeAsync(await AssignAsync(w, atB1.Id, companyRole, a.User.Client), HttpStatusCode.Forbidden, "role_assignment_forbidden");
        await AssertCodeAsync(await AssignAsync(w, atB1.Id, await AdministratorRoleIdAsync(w), a.User.Client), HttpStatusCode.Forbidden, "role_assignment_forbidden");
    }

    [Fact]
    public async Task BranchAdmin_SeesOnlyItsBranchRolesInAUsersAssignmentList_AndOnlyItsBranchMembers()
    {
        var w = await WorldAsync();
        var a = await BranchAdminAsync(w);
        var target = await MemberAsync(w, await EmptyRoleAsync(w), CompanyAccessScope.AssignedBranches, [a.B1, a.B2], a.B1);
        await AssignOkAsync(w, target.Id, await RoleAsync(w, N("B1Clerk"), ["Sales.View"], RoleScope.Branch, a.B1));
        await AssignOkAsync(w, target.Id, await RoleAsync(w, N("B2Clerk"), ["Sales.View"], RoleScope.Branch, a.B2));

        var view = await JsonAsync(await Send(w, HttpMethod.Get, $"/api/v1/users/{target.Id}/roles", client: a.User.Client));
        var members = await Send(w, HttpMethod.Get, $"/api/v1/branches/{a.B1}/members", client: a.User.Client);
        var otherMembers = await Send(w, HttpMethod.Get, $"/api/v1/branches/{a.B2}/members", client: a.User.Client);

        var visible = view.GetProperty("current").EnumerateArray().ToList();
        Assert.All(visible, r => Assert.Equal(a.B1, r.GetProperty("branchId").GetGuid())); // company roles and B2 roles are not shown to a branch administrator
        Assert.Single(visible);
        Assert.Contains((await JsonAsync(members)).EnumerateArray(), m => m.GetProperty("userId").GetGuid() == target.Id);
        Assert.Equal(HttpStatusCode.NotFound, otherMembers.StatusCode);
    }

    [Fact]
    public async Task BranchAdmin_CanEndAssignmentsOfItsBranchOnly()
    {
        var w = await WorldAsync();
        var a = await BranchAdminAsync(w);
        var target = await MemberAsync(w, await EmptyRoleAsync(w), CompanyAccessScope.AssignedBranches, [a.B1, a.B2], a.B1);
        var b1Row = await AssignOkAsync(w, target.Id, await RoleAsync(w, N("B1Clerk"), ["Sales.View"], RoleScope.Branch, a.B1));
        var b2Row = await AssignOkAsync(w, target.Id, await RoleAsync(w, N("B2Clerk"), ["Sales.View"], RoleScope.Branch, a.B2));

        var other = await Send(w, HttpMethod.Post, $"/api/v1/users/{target.Id}/roles/{b2Row}/end", new EndRoleAssignmentRequest("no"), a.User.Client);
        var own = await Send(w, HttpMethod.Post, $"/api/v1/users/{target.Id}/roles/{b1Row}/end", new EndRoleAssignmentRequest("done"), a.User.Client);

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var history = (await RolesOfAsync(w, target.Id)).GetProperty("history").EnumerateArray().ToList();
        Assert.Contains(history, h => h.GetProperty("id").GetGuid() == b1Row && h.GetProperty("status").GetString() == "Ended" && h.GetProperty("endReason").GetString() == "done");
    }

    // ---- T7: immediate revocation (same token) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task EditingARolesPermissions_RevokesAccessOnTheHoldersNextRequest_WithTheSameToken()
    {
        var w = await WorldAsync();
        var role = await RoleAsync(w, N("Seller"), ["Sales.View", "Purchase.View"]);
        var holder = await MemberAsync(w, role);
        var bystander = await MemberAsync(w, await RoleAsync(w, N("Other"), ["Sales.View"]));
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: holder.Client)).StatusCode);
        var versionBefore = await ScalarAsync<long>(w.Host.Database, $"""SELECT "AccessVersion" FROM "UserCompanyMemberships" WHERE "UserId" = '{bystander.Id}'""");

        var edit = await Send(w, HttpMethod.Put, $"/api/v1/roles/{role}", new UpdateRoleRequest(N("Seller"), null, ["Purchase.View"]));

        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: holder.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/purchases", client: holder.Client)).StatusCode);
        Assert.Equal(versionBefore, await ScalarAsync<long>(w.Host.Database, $"""SELECT "AccessVersion" FROM "UserCompanyMemberships" WHERE "UserId" = '{bystander.Id}'""")); // only holders are bumped
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: bystander.Client)).StatusCode);
    }

    [Fact]
    public async Task EndingAnAssignment_RevokesAccessOnTheNextRequest_WithTheSameToken()
    {
        var w = await WorldAsync();
        var holder = await MemberAsync(w, await RoleAsync(w, N("Seller"), ["Sales.View"]));
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: holder.Client)).StatusCode);
        var row = (await RolesOfAsync(w, holder.Id)).GetProperty("current").EnumerateArray().Single().GetProperty("id").GetGuid();

        var end = await Send(w, HttpMethod.Post, $"/api/v1/users/{holder.Id}/roles/{row}/end", new EndRoleAssignmentRequest("left the team"));

        Assert.Equal(HttpStatusCode.OK, end.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: holder.Client)).StatusCode);
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'role_assignment.ended' AND "SubjectUserId" = '{holder.Id}'"""));
    }

    [Fact]
    public async Task EndingAnAssignment_NeedsAReason_AndUnknownOrAlreadyEndedAssignmentsAre404()
    {
        var w = await WorldAsync();
        var holder = await MemberAsync(w, await RoleAsync(w, N("Seller"), ["Sales.View"]));
        var row = (await RolesOfAsync(w, holder.Id)).GetProperty("current").EnumerateArray().Single().GetProperty("id").GetGuid();

        var noReason = await Send(w, HttpMethod.Post, $"/api/v1/users/{holder.Id}/roles/{row}/end", new { reason = "" });
        await Send(w, HttpMethod.Post, $"/api/v1/users/{holder.Id}/roles/{row}/end", new EndRoleAssignmentRequest("first"));
        var again = await Send(w, HttpMethod.Post, $"/api/v1/users/{holder.Id}/roles/{row}/end", new EndRoleAssignmentRequest("second"));
        var unknown = await Send(w, HttpMethod.Post, $"/api/v1/users/{holder.Id}/roles/{Guid.NewGuid()}/end", new EndRoleAssignmentRequest("x"));

        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task DeactivatedRole_GrantsNothingOnTheNextRequest_AndReactivatingRestoresIt()
    {
        var w = await WorldAsync();
        var role = await RoleAsync(w, N("Seller"), ["Sales.View"]);
        var holder = await MemberAsync(w, role);
        var admin2 = w.Admin;
        // a role with holders cannot be deactivated; disable the only holder first (the rule counts non-disabled holders), then bring the user back
        Assert.Equal(HttpStatusCode.NoContent, (await Send(w, HttpMethod.Post, $"/api/v1/users/{holder.Id}/status", new SetUserStatusRequest(UserStatus.Disabled), admin2)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(w, HttpMethod.Post, $"/api/v1/roles/{role}/status", new SetRoleActiveRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(w, HttpMethod.Post, $"/api/v1/users/{holder.Id}/status", new SetUserStatusRequest(UserStatus.Active), admin2)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: holder.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(w, HttpMethod.Post, $"/api/v1/roles/{role}/status", new SetRoleActiveRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: holder.Client)).StatusCode);
    }

    [Fact]
    public async Task EndingTheAllocationAtABranch_ClosesBranchRolesThere_AndRevokesAccessImmediately()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var (b2, _) = await w.NewBranchAsync();
        var user = await MemberAsync(w, await EmptyRoleAsync(w), CompanyAccessScope.AssignedBranches, [b1, b2], b1);
        var b1Row = await AssignOkAsync(w, user.Id, await RoleAsync(w, N("B1Sales"), ["Sales.View"], RoleScope.Branch, b1));
        var b2Row = await AssignOkAsync(w, user.Id, await RoleAsync(w, N("B2Sales"), ["Sales.View"], RoleScope.Branch, b2));
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: user.Client, branch: b2)).StatusCode);
        var access = await JsonAsync(await Send(w, HttpMethod.Get, $"/api/v1/users/{user.Id}/access"));
        var b2Allocation = access.GetProperty("current").EnumerateArray().Single(a => a.GetProperty("branchId").GetGuid() == b2).GetProperty("id").GetGuid();

        var end = await Send(w, HttpMethod.Post, $"/api/v1/users/{user.Id}/access/assignments/{b2Allocation}/end", new EndAssignmentRequest("moved"));

        Assert.True(end.IsSuccessStatusCode, await end.Content.ReadAsStringAsync());
        var view = await RolesOfAsync(w, user.Id);
        Assert.DoesNotContain(view.GetProperty("current").EnumerateArray(), r => r.GetProperty("id").GetGuid() == b2Row);
        Assert.Contains(view.GetProperty("current").EnumerateArray(), r => r.GetProperty("id").GetGuid() == b1Row);
        Assert.Contains(view.GetProperty("history").EnumerateArray(), r => r.GetProperty("id").GetGuid() == b2Row && r.GetProperty("status").GetString() == "Ended");
        Assert.NotEqual(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: user.Client, branch: b2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: user.Client, branch: b1)).StatusCode);
    }

    [Fact]
    public async Task SwitchingToCompanyWide_ClosesAllBranchRoleAssignments_CompanyRolesRemain()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var user = await MemberAsync(w, await RoleAsync(w, N("Buyer"), ["Purchase.View"]), CompanyAccessScope.AssignedBranches, [b1], b1);
        await AssignOkAsync(w, user.Id, await RoleAsync(w, N("B1Sales"), ["Sales.View"], RoleScope.Branch, b1));

        var switched = await Send(w, HttpMethod.Put, $"/api/v1/users/{user.Id}/access/scope", new SetAccessScopeRequest(CompanyAccessScope.CompanyWide, null, null, "promoted"));

        Assert.True(switched.IsSuccessStatusCode, await switched.Content.ReadAsStringAsync());
        var view = await RolesOfAsync(w, user.Id);
        Assert.DoesNotContain(view.GetProperty("current").EnumerateArray(), r => r.GetProperty("scopeType").GetString() == "Branch");
        Assert.Single(view.GetProperty("current").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: user.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/purchases", client: user.Client)).StatusCode);
    }

    [Fact]
    public async Task SuspendingAMembership_StopsAllAccessOnTheNextRequest()
    {
        var w = await WorldAsync();
        var user = await MemberAsync(w, await RoleAsync(w, N("Seller"), ["Sales.View"]));
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: user.Client)).StatusCode);

        var suspend = await Send(w, HttpMethod.Post, $"/api/v1/users/{user.Id}/access/status", new SetMembershipStatusRequest(MembershipStatus.Suspended, "hold", null, null, null));

        Assert.True(suspend.IsSuccessStatusCode, await suspend.Content.ReadAsStringAsync());
        Assert.NotEqual(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: user.Client)).StatusCode);
    }

    // ---- T9: governance and operational separation ----------------------------------------------------------------------------------------

    [Fact]
    public async Task GovernanceOwner_WithoutAnOperationalRole_HoldsNoOperationalCodes()
    {
        var w = await WorldAsync();
        var owner = await MemberAsync(w, await EmptyRoleAsync(w));
        await Phase6Support.AddGovernanceMemberAsync(w.Host, owner.Id, "Owner", DefaultCompany, Phase6Support.AllCodes);

        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/governance/members", client: owner.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: owner.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, "/api/v1/roles", client: owner.Client)).StatusCode);
    }

    [Fact]
    public async Task Administrator_WithoutGovernanceGrants_CannotGovern_ButManagesRoles()
    {
        var w = await WorldAsync();
        var admin = await MemberAsync(w, await AdministratorRoleIdAsync(w));

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w, HttpMethod.Get, "/api/v1/governance/members", client: admin.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/roles", client: admin.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/sales", client: admin.Client)).StatusCode);
    }

    [Fact]
    public async Task RolePermissionDetail_NeverListsGovernanceCodes_AdministratorShowsTheWholeCatalogue()
    {
        var w = await WorldAsync();
        var administrator = await AdministratorRoleIdAsync(w);

        var detail = await JsonAsync(await Send(w, HttpMethod.Get, $"/api/v1/roles/{administrator}"));

        var codes = detail.GetProperty("permissionCodes").EnumerateArray().Select(c => c.GetString()!).ToArray();
        Assert.Equal(Sellora.Domain.Permissions.PermissionCatalog.All.Count, codes.Length);
        Assert.DoesNotContain(codes, c => c.StartsWith("Company.Governance."));
    }

    // ---- T12: compatibility paths ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Compat_CreateUserWithRoleId_WritesTheInitialCompanyRoleAssignment_AndUserDtoKeepsRoleIdAndRoleName()
    {
        var w = await WorldAsync();
        var roleName = N("Compat");
        var role = await RoleAsync(w, roleName, ["Sales.View"]);

        var member = await MemberAsync(w, role);

        var detail = await JsonAsync(await Send(w, HttpMethod.Get, $"/api/v1/users/{member.Id}"));
        Assert.Equal((role, roleName), (detail.GetProperty("roleId").GetGuid(), detail.GetProperty("roleName").GetString()));
        Assert.Equal(role, detail.GetProperty("roles").EnumerateArray().Single().GetProperty("id").GetGuid());
        Assert.Equal(1L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "UserRoleAssignments" a JOIN "UserCompanyMemberships" m ON m."Id" = a."UserCompanyMembershipId" WHERE m."UserId" = '{member.Id}' AND a."RoleId" = '{role}' AND a."Status" = 'Active' AND a."ScopeType" = 'Company'"""));
        Assert.True(await ScalarAsync<bool>(w.Host.Database, $"""SELECT "RoleId" IS NULL FROM "Users" WHERE "Id" = '{member.Id}'""")); // Users.RoleId is no longer written
        var list = await JsonAsync(await Send(w, HttpMethod.Get, $"/api/v1/users?roleId={role}"));
        Assert.Contains(list.GetProperty("items").EnumerateArray(), u => u.GetProperty("id").GetGuid() == member.Id);
    }

    [Fact]
    public async Task Compat_UpdateUserWithANewRoleId_ReplacesTheCompanyRole_KeepsBranchRoles_AndIsANoOpWhenUnchanged()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var first = await RoleAsync(w, N("First"), ["Sales.View"]);
        var second = await RoleAsync(w, N("Second"), ["Purchase.View"]);
        var user = await MemberAsync(w, first, CompanyAccessScope.AssignedBranches, [b1], b1);
        await AssignOkAsync(w, user.Id, await RoleAsync(w, N("B1Sales"), ["Sales.Create"], RoleScope.Branch, b1));
        UpdateUserRequest Edit(Guid role, string first_ = "Test") => new(first_, "User", user.Username, user.Username + "@selloramart.in", null, role);

        var unchanged = await Send(w, HttpMethod.Put, $"/api/v1/users/{user.Id}", Edit(first, "Renamed"));
        var versionAfterNoOp = await ScalarAsync<long>(w.Host.Database, $"""SELECT "AccessVersion" FROM "UserCompanyMemberships" WHERE "UserId" = '{user.Id}'""");
        var replaced = await Send(w, HttpMethod.Put, $"/api/v1/users/{user.Id}", Edit(second));

        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var view = await RolesOfAsync(w, user.Id);
        var current = view.GetProperty("current").EnumerateArray().Select(r => r.GetProperty("roleId").GetGuid()).ToList();
        Assert.Contains(second, current);
        Assert.DoesNotContain(first, current);
        Assert.Equal(2, current.Count); // the branch role stays
        Assert.Contains(view.GetProperty("history").EnumerateArray(), h => h.GetProperty("roleId").GetGuid() == first && h.GetProperty("status").GetString() == "Ended");
        Assert.True(await ScalarAsync<long>(w.Host.Database, $"""SELECT "AccessVersion" FROM "UserCompanyMemberships" WHERE "UserId" = '{user.Id}'""") > versionAfterNoOp);
        Assert.Equal(HttpStatusCode.OK, (await Send(w, HttpMethod.Get, "/api/v1/purchases", client: user.Client, branch: b1)).StatusCode);
    }

    [Fact]
    public async Task Compat_RoleIdFromAnotherScopeOrUnknown_IsRejectedOnRoleId()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var branchRole = await RoleAsync(w, N("B1"), ["Sales.View"], RoleScope.Branch, b1);
        var user = await MemberAsync(w, await EmptyRoleAsync(w));

        var asBranch = await Send(w, HttpMethod.Put, $"/api/v1/users/{user.Id}", new UpdateUserRequest("A", "B", user.Username, user.Username + "@selloramart.in", null, branchRole));
        var unknown = await Send(w, HttpMethod.Put, $"/api/v1/users/{user.Id}", new UpdateUserRequest("A", "B", user.Username, user.Username + "@selloramart.in", null, Guid.NewGuid()));
        var createUnknown = await Send(w, HttpMethod.Post, "/api/v1/users", new CreateUserRequest("T", "U", N("x"), N("y") + "@selloramart.in", null, Pw, Pw, Guid.NewGuid()));

        await AssertFieldAsync(asBranch, "roleId");
        await AssertFieldAsync(unknown, "roleId");
        await AssertFieldAsync(createUnknown, "roleId");
    }

    [Fact]
    public async Task Compat_RolesListDefaultsToCompanyRoles_SoTheExistingUserFormKeepsWorking()
    {
        var w = await WorldAsync();
        var (b1, _) = await w.NewBranchAsync();
        var branchRole = await RoleAsync(w, N("B1Only"), ["Sales.View"], RoleScope.Branch, b1);

        var company = await JsonAsync(await Send(w, HttpMethod.Get, "/api/v1/roles"));
        var branch = await JsonAsync(await Send(w, HttpMethod.Get, "/api/v1/roles?scope=Branch"));

        Assert.All(company.EnumerateArray(), r => Assert.Equal("Company", r.GetProperty("scopeType").GetString()));
        Assert.DoesNotContain(company.EnumerateArray(), r => r.GetProperty("id").GetGuid() == branchRole);
        Assert.Contains(branch.EnumerateArray(), r => r.GetProperty("id").GetGuid() == branchRole);
    }

    // ---- reference data (D11) ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CompanyUnitsAndCurrenciesWrites_AreRefused403PlatformManaged_ReadsStayOpen()
    {
        var w = await WorldAsync();
        var code = N("Z")[..6];

        var createUnit = await Send(w, HttpMethod.Post, "/api/v1/units", new UpsertUnitRequest("Unit " + code, code, MeasurementCategory.Count, 0, null));
        var createCurrency = await Send(w, HttpMethod.Post, "/api/v1/currencies", new UpsertCurrencyRequest("ZZZ", "Zed", "Z", 2));
        var someUnit = (await JsonAsync(await Send(w, HttpMethod.Get, "/api/v1/units"))).EnumerateArray().First().GetProperty("id").GetGuid();
        var updateUnit = await Send(w, HttpMethod.Put, $"/api/v1/units/{someUnit}", new UpsertUnitRequest("Renamed", "rn", MeasurementCategory.Count, 0, null));
        var statusUnit = await Send(w, HttpMethod.Post, $"/api/v1/units/{someUnit}/status", new SetActiveRequest(false));
        var someCurrency = (await JsonAsync(await Send(w, HttpMethod.Get, "/api/v1/currencies"))).EnumerateArray().First().GetProperty("id").GetGuid();
        var updateCurrency = await Send(w, HttpMethod.Put, $"/api/v1/currencies/{someCurrency}", new UpsertCurrencyRequest("QQQ", "Q", "Q", 2));
        var statusCurrency = await Send(w, HttpMethod.Post, $"/api/v1/currencies/{someCurrency}/status", new SetActiveRequest(false));

        foreach (var response in new[] { createUnit, createCurrency, updateUnit, statusUnit, updateCurrency, statusCurrency })
            await AssertCodeAsync(response, HttpStatusCode.Forbidden, "reference_data_platform_managed");
        Assert.Equal(0L, await ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "Units" WHERE "ShortCode" = '{code}'"""));
    }

    [Fact]
    public async Task CompanyUnitsAndCurrenciesWrites_WorkWhenThePlatformFlagAllowsCompanies()
    {
        await using var host = await ApiHost.StartAsync(postgres, new Dictionary<string, string> { ["Platform:ReferenceDataEditableByCompanies"] = "true" });
        using var admin = await host.LoginClientAsync();
        var code = N("Y")[..6];

        var unit = await admin.PostAsJsonAsync("/api/v1/units", new UpsertUnitRequest("Unit " + code, code, MeasurementCategory.Count, 0, null), ApiHost.Json);
        var currency = await admin.PostAsJsonAsync("/api/v1/currencies", new UpsertCurrencyRequest("YYY", "Why", "Y", 2), ApiHost.Json);

        Assert.Equal(HttpStatusCode.OK, unit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, currency.StatusCode);
    }

    [Fact]
    public async Task PlatformReferenceData_UnitsAndCurrencies_AreManagedWithThePlatformSession_AndCompanyTokensAreRefused()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        var ops = await Phase6Support.AddUserAsync(host, "ops.refdata", administrator: true, company: null);
        await Phase6Support.GrantPlatformAdminAsync(host, ops);
        using var platform = await Phase6Support.PlatformClientAsync(host, "ops.refdata");
        using var company = await host.LoginClientAsync();
        var code = N("P")[..6];

        var create = await platform.PostAsJsonAsync("/api/v1/platform/units", new UpsertUnitRequest("Platform " + code, code, MeasurementCategory.Count, 0, null), ApiHost.Json);
        var id = (await JsonAsync(create)).GetProperty("id").GetGuid();
        var update = await platform.PutAsJsonAsync($"/api/v1/platform/units/{id}", new UpsertUnitRequest("Platform " + code + "x", code, MeasurementCategory.Count, 0, null), ApiHost.Json);
        var deactivate = await platform.PostAsJsonAsync($"/api/v1/platform/units/{id}/status", new SetActiveRequest(false), ApiHost.Json);
        var currency = await platform.PostAsJsonAsync("/api/v1/platform/currencies", new UpsertCurrencyRequest("PPP", "Pee", "P", 2), ApiHost.Json);
        var all = await platform.GetAsync("/api/v1/platform/currencies/all");
        var asCompany = await company.GetAsync("/api/v1/platform/units");
        var asCompanyWrite = await company.PostAsJsonAsync("/api/v1/platform/units", new UpsertUnitRequest("X", "xx", MeasurementCategory.Count, 0, null), ApiHost.Json);

        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.True(deactivate.IsSuccessStatusCode, await deactivate.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, currency.StatusCode);
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, asCompany.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, asCompanyWrite.StatusCode);
        Assert.Equal("false", (await ScalarAsync<bool>(host.Database, $"""SELECT "IsActive" FROM "Units" WHERE "Id" = '{id}'""")).ToString().ToLowerInvariant());
    }
}
