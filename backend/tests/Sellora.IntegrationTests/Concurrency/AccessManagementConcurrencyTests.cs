using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// User/role management writes are serialised by one advisory lock and re-load the caller after taking it
/// (<c>AccessManagementScope</c>). Two Administrators disabling (or demoting) each other at the same moment must not
/// both succeed and leave no active Administrator: a gate transaction holds the lock, both requests are started and
/// must wait on it; after release exactly one succeeds and the other is refused because its caller is no longer an
/// active Administrator. Without the lock both requests finish while the gate is held (WaitBlockedAsync fails).
/// </summary>
[Collection(PostgresCollection.Name)]
public class AccessManagementConcurrencyTests(PostgresFixture postgres)
{
    private const string AccessLock = "SELECT pg_advisory_xact_lock(hashtext('sellora:access-management'))";

    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => "hash:" + password;
        public bool Verify(string password, string hash) => hash == "hash:" + password;
    }

    private sealed record Seed(Guid AdminRoleId, Guid OtherRoleId, User A, User B);

    private static async Task<Seed> SeedAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        // A migrated database may already hold the seeded Administrator role; reuse it when present.
        var admin = await db.Roles.SingleOrDefaultAsync(r => r.IsSystem);
        if (admin is null)
        {
            admin = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
            db.Roles.Add(admin);
        }
        var other = new Role { Id = Guid.NewGuid(), Name = "Race Viewer" };
        var a = Admin("race-a");
        var b = Admin("race-b");
        db.AddRange(other, a, b);
        var (ma, mb) = (Sellora.IntegrationTests.TestData.TestCompany.MembershipFor(a), Sellora.IntegrationTests.TestData.TestCompany.MembershipFor(b));
        db.UserCompanyMemberships.AddRange(ma, mb);
        await db.SaveChangesAsync();
        // Phase 7: roles are held through assignments (Users.RoleId is ignored).
        db.UserRoleAssignments.AddRange(Assignment(ma, admin.Id), Assignment(mb, admin.Id));
        await db.SaveChangesAsync();
        return new Seed(admin.Id, other.Id, a, b);
    }

    private static User Admin(string username) => new()
    {
        Id = Guid.NewGuid(), FirstName = username, LastName = "Admin", Username = username, Email = $"{username}@selloramart.in",
        PasswordHash = "x", Status = UserStatus.Active,
    };

    private static UserRoleAssignment Assignment(UserCompanyMembership m, Guid roleId) => new()
    {
        Id = Guid.NewGuid(), CompanyId = m.CompanyId, UserCompanyMembershipId = m.Id, RoleId = roleId, ScopeType = RoleScope.Company,
        Status = AssignmentStatus.Active, StartAt = DateTimeOffset.UtcNow.AddMinutes(-1),
    };

    private static UsersController As(SelloraDbContext db, User actor) => new(db, new FakeHasher(), new Sellora.Infrastructure.Services.UserAccessService(db),
        new Sellora.Infrastructure.Services.RoleAssignmentService(db, new Sellora.Infrastructure.Services.UserAccessService(db)))
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString())], "test")),
            },
        },
    };

    private static async Task<int> CountActiveAdministratorsAmongAsync(TestDatabase database, params Guid[] userIds)
    {
        await using var check = database.CreateDbContext();
        return await check.UserRoleAssignments.CountAsync(a => a.Status == AssignmentStatus.Active && check.Roles.Any(r => r.Id == a.RoleId && r.IsSystem)
            && check.UserCompanyMemberships.Any(m => m.Id == a.UserCompanyMembershipId && userIds.Contains(m.UserId) && m.User.Status != UserStatus.Disabled));
    }

    /// <summary>Runs both requests behind the gate and returns their action results (in start order).</summary>
    private static async Task<object?[]> RaceAsync(TestDatabase database, Func<SelloraDbContext, Task<object?>> first, Func<SelloraDbContext, Task<object?>> second)
    {
        var results = new object?[2];
        await using var race = await LockRace.BeginAsync(database, gate => gate.Database.ExecuteSqlRawAsync(AccessLock));
        var w1 = await race.StartAsync("first", async db => results[0] = await first(db));
        await race.WaitBlockedAsync(w1, "%pg_advisory_xact_lock%");
        var w2 = await race.StartAsync("second", async db => results[1] = await second(db));
        await race.WaitBlockedAsync(w2, "%pg_advisory_xact_lock%");
        var outcomes = await race.ReleaseAsync();
        Assert.All(outcomes, Assert.Null);
        return results;
    }

    private static int? StatusOf(object? result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => null,
    };

    [Fact]
    public async Task TwoAdministratorsDisablingEachOther_Concurrently_ExactlyOneSucceeds()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database);
        var disable = new SetUserStatusRequest(UserStatus.Disabled);

        var results = await RaceAsync(database,
            async db => await As(db, seed.A).SetStatus(seed.B.Id, disable, default),
            async db => await As(db, seed.B).SetStatus(seed.A.Id, disable, default));

        Assert.Equal(204, StatusOf(results[0]));
        Assert.Equal(403, StatusOf(results[1]));
        Assert.Equal(1, await CountActiveAdministratorsAmongAsync(database, seed.A.Id, seed.B.Id));
    }

    [Fact]
    public async Task TwoAdministratorsDemotingEachOther_Concurrently_ExactlyOneSucceeds()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database);
        UpdateUserRequest Demote(User u) => new(u.FirstName, u.LastName, u.Username, u.Email, null, seed.OtherRoleId);

        var results = await RaceAsync(database,
            async db => (await As(db, seed.A).Update(seed.B.Id, Demote(seed.B), default)).Result,
            async db => (await As(db, seed.B).Update(seed.A.Id, Demote(seed.A), default)).Result);

        Assert.Equal(200, StatusOf(results[0]));
        Assert.Equal(403, StatusOf(results[1]));
        Assert.Equal(1, await CountActiveAdministratorsAmongAsync(database, seed.A.Id, seed.B.Id));
    }
}
