using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Auth;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.Phase6Support;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Phase 6 T1-T3 through the real pipeline on PostgreSQL: the platform session is separate from company sessions (own login, own claim, no company, no
/// permissions, no refresh, 30 minutes), a platform administrator without membership cannot reach company data and the other way round, revocation is
/// immediate, the company list shows no business data, provisioning (flag O1) is atomic and complete, concurrent creation of one code yields one 201
/// and one 400, suspension cuts the company's users off and reactivation restores them. The first execution of the platform surface on PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PlatformApiTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string> CreationOn = new() { ["Platform:CompanyCreationEnabled"] = "true" };

    private static async Task AssertCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await response.ProblemCodeAsync());
    }

    private static async Task<(ApiHost Host, Guid Ops)> StartWithOperatorAsync(PostgresFixture postgres, IReadOnlyDictionary<string, string>? settings = null)
    {
        var host = await ApiHost.StartAsync(postgres, settings);
        var ops = await AddUserAsync(host, "ops.alice", administrator: true, company: null);
        await GrantPlatformAdminAsync(host, ops);
        return (host, ops);
    }

    // ---- T1: separation ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlatformAdminWithoutMembership_NormalLogin_Is403NoCompanyAccess()
    {
        var (host, _) = await StartWithOperatorAsync(postgres);
        await using var disposeHost = host;
        using var client = host.NewClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("ops.alice", Password), ApiHost.Json);

        await AssertCodeAsync(response, HttpStatusCode.Forbidden, "no_company_access");
    }

    [Fact]
    public async Task PlatformLogin_IssuesAPlatformTokenWithoutCompanyOrPermissionsOrRefreshToken_ThirtyMinutes()
    {
        var (host, ops) = await StartWithOperatorAsync(postgres);
        await using var disposeHost = host;
        using var client = host.NewClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/platform/login", new PlatformLoginRequest("ops.alice@example.io", Password), ApiHost.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.False(body.TryGetProperty("refreshToken", out _));
        Assert.Equal(ops, body.GetProperty("user").GetProperty("id").GetGuid());
        var token = new JwtSecurityTokenHandler().ReadJwtToken(body.GetProperty("accessToken").GetString());
        Assert.Equal(AuthClaimTypes.PlatformSession, token.Claims.Single(c => c.Type == AuthClaimTypes.SessionKind).Value);
        Assert.DoesNotContain(token.Claims, c => c.Type is AuthClaimTypes.CompanyId or AuthClaimTypes.Permission);
        Assert.InRange(token.ValidTo - DateTime.UtcNow, TimeSpan.FromMinutes(28), TimeSpan.FromMinutes(31));
        Assert.Equal(1L, await ScalarAsync<long>(host.Database, $"""SELECT COUNT(*) FROM "PlatformAuditEvents" WHERE "Action" = 'platform.login' AND "ActorUserId" = '{ops}'"""));
        Assert.Equal(0L, await ScalarAsync<long>(host.Database, $"""SELECT COUNT(*) FROM "RefreshTokens" WHERE "UserId" = '{ops}'"""));
    }

    [Fact]
    public async Task PlatformLogin_Failures_MapToTheirCodes()
    {
        var (host, _) = await StartWithOperatorAsync(postgres);
        await using var disposeHost = host;
        await AddUserAsync(host, "ops.disabled", administrator: true, company: null, status: "Disabled");
        using var client = host.NewClient();
        Task<HttpResponseMessage> Login(string user, string password) => client.PostAsJsonAsync("/api/v1/auth/platform/login", new PlatformLoginRequest(user, password), ApiHost.Json);

        await AssertCodeAsync(await Login("ops.alice", "wrong"), HttpStatusCode.Unauthorized, "invalid_credentials");
        await AssertCodeAsync(await Login("nobody", Password), HttpStatusCode.Unauthorized, "invalid_credentials");
        await AssertCodeAsync(await Login("ops.disabled", Password), HttpStatusCode.Forbidden, "account_disabled");
        await AssertCodeAsync(await Login(ApiHost.AdminEmail, ApiHost.AdminPassword), HttpStatusCode.Forbidden, "platform_admin_required"); // the company administrator is no platform admin
    }

    [Theory]
    [InlineData("/api/v1/products")]
    [InlineData("/api/v1/sales")]
    [InlineData("/api/v1/branches")]
    [InlineData("/api/v1/auth/me")]
    [InlineData("/api/v1/governance/members")]
    public async Task PlatformToken_OnCompanyRoutes_Is401CompanyContextInvalid(string route)
    {
        var (host, _) = await StartWithOperatorAsync(postgres);
        await using var disposeHost = host;
        using var platform = await PlatformClientAsync(host, "ops.alice");

        await AssertCodeAsync(await platform.GetAsync(route), HttpStatusCode.Unauthorized, "company_context_invalid");
    }

    [Fact]
    public async Task CompanyToken_OnPlatformRoutes_Is401PlatformSessionRequired_AndAnonymousIs401()
    {
        var (host, _) = await StartWithOperatorAsync(postgres);
        await using var disposeHost = host;
        using var admin = await host.LoginClientAsync();
        using var anonymous = host.NewClient();

        await AssertCodeAsync(await admin.GetAsync("/api/v1/platform/companies"), HttpStatusCode.Unauthorized, PlatformCode);
        await AssertCodeAsync(await admin.GetAsync("/api/v1/platform/currencies"), HttpStatusCode.Unauthorized, PlatformCode);
        await AssertCodeAsync(await admin.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("X1", Guid.NewGuid(), "x1"), ApiHost.Json), HttpStatusCode.Unauthorized, PlatformCode);
        await AssertCodeAsync(await admin.PostAsJsonAsync($"/api/v1/platform/companies/{DefaultCompany}/status", new SetPlatformCompanyStatusRequest(false, "x"), ApiHost.Json), HttpStatusCode.Unauthorized, PlatformCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/platform/companies")).StatusCode);
        Assert.Equal(1L, await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "Companies" """));
    }

    private const string PlatformCode = "platform_session_required";

    [Fact]
    public async Task ForgedPlatformClaim_ForAUserWhoIsNotAPlatformAdmin_Is403PlatformAdminRequired()
    {
        var (host, _) = await StartWithOperatorAsync(postgres);
        await using var disposeHost = host;
        var adminId = await AdminUserIdAsync(host);
        var token = SignPlatformToken(adminId);
        using var client = host.NewClient(token);

        await AssertCodeAsync(await client.GetAsync("/api/v1/platform/companies"), HttpStatusCode.Forbidden, "platform_admin_required");
    }

    [Fact]
    public async Task RevokedPlatformAdmin_WithALiveToken_IsRefusedOnTheNextRequest_AndRegrantRestoresIt()
    {
        var (host, ops) = await StartWithOperatorAsync(postgres);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/platform/companies")).StatusCode);

        await RevokePlatformAdminAsync(host, ops);
        await AssertCodeAsync(await client.GetAsync("/api/v1/platform/companies"), HttpStatusCode.Forbidden, "platform_admin_required");

        await ExecuteAsync(host.Database, $"""UPDATE "PlatformAdministrators" SET "Status" = 'Active', "RevokedAt" = NULL WHERE "UserId" = '{ops}'""");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/platform/companies")).StatusCode);
    }

    [Fact]
    public async Task DisabledPlatformAdmin_WithALiveToken_IsRefusedOnTheNextRequest()
    {
        var (host, ops) = await StartWithOperatorAsync(postgres);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");

        await ExecuteAsync(host.Database, $"""UPDATE "Users" SET "Status" = 'Disabled' WHERE "Id" = '{ops}'""");

        await AssertCodeAsync(await client.GetAsync("/api/v1/platform/companies"), HttpStatusCode.Forbidden, "platform_admin_required");
    }

    [Fact]
    public async Task PlatformCompanyList_ShowsOnlyCompanyIdentityAndCounts_NoBusinessData()
    {
        var (host, _) = await StartWithOperatorAsync(postgres);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");

        var response = await client.GetAsync("/api/v1/platform/companies");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = (await JsonAsync(response)).EnumerateArray().ToList();
        var company = Assert.Single(list);
        Assert.Equal(["code", "createdAt", "id", "isActive", "memberCount", "name", "ownerCount"], company.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(DefaultCompany, company.GetProperty("id").GetString());
        Assert.Equal(1, company.GetProperty("ownerCount").GetInt32()); // the seeded administrator
        Assert.True(company.GetProperty("memberCount").GetInt32() >= 1);
        var currencies = await JsonAsync(await client.GetAsync("/api/v1/platform/currencies"));
        Assert.Contains(currencies.EnumerateArray(), c => c.GetProperty("code").GetString() == "INR");
    }

    [Fact]
    public async Task PlatformRoutes_NeverShowCompanyRows_EvenWithBusinessDataPresent()
    {
        var (host, _) = await StartWithOperatorAsync(postgres);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");

        var body = await (await client.GetAsync("/api/v1/platform/companies")).Content.ReadAsStringAsync();

        // the seeded default company owns branches, locations, accounts, products ... none of their ids or names may appear
        var branchId = await ScalarAsync<Guid>(host.Database, """SELECT "Id" FROM "Branches" LIMIT 1""");
        Assert.DoesNotContain(branchId.ToString(), body);
        Assert.DoesNotContain("STN-PEN", body);
        Assert.DoesNotContain("Main Cash", body);
    }

    // ---- T2: provisioning -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateCompany_WithTheFlagOff_Is403CompanyCreationDisabled_AndWritesNothing()
    {
        var (host, _) = await StartWithOperatorAsync(postgres);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var inr = await InrIdAsync(host);

        var response = await client.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("ACME", inr, "asha"), ApiHost.Json);

        await AssertCodeAsync(response, HttpStatusCode.Forbidden, "company_creation_disabled");
        Assert.Equal(1L, await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "Companies" """));
        Assert.Equal(0L, await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "PlatformAuditEvents" WHERE "Action" = 'platform.company_created'"""));
    }

    [Fact]
    public async Task CreateCompany_CreatesEveryRowAtomically_AndAuditsIt()
    {
        var (host, ops) = await StartWithOperatorAsync(postgres, CreationOn);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var inr = await InrIdAsync(host);

        var response = await client.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("ACME", inr, "asha"), ApiHost.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await JsonAsync(response);
        var company = created.GetProperty("id").GetGuid();
        var main = created.GetProperty("mainBranchId").GetGuid();
        var owner = created.GetProperty("ownerUserId").GetGuid();
        Assert.Equal("ACME", created.GetProperty("code").GetString());
        var db = host.Database;
        Assert.Equal(1L, await ScalarAsync<long>(db, $"""SELECT COUNT(*) FROM "Companies" WHERE "Id" = '{company}' AND "IsActive" """));
        Assert.Equal(1L, await ScalarAsync<long>(db, $"""SELECT COUNT(*) FROM "BusinessSettings" WHERE "CompanyId" = '{company}' AND "InvoicePrefix" = 'ACME' AND "BaseCurrencyId" = '{inr}'"""));
        Assert.Equal(1L, await ScalarAsync<long>(db, $"""SELECT COUNT(*) FROM "Branches" WHERE "CompanyId" = '{company}' AND "Id" = '{main}' AND "IsDefault" AND "IsActive" AND "Code" = 'MAIN'"""));
        Assert.Equal(1L, await ScalarAsync<long>(db, $"""SELECT COUNT(*) FROM "StockLocations" WHERE "CompanyId" = '{company}' AND "BranchId" = '{main}' AND "LocationType" = 'BranchStore'"""));
        Assert.Equal(1L, await ScalarAsync<long>(db, $"""SELECT COUNT(*) FROM "StockLocations" WHERE "CompanyId" = '{company}' AND "BranchId" IS NULL AND "LocationType" = 'Transit'"""));
        Assert.Equal(1L, await ScalarAsync<long>(db, $"""SELECT COUNT(*) FROM "FinancialAccounts" WHERE "CompanyId" = '{company}' AND "Name" = 'Main Cash' AND "Kind" = 'Cash'"""));
        Assert.Equal(1L, await ScalarAsync<long>(db, $"""SELECT COUNT(*) FROM "UserCompanyMemberships" WHERE "CompanyId" = '{company}' AND "UserId" = '{owner}' AND "Status" = 'Active' AND "AccessScope" = 'CompanyWide'"""));
        var grants = await QueryAsync(db, $"""
            SELECT gp."Code" FROM "CompanyGovernanceMembers" g JOIN "UserCompanyMemberships" m ON m."CompanyId" = g."CompanyId" AND m."Id" = g."UserCompanyMembershipId"
            JOIN "GovernanceMemberPermissions" p ON p."GovernanceMemberId" = g."Id" AND p."CompanyId" = g."CompanyId" JOIN "GovernancePermissions" gp ON gp."Id" = p."GovernancePermissionId"
            WHERE g."CompanyId" = '{company}' AND g."Title" = 'Owner' AND g."Status" = 'Active' AND m."UserId" = '{owner}' AND g."CreatedByUserId" = '{ops}'
            """, r => r.GetString(0));
        Assert.Equal(AllCodes.Order(), grants.Order());
        var audit = await QueryAsync(db, $"""SELECT "TargetId"::text, "ActorUserId"::text, "Reason", "CompanyId"::text FROM "PlatformAuditEvents" WHERE "Action" = 'platform.company_created'""", r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        Assert.Equal([(company.ToString(), ops.ToString(), "Onboarding", company.ToString())], audit);
        // no other company's data was touched: still one default company, one default branch
        Assert.Equal(1L, await ScalarAsync<long>(db, $"""SELECT COUNT(*) FROM "Branches" WHERE "CompanyId" = '{DefaultCompany}'"""));
    }

    [Fact]
    public async Task CreateCompany_TheNewOwnerLogsInSwitchesAndOwnsOnlyTheNewCompany_IsolationBothWays()
    {
        var (host, _) = await StartWithOperatorAsync(postgres, CreationOn);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var inr = await InrIdAsync(host);
        var created = await JsonAsync(await client.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("ACME", inr, "asha"), ApiHost.Json));
        var company = created.GetProperty("id").GetGuid();
        var main = created.GetProperty("mainBranchId").GetGuid();

        var login = await host.LoginAsync("asha", "Str0ng!Passw0rd1");

        Assert.Equal(company, login.User.CompanyId);
        using var owner = host.NewClient(login.AccessToken);
        var me = await JsonAsync(await owner.GetAsync("/api/v1/auth/me"));
        Assert.Equal(company, me.GetProperty("companyId").GetGuid());
        Assert.Equal("Owner", me.GetProperty("governance").GetProperty("title").GetString());
        Assert.Equal(AllCodes.Order(), me.GetProperty("governance").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).Order());
        // the owner sees only the new company's branch; the default company's administrator sees only the default company's
        var branches = (await JsonAsync(await owner.GetAsync("/api/v1/branches?includeInactive=true"))).EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([main], branches);
        using var admin = await host.LoginClientAsync();
        var adminBranches = (await JsonAsync(await admin.GetAsync("/api/v1/branches?includeInactive=true"))).EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(main, adminBranches);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/branches/{main}")).StatusCode);
        // switch-company is not offered: the owner belongs to the new company only
        Assert.Equal([company], (await JsonAsync(await owner.GetAsync("/api/v1/auth/me"))).GetProperty("companies").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList());
    }

    [Fact]
    public async Task CreateCompany_FirstExpenseInTheNewCompanyIsExp0001_WhileTheDefaultCompanyKeepsItsOwnSequence()
    {
        var (host, _) = await StartWithOperatorAsync(postgres, CreationOn);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var inr = await InrIdAsync(host);
        await client.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("ACME", inr, "asha"), ApiHost.Json);
        using var admin = await host.LoginClientAsync();
        using var owner = await host.LoginClientAsync("asha", "Str0ng!Passw0rd1");

        async Task<string> FirstExpenseAsync(HttpClient c, string category)
        {
            var cat = await JsonAsync(await c.PostAsJsonAsync("/api/v1/expense-categories", new UpsertExpenseCategoryRequest(category), ApiHost.Json));
            var response = await c.PostAsJsonAsync("/api/v1/expenses",
                new CreateExpenseRequest(cat.GetProperty("id").GetGuid(), DateOnly.FromDateTime(DateTime.UtcNow), inr, 10m, Sellora.Domain.Entities.PaymentMethod.Cash, null, null), ApiHost.Json);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await JsonAsync(response)).GetProperty("expenseNo").GetString()!;
        }

        Assert.Equal("EXP-0001", await FirstExpenseAsync(owner, "Rent"));
        Assert.Equal("EXP-0001", await FirstExpenseAsync(admin, "Phase6 Category"));
    }

    [Fact]
    public async Task CreateCompany_DuplicateOwnerUsername_Is400AndLeavesZeroRows()
    {
        var (host, _) = await StartWithOperatorAsync(postgres, CreationOn);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var inr = await InrIdAsync(host);
        var before = await CountsAsync(host);

        var response = await client.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("ACME", inr, "admin"), ApiHost.Json); // username "admin" exists

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("owner.newuser.username", (await response.Content.ReadAsStringAsync()).ToLowerInvariant());
        Assert.Equal(before, await CountsAsync(host));
    }

    [Fact]
    public async Task CreateCompany_ValidationErrors_MapToTheirFields()
    {
        var (host, _) = await StartWithOperatorAsync(postgres, CreationOn);
        await using var disposeHost = host;
        using var client = await PlatformClientAsync(host, "ops.alice");
        var inr = await InrIdAsync(host);

        var badCurrency = await client.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("ACME", Guid.NewGuid(), "asha"), ApiHost.Json);
        var badCode = await client.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("-", inr, "asha"), ApiHost.Json);
        var ownerSelf = await client.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("ACME", inr, "x", existingOwner: await ScalarAsync<Guid>(host.Database, """SELECT "Id" FROM "Users" WHERE "Username" = 'ops.alice'""")), ApiHost.Json);

        Assert.Contains("basecurrencyid", (await badCurrency.Content.ReadAsStringAsync()).ToLowerInvariant());
        Assert.Equal(HttpStatusCode.BadRequest, badCurrency.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badCode.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, ownerSelf.StatusCode);
        // an existing user who already belongs to another company (the seeded administrator) cannot be the owner
        var adminId = await ScalarAsync<Guid>(host.Database, $"""SELECT "Id" FROM "Users" WHERE lower("Email") = lower('{ApiHost.AdminEmail}')""");
        var ownerShared = await client.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("ACME", inr, "x", existingOwner: adminId), ApiHost.Json);
        Assert.Equal(HttpStatusCode.BadRequest, ownerShared.StatusCode);
        Assert.Contains("owner", (await ownerShared.Content.ReadAsStringAsync()).ToLowerInvariant());
        Assert.Equal(1L, await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "Companies" """));
    }

    [Fact]
    public async Task CreateCompany_ConcurrentRequestsForTheSameCode_OneCreatedOneRefused_NoPartialRows()
    {
        var (host, _) = await StartWithOperatorAsync(postgres, CreationOn);
        await using var disposeHost = host;
        var inr = await InrIdAsync(host);
        using var c1 = await PlatformClientAsync(host, "ops.alice");
        using var c2 = await PlatformClientAsync(host, "ops.alice");

        var responses = await Task.WhenAll(
            c1.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("RACE", inr, "owner.one"), ApiHost.Json),
            c2.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("RACE", inr, "owner.two"), ApiHost.Json));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.BadRequest], responses.Select(r => r.StatusCode).Order().ToArray());
        Assert.Equal(1L, await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "Companies" WHERE "Code" = 'RACE'"""));
        // every company has exactly one default branch, one Owner member and 5 grants: nothing half-written by the loser
        Assert.Empty(await QueryAsync(host.Database, """
            SELECT c."Code" FROM "Companies" c WHERE c."Code" = 'RACE' AND ((SELECT COUNT(*) FROM "Branches" b WHERE b."CompanyId" = c."Id" AND b."IsDefault") <> 1
              OR (SELECT COUNT(*) FROM "CompanyGovernanceMembers" g WHERE g."CompanyId" = c."Id" AND g."Title" = 'Owner') <> 1
              OR (SELECT COUNT(*) FROM "GovernanceMemberPermissions" p WHERE p."CompanyId" = c."Id") <> 5)
            """, r => r.GetString(0)));
        Assert.Equal(1L, await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "Users" WHERE "Username" IN ('owner.one', 'owner.two')"""));
        Assert.Equal(1L, await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "PlatformAuditEvents" WHERE "Action" = 'platform.company_created'"""));
    }

    // ---- T3: suspension ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SuspendingACompany_CutsItsUsersOff_AndReactivatingRestoresThem_BothAudited()
    {
        var (host, _) = await StartWithOperatorAsync(postgres, CreationOn);
        await using var disposeHost = host;
        using var platform = await PlatformClientAsync(host, "ops.alice");
        var inr = await InrIdAsync(host);
        var created = await JsonAsync(await platform.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("ACME", inr, "asha"), ApiHost.Json));
        var company = created.GetProperty("id").GetGuid();
        using var owner = await host.LoginClientAsync("asha", "Str0ng!Passw0rd1");
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/taxes")).StatusCode);

        var suspend = await platform.PostAsJsonAsync($"/api/v1/platform/companies/{company}/status", new SetPlatformCompanyStatusRequest(false, "unpaid"), ApiHost.Json);

        Assert.Equal(HttpStatusCode.NoContent, suspend.StatusCode);
        await AssertCodeAsync(await owner.GetAsync("/api/v1/taxes"), HttpStatusCode.Unauthorized, "company_context_invalid");
        using var anonymous = host.NewClient();
        await AssertCodeAsync(await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("asha", "Str0ng!Passw0rd1"), ApiHost.Json), HttpStatusCode.Forbidden, "no_company_access");
        Assert.False((await JsonAsync(await platform.GetAsync("/api/v1/platform/companies"))).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == company).GetProperty("isActive").GetBoolean());
        // the default company is unaffected
        using var admin = await host.LoginClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/taxes")).StatusCode);

        var reactivate = await platform.PostAsJsonAsync($"/api/v1/platform/companies/{company}/status", new SetPlatformCompanyStatusRequest(true, "paid"), ApiHost.Json);

        Assert.Equal(HttpStatusCode.NoContent, reactivate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/taxes")).StatusCode);
        Assert.Equal(["paid", "unpaid"], (await QueryAsync(host.Database, """SELECT "Reason" FROM "PlatformAuditEvents" WHERE "Action" = 'platform.company_status_changed'""", r => r.GetString(0))).Order().ToArray());
    }

    [Fact]
    public async Task SetCompanyStatus_UnknownCompanyIs404_MissingReasonIs400_AndRepeatIsANoOpWithoutANewAuditRow()
    {
        var (host, _) = await StartWithOperatorAsync(postgres);
        await using var disposeHost = host;
        using var platform = await PlatformClientAsync(host, "ops.alice");

        Assert.Equal(HttpStatusCode.NotFound, (await platform.PostAsJsonAsync($"/api/v1/platform/companies/{Guid.NewGuid()}/status", new SetPlatformCompanyStatusRequest(false, "x"), ApiHost.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync($"/api/v1/platform/companies/{DefaultCompany}/status", new SetPlatformCompanyStatusRequest(false, ""), ApiHost.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await platform.PostAsJsonAsync($"/api/v1/platform/companies/{DefaultCompany}/status", new SetPlatformCompanyStatusRequest(true, "already active"), ApiHost.Json)).StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "PlatformAuditEvents" WHERE "Action" = 'platform.company_status_changed'"""));
    }

    // ---- O3: the grant script is the only way in --------------------------------------------------------------------------------

    [Fact]
    public async Task GrantScript_ThenPlatformLogin_Works_EndToEnd()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        await AddUserAsync(host, "ops.bob", administrator: true, company: null);
        using var anonymous = host.NewClient();
        await AssertCodeAsync(await anonymous.PostAsJsonAsync("/api/v1/auth/platform/login", new PlatformLoginRequest("ops.bob", Password), ApiHost.Json), HttpStatusCode.Forbidden, "platform_admin_required");

        var script = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "scripts", "sql", "grant-platform-admin.sql"));
        var result = await postgres.RunPsqlScriptAsync(host.Database.Name, script, new Dictionary<string, string> { ["username"] = "ops.bob" });

        Assert.True(result.ExitCode == 0, result.Stderr);
        using var platform = await PlatformClientAsync(host, "ops.bob");
        Assert.Equal(HttpStatusCode.OK, (await platform.GetAsync("/api/v1/platform/companies")).StatusCode);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "sql", "grant-platform-admin.sql"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root not found");
    }

    private static string SignPlatformToken(Guid userId)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()), new(AuthClaimTypes.SessionKind, AuthClaimTypes.PlatformSession),
        };
        var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Convert.FromBase64String(SelloraApiFactory.SigningKey)), Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(SelloraApiFactory.Issuer, SelloraApiFactory.Audience, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: credentials));
    }

    private static async Task<string> CountsAsync(ApiHost host) => string.Join(",", await RowCountsAsync(host.Database,
        "Companies", "Users", "UserCompanyMemberships", "Branches", "StockLocations", "FinancialAccounts", "BusinessSettings", "CompanyGovernanceMembers", "GovernanceMemberPermissions", "PlatformAuditEvents"));
}
