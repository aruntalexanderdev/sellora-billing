using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Auth;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Security;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// T7 — company-aware authentication through the real HTTP pipeline (JWT bearer + CompanyContextMiddleware): tokens carry
/// <c>company_id</c>; the claim is never trusted without a live Active membership of an active company for an enabled user;
/// refresh and switch-company behave per design D2; users are scoped to the caller's company (L1).
/// </summary>
[Collection(PostgresCollection.Name)]
public class AuthCompanyApiTests(PostgresFixture postgres)
{
    private static readonly Guid CompanyB = Guid.Parse(OtherCompany);
    private static readonly Guid CompanyC = Guid.Parse("0c0a1f00-0000-4000-8000-0000000000c3");
    private const string ProbeRoute = "/api/v1/taxes"; // any company-scoped endpoint the admin may call

    private static async Task<Guid> AdminIdAsync(ApiHost host)
    {
        await using var db = host.Database.CreateDbContext();
        return await db.Users.Where(u => u.Email == ApiHost.AdminEmail).Select(u => u.Id).SingleAsync();
    }

    private static async Task AddCompanyAsync(ApiHost host, Guid id, string name, bool active = true)
    {
        await using var db = host.Database.CreateDbContext();
        db.Companies.Add(new Company { Id = id, Code = "C" + id.ToString("N")[^4..], Name = name, IsActive = active });
        await db.SaveChangesAsync();
    }

    private static async Task AddMembershipAsync(ApiHost host, Guid userId, Guid companyId, MembershipStatus status = MembershipStatus.Active)
    {
        await using var db = host.Database.CreateDbContext();
        db.UserCompanyMemberships.Add(new UserCompanyMembership { Id = Guid.NewGuid(), UserId = userId, CompanyId = companyId, Status = status });
        await db.SaveChangesAsync();
    }

    private static async Task<User> AddUserAsync(ApiHost host, string username, params Guid[] companies)
    {
        await using var db = host.Database.CreateDbContext();
        var role = await db.Roles.SingleAsync(r => r.IsSystem);
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = username, LastName = "Test", Username = username, Email = $"{username}@selloramart.in",
            PasswordHash = new PasswordHasher().Hash(ApiHost.AdminPassword), RoleId = role.Id, Status = UserStatus.Active,
        };
        db.Users.Add(user);
        db.UserCompanyMemberships.AddRange(companies.Select(c => new UserCompanyMembership { Id = Guid.NewGuid(), UserId = user.Id, CompanyId = c }));
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task AssertCompanyContextInvalidAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("company_context_invalid", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Login_ReturnsTokenWithCompanyIdClaimAndCompanyInTheUserSummary()
    {
        await using var host = await ApiHost.StartAsync(postgres);

        var auth = await host.LoginAsync();

        Assert.Equal(DefaultCompany, ApiHost.ClaimOf(auth.AccessToken, AuthClaimTypes.CompanyId));
        Assert.Equal(DefaultTenant.CompanyId, auth.User.CompanyId);
        Assert.False(string.IsNullOrWhiteSpace(auth.User.CompanyName));
        using var client = host.NewClient(auth.AccessToken);
        var me = await client.GetFromJsonAsync<MeResponse>("/api/v1/auth/me", ApiHost.Json);
        Assert.Equal([DefaultTenant.CompanyId], me!.Companies.Select(c => c.Id));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ProbeRoute)).StatusCode);
    }

    [Fact]
    public async Task Refresh_IssuesATokenForTheTokensCompany()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        var auth = await host.LoginAsync();
        using var anonymous = host.NewClient();

        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken), ApiHost.Json);

        response.EnsureSuccessStatusCode();
        var refreshed = (await response.Content.ReadFromJsonAsync<AuthResponse>(ApiHost.Json))!;
        Assert.Equal(DefaultCompany, ApiHost.ClaimOf(refreshed.AccessToken, AuthClaimTypes.CompanyId));
        Assert.Equal(DefaultTenant.CompanyId, await ScalarAsync<Guid>(host.Database, """SELECT "CompanyId" FROM "RefreshTokens" WHERE "RevokedAt" IS NULL ORDER BY "CreatedAt" DESC LIMIT 1"""));
    }

    [Fact]
    public async Task Refresh_LegacyTokenWithoutCompany_ResolvesTheDefaultMembership()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        var auth = await host.LoginAsync();
        await ExecuteAsync(host.Database, """UPDATE "RefreshTokens" SET "CompanyId" = NULL""");
        using var anonymous = host.NewClient();

        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken), ApiHost.Json);

        response.EnsureSuccessStatusCode();
        var refreshed = (await response.Content.ReadFromJsonAsync<AuthResponse>(ApiHost.Json))!;
        Assert.Equal(DefaultCompany, ApiHost.ClaimOf(refreshed.AccessToken, AuthClaimTypes.CompanyId));
    }

    [Fact]
    public async Task Request_WithSignedTokenWithoutCompanyClaim_Is401CompanyContextInvalid()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        var adminId = await AdminIdAsync(host);
        using var client = host.NewClient(ApiHost.SignToken(adminId, companyId: null, "Masters.View"));

        await AssertCompanyContextInvalidAsync(await client.GetAsync(ProbeRoute));
    }

    [Fact]
    public async Task Request_WithSignedTokenForACompanyTheUserIsNotAMemberOf_Is401()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        await AddCompanyAsync(host, CompanyB, "Company B");
        var adminId = await AdminIdAsync(host);
        using var existing = host.NewClient(ApiHost.SignToken(adminId, CompanyB, "Masters.View"));
        using var unknown = host.NewClient(ApiHost.SignToken(adminId, Guid.NewGuid(), "Masters.View"));

        await AssertCompanyContextInvalidAsync(await existing.GetAsync(ProbeRoute));
        await AssertCompanyContextInvalidAsync(await unknown.GetAsync(ProbeRoute));
    }

    [Fact]
    public async Task SuspendedMembership_Is401OnTheNextRequestAndRefreshFails()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        var auth = await host.LoginAsync();
        using var client = host.NewClient(auth.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ProbeRoute)).StatusCode);

        await ExecuteAsync(host.Database, """UPDATE "UserCompanyMemberships" SET "Status" = 'Suspended'""");

        await AssertCompanyContextInvalidAsync(await client.GetAsync(ProbeRoute));
        using var anonymous = host.NewClient();
        var refresh = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken), ApiHost.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task DisabledUser_Is401OnTheNextRequestAndRefreshFails()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        var auth = await host.LoginAsync();
        using var client = host.NewClient(auth.AccessToken);

        await ExecuteAsync(host.Database, """UPDATE "Users" SET "Status" = 'Disabled'""");

        await AssertCompanyContextInvalidAsync(await client.GetAsync(ProbeRoute));
        using var anonymous = host.NewClient();
        var refresh = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken), ApiHost.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task InactiveCompany_Is401OnTheNextRequestAndRefreshFails()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        var auth = await host.LoginAsync();
        using var client = host.NewClient(auth.AccessToken);

        await ExecuteAsync(host.Database, $"""UPDATE "Companies" SET "IsActive" = false WHERE "Id" = '{DefaultCompany}'""");

        await AssertCompanyContextInvalidAsync(await client.GetAsync(ProbeRoute));
        using var anonymous = host.NewClient();
        var refresh = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken), ApiHost.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Login_UserWithoutAnyActiveMembership_Is403NoCompanyAccess()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        await AddUserAsync(host, "orphan"); // no membership at all
        using var anonymous = host.NewClient();

        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("orphan@selloramart.in", ApiHost.AdminPassword), ApiHost.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("no_company_access", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task SwitchCompany_ToAMemberCompany_IssuesNewClaimAndRevokesTheOldRefreshToken()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        await AddCompanyAsync(host, CompanyB, "Company B");
        await AddMembershipAsync(host, await AdminIdAsync(host), CompanyB);
        var auth = await host.LoginAsync();
        using var client = host.NewClient(auth.AccessToken);

        var response = await client.PostAsJsonAsync("/api/v1/auth/switch-company", new SwitchCompanyRequest(CompanyB, auth.RefreshToken), ApiHost.Json);

        response.EnsureSuccessStatusCode();
        var switched = (await response.Content.ReadFromJsonAsync<AuthResponse>(ApiHost.Json))!;
        Assert.Equal(OtherCompany, ApiHost.ClaimOf(switched.AccessToken, AuthClaimTypes.CompanyId));
        Assert.Equal((CompanyB, "Company B"), (switched.User.CompanyId, switched.User.CompanyName));
        using var asB = host.NewClient(switched.AccessToken);
        var taxesInB = await asB.GetFromJsonAsync<JsonElement>(ProbeRoute, ApiHost.Json);
        Assert.Equal(0, taxesInB.GetArrayLength()); // company B has no data: the context really is B
        using var anonymous = host.NewClient();
        var reuse = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken), ApiHost.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        var refreshedB = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(switched.RefreshToken), ApiHost.Json);
        var refreshed = (await refreshedB.Content.ReadFromJsonAsync<AuthResponse>(ApiHost.Json))!;
        Assert.Equal(OtherCompany, ApiHost.ClaimOf(refreshed.AccessToken, AuthClaimTypes.CompanyId)); // refresh never switches tenant
    }

    [Fact]
    public async Task SwitchCompany_ToNonMemberOrNonExistentCompany_GivesTheSameForbiddenResponse()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        await AddCompanyAsync(host, CompanyC, "Company C"); // exists, but the admin is not a member
        var auth = await host.LoginAsync();
        using var client = host.NewClient(auth.AccessToken);

        var nonMember = await client.PostAsJsonAsync("/api/v1/auth/switch-company", new SwitchCompanyRequest(CompanyC, auth.RefreshToken), ApiHost.Json);
        var missing = await client.PostAsJsonAsync("/api/v1/auth/switch-company", new SwitchCompanyRequest(Guid.NewGuid(), auth.RefreshToken), ApiHost.Json);

        Assert.Equal(HttpStatusCode.Forbidden, nonMember.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal("company_access_denied", await nonMember.ProblemCodeAsync());
        Assert.Equal("company_access_denied", await missing.ProblemCodeAsync());
        string Title(string body) => JsonDocument.Parse(body).RootElement.GetProperty("title").GetString()!;
        Assert.Equal(Title(await nonMember.Content.ReadAsStringAsync()), Title(await missing.Content.ReadAsStringAsync()));
        // The failed switches did not consume the refresh token.
        using var anonymous = host.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken), ApiHost.Json)).StatusCode);
    }

    [Fact]
    public async Task CreateUserThroughTheApi_GetsAMembershipAndCanLogIn()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        using var admin = await host.LoginClientAsync();
        Guid roleId;
        await using (var db = host.Database.CreateDbContext()) roleId = await db.Roles.Where(r => r.IsSystem).Select(r => r.Id).SingleAsync();

        var response = await admin.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("New", "Person", "newperson", "newperson@selloramart.in", null, "Passw0rd!99", "Passw0rd!99", roleId), ApiHost.Json);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var login = await host.LoginAsync("newperson@selloramart.in", "Passw0rd!99");
        Assert.Equal(DefaultTenant.CompanyId, login.User.CompanyId);
        Assert.Equal(1L, await ScalarAsync<long>(host.Database, $"""SELECT COUNT(*) FROM "UserCompanyMemberships" m JOIN "Users" u ON u."Id" = m."UserId" WHERE u."Username" = 'newperson' AND m."CompanyId" = '{DefaultCompany}' AND m."Status" = 'Active'"""));
    }

    [Fact]
    public async Task Users_OfAnotherCompany_AreInvisibleAndNotFoundForDetailUpdateStatusAndResetPassword()
    {
        await using var host = await ApiHost.StartAsync(postgres);
        await AddCompanyAsync(host, CompanyB, "Company B");
        var userB = await AddUserAsync(host, "bperson", CompanyB);
        var userA = await AddUserAsync(host, "aperson", DefaultTenant.CompanyId);
        using var admin = await host.LoginClientAsync();
        Guid roleId;
        await using (var db = host.Database.CreateDbContext()) roleId = await db.Roles.Where(r => r.IsSystem).Select(r => r.Id).SingleAsync();

        var list = await admin.GetStringAsync("/api/v1/users");
        var detail = await admin.GetAsync($"/api/v1/users/{userB.Id}");
        var ownDetail = await admin.GetAsync($"/api/v1/users/{userA.Id}");
        var update = await admin.PutAsJsonAsync($"/api/v1/users/{userB.Id}", new UpdateUserRequest("Hacked", "Name", "bperson", "bperson@selloramart.in", null, roleId), ApiHost.Json);
        var status = await admin.PostAsJsonAsync($"/api/v1/users/{userB.Id}/status", new SetUserStatusRequest(UserStatus.Disabled), ApiHost.Json);
        var reset = await admin.PostAsync($"/api/v1/users/{userB.Id}/reset-password", null);

        Assert.Contains(userA.Id.ToString(), list);
        Assert.DoesNotContain(userB.Id.ToString(), list);
        Assert.Equal(HttpStatusCode.OK, ownDetail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, reset.StatusCode);
        Assert.Equal(("Active", "bperson"), (await ScalarAsync<string>(host.Database, """SELECT "Status" FROM "Users" WHERE "Username" = 'bperson'"""),
            await ScalarAsync<string>(host.Database, """SELECT "FirstName" FROM "Users" WHERE "Username" = 'bperson'""")));
    }
}
