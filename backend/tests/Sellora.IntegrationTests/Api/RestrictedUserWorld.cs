using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;

namespace Sellora.IntegrationTests.Api;

/// <summary>A user created through the real API (POST users, optionally the access API) and signed in with a password login.</summary>
public sealed class RestrictedUser(Guid id, string username, string password, AuthResponse auth, HttpClient client, Guid roleId)
{
    public Guid Id { get; } = id;
    public string Username { get; } = username;
    public string Password { get; } = password;
    public AuthResponse Auth { get; } = auth;
    public Guid RoleId { get; } = roleId;
    /// <summary>Bearer client with the access token of the login.</summary>
    public HttpClient Client { get; } = client;
}

/// <summary>
/// Phase 5 harness gap closed: restricted (AssignedBranches) users over HTTP. The shared <see cref="BranchApiWorld"/> admin creates the user through
/// <c>POST users</c> (initial access fields) and the access API, then the user signs in with username + password (Invited -> Active on login).
/// Roles: <see cref="StaffRoleId"/> = every operational permission, no user/role/branch-access administration; <see cref="DelegateRoleId"/> =
/// Users.View + Company.Users.ManageBranchAccess + Company.Branches.View only.
/// </summary>
public sealed class RestrictedUserWorld
{
    public const string Password = "Passw0rd!99";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static RestrictedUserWorld? _instance;

    public required BranchApiWorld World { get; init; }
    public required Guid StaffRoleId { get; init; }
    public required Guid DelegateRoleId { get; init; }
    public required Guid AdminRoleId { get; init; }

    public static async Task<RestrictedUserWorld> GetWorldAsync(PostgresFixture postgres)
    {
        var world = await BranchApiWorld.GetAsync(postgres);
        await Gate.WaitAsync();
        try
        {
            if (_instance is not null) return _instance;
            var staff = PermissionCatalog.All.Select(p => p.Code)
                .Where(c => !c.StartsWith("Users.") && !c.StartsWith("Roles.") && !c.StartsWith("Permissions.") && c != "Company.Users.ManageBranchAccess"
                            && !c.StartsWith("Company.Branches.") || c == "Company.Branches.View")
                .ToArray();
            string[] delegates = ["Users.View", "Company.Users.ManageBranchAccess", "Company.Branches.View"];
            Guid adminRole;
            await using (var db = world.Host.Database.CreateDbContext()) adminRole = db.Roles.Where(r => r.IsSystem).Select(r => r.Id).Single();
            return _instance = new RestrictedUserWorld
            {
                World = world, AdminRoleId = adminRole,
                StaffRoleId = await CreateRoleAsync(world, "Branch Staff", staff),
                DelegateRoleId = await CreateRoleAsync(world, "Access Delegate", delegates),
            };
        }
        finally { Gate.Release(); }
    }

    private static async Task<Guid> CreateRoleAsync(BranchApiWorld world, string name, string[] codes)
    {
        var response = await world.Admin.PostAsJsonAsync("/api/v1/roles", new CreateRoleRequest(name, null, codes), ApiHost.Json);
        Check(response, "create role " + name);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    public static string NewName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    public Task<HttpResponseMessage> PostUserAsync(string username, Guid roleId, CompanyAccessScope? scope = null, Guid[]? branchIds = null, Guid? primary = null, HttpClient? client = null) =>
        (client ?? World.Admin).PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("Test", "User", username, username + "@selloramart.in", null, Password, Password, roleId, scope, branchIds, primary), ApiHost.Json);

    /// <summary>Creates a user (default role = staff). No access fields = the D12 default (AssignedBranches at the creator's working branch).</summary>
    public async Task<RestrictedUser> CreateAsync(string prefix = "ru", Guid? roleId = null, CompanyAccessScope? scope = null, Guid[]? branchIds = null, Guid? primary = null)
    {
        var username = NewName(prefix);
        var role = roleId ?? StaffRoleId;
        var response = await PostUserAsync(username, role, scope, branchIds, primary);
        Check(response, "create user " + username);
        var id = (await JsonAsync(response)).GetProperty("id").GetGuid();
        return await LoginAsync(id, username, role);
    }

    /// <summary>Restricted staff user allocated to <paramref name="branches"/> with the first as primary.</summary>
    public Task<RestrictedUser> CreateAtAsync(params Guid[] branches) =>
        CreateAsync("ru", StaffRoleId, CompanyAccessScope.AssignedBranches, branches, branches[0]);

    public async Task<RestrictedUser> LoginAsync(Guid id, string username, Guid roleId)
    {
        var auth = await World.Host.LoginAsync(username, Password);
        return new RestrictedUser(id, username, Password, auth, World.Host.NewClient(auth.AccessToken), roleId);
    }

    // ---- access API (admin) -------------------------------------------------------------------------------------------------

    public Task<HttpResponseMessage> AccessAsync(HttpMethod method, Guid userId, string suffix, object? body = null, HttpClient? client = null) =>
        World.SendAsync(method, $"/api/v1/users/{userId}/access{suffix}", body, client: client);

    public static async Task<JsonElement> JsonOkAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await JsonAsync(response);
    }

    public async Task<JsonElement> GetAccessAsync(Guid userId) => await JsonOkAsync(await AccessAsync(HttpMethod.Get, userId, ""));

    public static Guid[] CurrentBranches(JsonElement access) =>
        access.GetProperty("current").EnumerateArray().Select(a => a.GetProperty("branchId").GetGuid()).ToArray();

    public static Guid? PrimaryOf(JsonElement access) =>
        access.GetProperty("current").EnumerateArray().Where(a => a.GetProperty("isPrimary").GetBoolean()).Select(a => (Guid?)a.GetProperty("branchId").GetGuid()).SingleOrDefault();
}
