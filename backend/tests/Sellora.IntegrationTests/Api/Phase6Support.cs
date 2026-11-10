using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Permissions;
using Sellora.Infrastructure.Security;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>Shared setup for the Phase 6 API tests (platform session, governance): users, roles, memberships and governance members written directly to PostgreSQL.</summary>
public static class Phase6Support
{
    public const string Password = ApiHost.AdminPassword;

    public static readonly string[] AllCodes = GovernancePermissionCatalog.All.Select(d => d.Code).ToArray();
    public static readonly Guid StaffRoleId = Guid.Parse("00000000-0000-0000-0000-0000000c6601");

    private static readonly string PasswordHash = new PasswordHasher().Hash(Password);

    public static async Task EnsureStaffRoleAsync(ApiHost host) =>
        await ExecuteAsync(host.Database, $"""
            INSERT INTO "Roles" ("Id", "Name", "Description", "IsActive", "IsSystem") VALUES ('{StaffRoleId}', 'Phase6 Staff', NULL, true, false) ON CONFLICT DO NOTHING;
            """);

    /// <summary>A user (role Administrator when <paramref name="administrator"/>, else the Phase 6 staff role) with a CompanyWide membership in <paramref name="company"/> (null: none).</summary>
    public static async Task<Guid> AddUserAsync(ApiHost host, string username, bool administrator = false, string? company = DefaultCompany, string status = "Active", string membershipStatus = "Active")
    {
        await EnsureStaffRoleAsync(host);
        var id = Guid.NewGuid();
        var role = administrator ? """(SELECT "Id" FROM "Roles" WHERE "IsSystem" AND "IsActive" ORDER BY "Name" LIMIT 1)""" : $"'{StaffRoleId}'";
        var membership = company is null ? "" : $"""
            INSERT INTO "UserCompanyMemberships" ("Id", "UserId", "CompanyId", "Status", "AccessScope", "CreatedAt") VALUES (gen_random_uuid(), '{id}', '{company}', '{membershipStatus}', 'CompanyWide', now());
            """;
        await ExecuteAsync(host.Database, $"""
            INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username")
            VALUES ('{id}', now(), '{username}@example.io', '{username}', 'T', '{PasswordHash}', {role}, '{status}', '{username}');
            {membership}
            """);
        return id;
    }

    public static Task GrantPlatformAdminAsync(ApiHost host, Guid userId) => ExecuteAsync(host.Database, $"""
        INSERT INTO "PlatformAdministrators" ("UserId", "Status", "GrantedAt") VALUES ('{userId}', 'Active', now());
        """);

    public static Task RevokePlatformAdminAsync(ApiHost host, Guid userId) => ExecuteAsync(host.Database, $"""
        UPDATE "PlatformAdministrators" SET "Status" = 'Revoked', "RevokedAt" = now() WHERE "UserId" = '{userId}';
        """);

    /// <summary>An Active governance member with exactly the given grants (what the migration / company creation write).</summary>
    public static async Task<Guid> AddGovernanceMemberAsync(ApiHost host, Guid userId, string title, string? company = DefaultCompany, params string[] codes)
    {
        var memberId = Guid.NewGuid();
        var grants = string.Concat(codes.Select(c => $"""
            INSERT INTO "GovernanceMemberPermissions" ("Id", "CompanyId", "GovernanceMemberId", "GovernancePermissionId", "GrantedAt")
            VALUES (gen_random_uuid(), '{company}', '{memberId}', '{GovernancePermissionCatalog.IdFor(c)}', now());
            """));
        await ExecuteAsync(host.Database, $"""
            INSERT INTO "CompanyGovernanceMembers" ("Id", "CompanyId", "UserCompanyMembershipId", "Title", "Status", "StartAt", "CreatedAt")
            SELECT '{memberId}', '{company}', m."Id", '{title}', 'Active', now() - interval '1 hour', now() - interval '1 hour'
            FROM "UserCompanyMemberships" m WHERE m."UserId" = '{userId}' AND m."CompanyId" = '{company}';
            {grants}
            """);
        return memberId;
    }

    public static Task<Guid> OwnerMemberOfAsync(ApiHost host, Guid userId, string company = DefaultCompany) => ScalarAsync<Guid>(host.Database, $"""
        SELECT g."Id" FROM "CompanyGovernanceMembers" g JOIN "UserCompanyMemberships" m ON m."CompanyId" = g."CompanyId" AND m."Id" = g."UserCompanyMembershipId"
        WHERE m."UserId" = '{userId}' AND m."CompanyId" = '{company}' AND g."Status" = 'Active'
        """);

    public static Task<Guid> AdminUserIdAsync(ApiHost host) => ScalarAsync<Guid>(host.Database, $"""SELECT "Id" FROM "Users" WHERE "Email" = '{ApiHost.AdminEmail}'""");

    public static async Task<HttpClient> PlatformClientAsync(ApiHost host, string username)
    {
        using var login = host.NewClient();
        var response = await login.PostAsJsonAsync("/api/v1/auth/platform/login", new PlatformLoginRequest(username, Password), ApiHost.Json);
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<PlatformLoginResponse>(ApiHost.Json))!;
        return host.NewClient(body.AccessToken);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public static CreatePlatformCompanyRequest NewCompany(string code, Guid currencyId, string ownerUsername, string? ownerEmail = null, Guid? existingOwner = null) =>
        new(code, code + " Retail", code + " Retail Pvt Ltd", code.Replace("-", "")[..Math.Min(4, code.Replace("-", "").Length)], currencyId, null, null, "Pune", "MH", null, null,
            existingOwner is { } e
                ? new PlatformOwnerRequest(e, null)
                : new PlatformOwnerRequest(null, new PlatformNewOwnerRequest("Asha", "Rao", ownerUsername, ownerEmail ?? $"{ownerUsername}@example.io", null, "Str0ng!Passw0rd1", "Str0ng!Passw0rd1")),
            "Onboarding");

    public static Task<Guid> InrIdAsync(ApiHost host) => ScalarAsync<Guid>(host.Database, """SELECT "Id" FROM "Currencies" WHERE "Code" = 'INR'""");
}
