using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Security;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// A dedicated API host for the Phase 4 tests (its own database, so warehouses/vehicles created here never disturb the shared
/// <see cref="BranchApiWorld"/> assertions about stock-location lists). Holds the admin client, Main, and a branch-RESTRICTED user
/// (AssignedBranches scope with a single primary allocation at Main: every branch the tests create stays inaccessible to it).
/// </summary>
public sealed class WarehouseVehicleApiWorld
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static WarehouseVehicleApiWorld? _instance;
    private static int _seed = 100;

    public const string RestrictedEmail = "restricted@selloramart.in";

    public required ApiHost Host { get; init; }
    public required HttpClient Admin { get; init; }
    public required HttpClient Restricted { get; init; }
    public required Guid AdminId { get; init; }
    public required Guid CompanyId { get; init; }
    public required Guid MainId { get; init; }

    public static async Task<WarehouseVehicleApiWorld> GetAsync(PostgresFixture postgres)
    {
        await Gate.WaitAsync();
        try { return _instance ??= await BuildAsync(postgres); }
        finally { Gate.Release(); }
    }

    private static async Task<WarehouseVehicleApiWorld> BuildAsync(PostgresFixture postgres)
    {
        var host = await ApiHost.StartAsync(postgres);
        Guid adminId, companyId, mainId;
        await using (var db = host.Database.CreateDbContext())
        {
            adminId = await db.Users.Where(u => u.Email == ApiHost.AdminEmail).Select(u => u.Id).SingleAsync();
            companyId = await db.BusinessSettings.Select(s => s.CompanyId).SingleAsync();
            mainId = await db.Branches.Where(b => b.IsDefault).Select(b => b.Id).SingleAsync();
            var roleId = await db.Roles.Where(r => r.IsSystem).Select(r => r.Id).SingleAsync();
            var user = new User
            {
                Id = Guid.NewGuid(), FirstName = "R", LastName = "Estricted", Username = "restricted", Email = RestrictedEmail,
                PasswordHash = new PasswordHasher().Hash(ApiHost.AdminPassword), RoleId = roleId, Status = UserStatus.Active,
            };
            db.Users.Add(user);
            var membership = new UserCompanyMembership { Id = Guid.NewGuid(), UserId = user.Id, CompanyId = companyId, AccessScope = CompanyAccessScope.AssignedBranches };
            db.UserCompanyMemberships.Add(membership);
            // Phase 5: an AssignedBranches member needs an active primary allocation to sign in (Main only; every branch a test creates is inaccessible to it).
            db.UserBranchAssignments.Add(new UserBranchAssignment { Id = Guid.NewGuid(), CompanyId = companyId, UserCompanyMembershipId = membership.Id, BranchId = mainId, IsPrimary = true, AssignmentReason = "test fixture" });
            Sellora.IntegrationTests.TestData.RbacSeed.Assign(db, membership, roleId); // Phase 7: held through an assignment
            await db.SaveChangesAsync();
        }
        return new WarehouseVehicleApiWorld
        {
            Host = host, Admin = await host.LoginClientAsync(), Restricted = await host.LoginClientAsync(RestrictedEmail),
            AdminId = adminId, CompanyId = companyId, MainId = mainId,
        };
    }

    public string NextCode(string prefix = "W") => prefix + Interlocked.Increment(ref _seed);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, object? body = null, HttpClient? client = null, Guid? branch = null)
    {
        using var request = new HttpRequestMessage(method, route);
        if (branch is { } id) request.Headers.Add("X-Branch-Id", id.ToString());
        if (body is not null) request.Content = JsonContent.Create(body, options: ApiHost.Json);
        return await (client ?? Admin).SendAsync(request);
    }

    public static Task<JsonElement> JsonAsync(HttpResponseMessage response) => BranchApiWorld.JsonAsync(response);

    public async Task<(Guid Id, string Code)> NewBranchAsync()
    {
        var code = NextCode("B");
        var response = await SendAsync(HttpMethod.Post, "/api/v1/branches", new CreateBranchRequest(code, "Branch " + code, null, null, null, null, null, null, null));
        BranchApiWorld.Check(response, "create branch " + code);
        return ((await JsonAsync(response)).GetProperty("id").GetGuid(), code);
    }

    public async Task<JsonElement> NewWarehouseAsync(Guid? branchId = null, string? code = null, string? name = null)
    {
        code ??= NextCode();
        var response = await SendAsync(HttpMethod.Post, "/api/v1/warehouses", new CreateWarehouseRequest(code, name ?? "Warehouse " + code, branchId));
        BranchApiWorld.Check(response, "create warehouse " + code);
        return await JsonAsync(response);
    }

    public async Task<JsonElement> NewVehicleAsync(Guid? assignedBranchId = null, string? number = null, string type = "Van")
    {
        number ??= NextCode("V");
        var response = await SendAsync(HttpMethod.Post, "/api/v1/vehicles", new CreateVehicleRequest(number, "Vehicle " + number, type, assignedBranchId));
        BranchApiWorld.Check(response, "create vehicle " + number);
        return await JsonAsync(response);
    }

    public static async Task<List<string>> ErrorFieldsAsync(HttpResponseMessage response) => await BranchApiWorld.ErrorFieldsAsync(response);
}
