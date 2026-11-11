using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.Phase6Support;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Phase 7 T11 and T14 over HTTP: company provisioning with the flag combinations (creation x reference-data editability), the new company's own Administrator and owner
/// assignment (Q4: nothing else), and a user shared by two companies holding different roles in each (login, switch, per-company evaluation).
/// </summary>
[Collection(PostgresCollection.Name)]
public class ScopedProvisioningApiTests(PostgresFixture postgres)
{
    private static Dictionary<string, string> Flags(bool creation, bool referenceDataEditable) => new()
    {
        ["Platform:CompanyCreationEnabled"] = creation ? "true" : "false",
        ["Platform:ReferenceDataEditableByCompanies"] = referenceDataEditable ? "true" : "false",
    };

    private async Task<(ApiHost Host, HttpClient Platform, Guid Inr)> StartAsync(Dictionary<string, string> settings)
    {
        var host = await ApiHost.StartAsync(postgres, settings);
        var ops = await AddUserAsync(host, "ops.alice", administrator: true, company: null);
        await GrantPlatformAdminAsync(host, ops);
        return (host, await PlatformClientAsync(host, "ops.alice"), await InrIdAsync(host));
    }

    [Theory]
    [InlineData(true, true, "reference_data_company_editable")]
    [InlineData(false, true, "company_creation_disabled")]
    [InlineData(false, false, "company_creation_disabled")]
    public async Task CreateCompany_FlagCombinationsThatDoNotAllowProvisioning_Are403_AndCreateNothing(bool creation, bool referenceDataEditable, string code)
    {
        var (host, platform, inr) = await StartAsync(Flags(creation, referenceDataEditable));
        await using var disposeHost = host;
        using var disposePlatform = platform;
        var rolesBefore = await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "Roles" """);

        var response = await platform.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("ACME", inr, "asha"), ApiHost.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(code, await response.ProblemCodeAsync());
        Assert.Equal(1L, await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "Companies" """));
        Assert.Equal(rolesBefore, await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "Roles" """));
        Assert.Equal(0L, await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "Users" WHERE "Username" = 'asha'"""));
    }

    [Fact]
    public async Task CreateCompany_WhenAllowed_GivesTheCompanyItsOwnAdministratorAndTheOwnerAssignment_AndNothingElse()
    {
        var (host, platform, inr) = await StartAsync(Flags(creation: true, referenceDataEditable: false));
        await using var disposeHost = host;
        using var disposePlatform = platform;
        var defaultRoles = await QueryAsync(host.Database, $"""SELECT "Id"::text FROM "Roles" WHERE "CompanyId" = '{DefaultCompany}' ORDER BY 1""", r => r.GetString(0));

        var response = await platform.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("ACME", inr, "asha"), ApiHost.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await JsonAsync(response);
        var company = created.GetProperty("id").GetGuid();
        var owner = created.GetProperty("ownerUserId").GetGuid();
        var roles = await QueryAsync(host.Database, $"""SELECT "Id"::text, "Name", "IsSystem", "IsActive", "ScopeType", ("BranchId" IS NULL)::text FROM "Roles" WHERE "CompanyId" = '{company}'""",
            r => (Id: r.GetString(0), Name: r.GetString(1), System: r.GetBoolean(2), Active: r.GetBoolean(3), Scope: r.GetString(4), NoBranch: r.GetString(5)));
        var admin = Assert.Single(roles); // Q4: only the Administrator
        Assert.Equal(("Administrator", true, true, "Company", "true"), (admin.Name, admin.System, admin.Active, admin.Scope, admin.NoBranch));
        Assert.Equal(1L, await ScalarAsync<long>(host.Database, $"""
            SELECT COUNT(*) FROM "UserRoleAssignments" a JOIN "UserCompanyMemberships" m ON m."Id" = a."UserCompanyMembershipId"
            WHERE a."CompanyId" = '{company}' AND m."UserId" = '{owner}' AND a."RoleId" = '{admin.Id}' AND a."Status" = 'Active' AND a."ScopeType" = 'Company'
            """));
        Assert.Equal(1L, await ScalarAsync<long>(host.Database, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "CompanyId" = '{company}' AND "Action" = 'role_assignment.created' AND "SubjectUserId" = '{owner}'"""));
        Assert.Equal(defaultRoles, await QueryAsync(host.Database, $"""SELECT "Id"::text FROM "Roles" WHERE "CompanyId" = '{DefaultCompany}' ORDER BY 1""", r => r.GetString(0)));

        var login = await host.LoginAsync("asha", "Str0ng!Passw0rd1");
        Assert.Equal(company, login.User.CompanyId);
        Assert.Equal("Administrator", login.User.RoleName);
        Assert.Equal(PermissionCatalog.All.Count, login.User.Permissions.Length);
        using var asOwner = host.NewClient(login.AccessToken);
        var ownRoles = await JsonAsync(await asOwner.GetAsync("/api/v1/roles"));
        Assert.Equal([admin.Id], ownRoles.EnumerateArray().Select(r => r.GetProperty("id").GetGuid().ToString()).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await asOwner.GetAsync($"/api/v1/roles/{defaultRoles[0]}")).StatusCode); // company A's roles do not exist in B
    }

    [Fact]
    public async Task NewCompanyOwner_CanCreateRolesAndUsers_WithoutTouchingTheDefaultCompany()
    {
        var (host, platform, inr) = await StartAsync(Flags(creation: true, referenceDataEditable: false));
        await using var disposeHost = host;
        using var disposePlatform = platform;
        await platform.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("ACME", inr, "asha"), ApiHost.Json);
        using var owner = host.NewClient((await host.LoginAsync("asha", "Str0ng!Passw0rd1")).AccessToken);

        var role = await owner.PostAsJsonAsync("/api/v1/roles", new CreateRoleRequest("Cashier", null, ["Sales.View", "Sales.Create"]), ApiHost.Json);
        var sameNameInDefault = await ScalarAsync<long>(host.Database, $"""SELECT COUNT(*) FROM "Roles" WHERE "CompanyId" = '{DefaultCompany}' AND "Name" = 'Cashier'""");

        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        Assert.Equal(0L, sameNameInDefault);
    }

    // ---- T14: a user shared by two companies --------------------------------------------------------------------------------------------

    [Fact]
    public async Task SharedUser_HoldsDifferentRolesPerCompany_LoginUsesTheOldestMembership_SwitchEvaluatesTheOtherCompanyOnly()
    {
        var (host, platform, inr) = await StartAsync(Flags(creation: true, referenceDataEditable: false));
        await using var disposeHost = host;
        using var disposePlatform = platform;
        var created = await JsonAsync(await platform.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("ACME", inr, "asha"), ApiHost.Json));
        var companyB = created.GetProperty("id").GetGuid();
        using var adminA = await host.LoginClientAsync();
        var sellerRole = await JsonAsync(await adminA.PostAsJsonAsync("/api/v1/roles", new CreateRoleRequest("Seller", null, ["Sales.View"]), ApiHost.Json));
        var userResponse = await adminA.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("Sam", "Shared", "sam.shared", "sam.shared@selloramart.in", null, "Passw0rd!99", "Passw0rd!99", sellerRole.GetProperty("id").GetGuid()), ApiHost.Json);
        var sam = (await JsonAsync(userResponse)).GetProperty("id").GetGuid();
        // Sam also belongs to company B (added directly: platform provisioning refuses users of other companies) and is its Administrator there.
        await ExecuteAsync(host.Database, $"""
            INSERT INTO "UserCompanyMemberships" ("Id", "UserId", "CompanyId", "Status", "AccessScope", "CreatedAt") VALUES (gen_random_uuid(), '{sam}', '{companyB}', 'Active', 'CompanyWide', now() + interval '1 minute');
            {Sellora.IntegrationTests.TestData.RbacSeed.AssignSql(sam, companyB.ToString(), $"""(SELECT "Id" FROM "Roles" WHERE "CompanyId" = '{companyB}' AND "IsSystem")""")}
            """);

        var login = await host.LoginAsync("sam.shared", "Passw0rd!99");

        Assert.Equal(DefaultCompany, login.User.CompanyId.ToString());
        Assert.Equal("Seller", login.User.RoleName);
        Assert.Equal(["Sales.View"], login.User.Permissions.Order().ToArray());
        using var inA = host.NewClient(login.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await inA.GetAsync("/api/v1/sales")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inA.GetAsync("/api/v1/roles")).StatusCode); // no role management in A

        var switched = await inA.PostAsJsonAsync("/api/v1/auth/switch-company", new SwitchCompanyRequest(companyB, login.RefreshToken), ApiHost.Json);
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        var inBAuth = (await switched.Content.ReadFromJsonAsync<AuthResponse>(ApiHost.Json))!;
        Assert.Equal("Administrator", inBAuth.User.RoleName);
        Assert.Equal(PermissionCatalog.All.Count, inBAuth.User.Permissions.Length);
        using var inB = host.NewClient(inBAuth.AccessToken);
        var rolesInB = await JsonAsync(await inB.GetAsync("/api/v1/roles"));
        Assert.Equal(HttpStatusCode.OK, (await inB.GetAsync("/api/v1/sales")).StatusCode);
        Assert.DoesNotContain(rolesInB.EnumerateArray(), r => r.GetProperty("name").GetString() == "Seller"); // A's role does not exist in B
        Assert.Equal(HttpStatusCode.NotFound, (await inB.GetAsync($"/api/v1/roles/{sellerRole.GetProperty("id").GetGuid()}")).StatusCode);
        // the A token still evaluates A (per-request, per-company): role changes in B never leak into A
        Assert.Equal(HttpStatusCode.Forbidden, (await inA.GetAsync("/api/v1/roles")).StatusCode);
    }

    [Fact]
    public async Task Refresh_KeepsTheCompanyAndReEvaluatesItsRoles_AfterTheRoleWasEdited()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        using var admin = await host.LoginClientAsync();
        var role = await JsonAsync(await admin.PostAsJsonAsync("/api/v1/roles", new CreateRoleRequest("Refresher", null, ["Sales.View", "Purchase.View"]), ApiHost.Json));
        var roleId = role.GetProperty("id").GetGuid();
        await admin.PostAsJsonAsync("/api/v1/users", new CreateUserRequest("R", "U", "refresh.user", "refresh.user@selloramart.in", null, "Passw0rd!99", "Passw0rd!99", roleId), ApiHost.Json);
        var first = await host.LoginAsync("refresh.user", "Passw0rd!99");
        Assert.Equal(["Purchase.View", "Sales.View"], first.User.Permissions.Order().ToArray());
        await admin.PutAsJsonAsync($"/api/v1/roles/{roleId}", new UpdateRoleRequest("Refresher", null, ["Sales.View"]), ApiHost.Json);

        using var anonymous = host.NewClient();
        var refreshed = (await (await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken), ApiHost.Json)).Content.ReadFromJsonAsync<AuthResponse>(ApiHost.Json))!;

        Assert.Equal(["Sales.View"], refreshed.User.Permissions.ToArray());
        using var me = host.NewClient(refreshed.AccessToken);
        var meBody = await JsonAsync(await me.GetAsync("/api/v1/auth/me"));
        Assert.Equal(["Sales.View"], meBody.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToArray());
    }
}
