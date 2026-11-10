using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Phase 6 O3: scripts/sql/grant-platform-admin.sql is the ONLY way platform administrators are created. It is run exactly as an operator would
/// (psql -v username=... inside the Testcontainers PostgreSQL container): grants, is idempotent, reactivates a revoked grant, refuses unknown /
/// disabled users, writes one platform.admin_granted audit row per effective change.
/// </summary>
[Collection(PostgresCollection.Name)]
public class GrantPlatformAdminScriptTests(PostgresFixture postgres)
{
    private static string G(int n) => $"00000000-0000-0000-0000-{n:x12}";
    private static readonly string Role = G(0xc301), Ops = G(0xd301), Disabled = G(0xd302), Member = G(0xd303);

    private static string Script => File.ReadAllText(ScriptPath());

    private static string ScriptPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "sql", "grant-platform-admin.sql");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("scripts/sql/grant-platform-admin.sql not found above " + AppContext.BaseDirectory);
    }

    private async Task<TestDatabase> CreateAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await ExecuteAsync(database, $"""
            INSERT INTO "Roles" ("Id", "Name", "Description", "IsActive", "IsSystem") VALUES ('{Role}', 'Ops', NULL, true, false);
            INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username") VALUES
              ('{Ops}', now(), 'Ops.Alice@Example.io', 'Ops', 'Alice', 'x', '{Role}', 'Active', 'ops.alice'),
              ('{Disabled}', now(), 'gone@example.io', 'Gone', 'Bob', 'x', '{Role}', 'Disabled', 'gone.bob'),
              ('{Member}', now(), 'member@example.io', 'Mem', 'Ber', 'x', '{Role}', 'Active', 'member1');
            INSERT INTO "UserCompanyMemberships" ("Id", "UserId", "CompanyId", "Status", "AccessScope", "CreatedAt")
              VALUES ('{G(0xe301)}', '{Member}', '{DefaultCompany}', 'Active', 'CompanyWide', now());
            """);
        return database;
    }

    private Task<PostgresFixture.PsqlResult> RunAsync(TestDatabase database, string? username, string? reason = null)
    {
        var vars = new Dictionary<string, string>();
        if (username is not null) vars["username"] = username;
        if (reason is not null) vars["reason"] = reason;
        return postgres.RunPsqlScriptAsync(database.Name, Script, vars);
    }

    private static Task<List<(string Status, bool GrantedByNull, bool RevokedNull, string? Reason, DateTimeOffset GrantedAt)>> RowsAsync(TestDatabase database, string user) =>
        QueryAsync(database, $"""SELECT "Status", ("GrantedByUserId" IS NULL), ("RevokedAt" IS NULL AND "RevokedByUserId" IS NULL), "Reason", "GrantedAt" FROM "PlatformAdministrators" WHERE "UserId" = '{user}'""",
            r => (r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)));

    private static Task<long> AuditCountAsync(TestDatabase database) =>
        ScalarAsync<long>(database, """SELECT COUNT(*) FROM "PlatformAuditEvents" WHERE "Action" = 'platform.admin_granted'""");

    [Fact]
    public async Task Script_GrantsAnActivePlatformAdminByUsername_AndWritesTheAuditRow()
    {
        await using var database = await CreateAsync();

        var result = await RunAsync(database, "ops.alice", "Platform operator onboarding");

        Assert.True(result.ExitCode == 0, result.Stderr);
        var row = Assert.Single(await RowsAsync(database, Ops));
        Assert.Equal("Active", row.Status); Assert.True(row.GrantedByNull); Assert.True(row.RevokedNull); Assert.Equal("Platform operator onboarding", row.Reason);
        var audit = await QueryAsync(database, $"""
            SELECT "TargetType", "TargetId"::text, ("ActorUserId" IS NULL), ("CompanyId" IS NULL), "Reason", ("Before" IS NULL), "After"->>'status', "CorrelationId"
            FROM "PlatformAuditEvents" WHERE "Action" = 'platform.admin_granted'
            """, r => (r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetString(4), r.GetBoolean(5), r.GetString(6), r.GetString(7)));
        var a = Assert.Single(audit);
        Assert.Equal(("PlatformAdministrator", Ops, true, true, "Platform operator onboarding", true, "Active", "ops-script"), a);
    }

    [Fact]
    public async Task Script_MatchesEmailCaseInsensitively_AndUsesTheDefaultReason()
    {
        await using var database = await CreateAsync();

        var result = await RunAsync(database, "OPS.ALICE@example.IO");

        Assert.True(result.ExitCode == 0, result.Stderr);
        var row = Assert.Single(await RowsAsync(database, Ops));
        Assert.Contains("grant-platform-admin.sql", row.Reason);
        Assert.Equal(1L, await AuditCountAsync(database));
    }

    [Fact]
    public async Task Script_RunTwice_IsIdempotent_NoSecondAuditRowAndGrantUntouched()
    {
        await using var database = await CreateAsync();
        await RunAsync(database, "ops.alice", "first");
        var first = Assert.Single(await RowsAsync(database, Ops));

        var second = await RunAsync(database, "ops.alice", "second");

        Assert.True(second.ExitCode == 0, second.Stderr);
        Assert.Contains("already an active platform administrator", second.Stderr + second.Stdout);
        var row = Assert.Single(await RowsAsync(database, Ops));
        Assert.Equal(first, row); // reason and GrantedAt unchanged
        Assert.Equal(1L, await AuditCountAsync(database));
    }

    [Fact]
    public async Task Script_ReactivatesARevokedGrant_AndAuditsTheTransition()
    {
        await using var database = await CreateAsync();
        await ExecuteAsync(database, $"""
            INSERT INTO "PlatformAdministrators" ("UserId", "Status", "GrantedAt", "RevokedAt", "RevokedByUserId", "Reason")
            VALUES ('{Ops}', 'Revoked', now() - interval '10 days', now() - interval '1 day', '{Member}', 'old');
            """);

        var result = await RunAsync(database, "ops.alice", "Re-onboarded");

        Assert.True(result.ExitCode == 0, result.Stderr);
        var row = Assert.Single(await RowsAsync(database, Ops));
        Assert.Equal("Active", row.Status); Assert.True(row.RevokedNull); Assert.True(row.GrantedByNull); Assert.Equal("Re-onboarded", row.Reason);
        Assert.True(row.GrantedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        var audit = Assert.Single(await QueryAsync(database, """
            SELECT "Before"->>'status', "After"->>'status', "Reason" FROM "PlatformAuditEvents" WHERE "Action" = 'platform.admin_granted'
            """, r => (r.GetString(0), r.GetString(1), r.GetString(2))));
        Assert.Equal(("Revoked", "Active", "Re-onboarded"), audit);
    }

    [Fact]
    public async Task Script_UnknownUser_FailsAndChangesNothing()
    {
        await using var database = await CreateAsync();

        var result = await RunAsync(database, "nobody");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("no user matches", result.Stderr);
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "PlatformAdministrators" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "PlatformAuditEvents" """));
    }

    [Fact]
    public async Task Script_DisabledUser_FailsAndChangesNothing()
    {
        await using var database = await CreateAsync();

        var result = await RunAsync(database, "gone.bob");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("is disabled", result.Stderr);
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "PlatformAdministrators" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "PlatformAuditEvents" """));
    }

    [Fact]
    public async Task Script_ReasonLongerThan500_FailsAndChangesNothing()
    {
        await using var database = await CreateAsync();

        var result = await RunAsync(database, "ops.alice", new string('x', 501));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("longer than 500", result.Stderr);
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "PlatformAdministrators" """));
    }

    [Fact]
    public async Task Script_UserWithACompanyMembership_WarnsButGrants()
    {
        await using var database = await CreateAsync();

        var result = await RunAsync(database, "member1");

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("has a company membership", result.Stderr + result.Stdout);
        Assert.Single(await RowsAsync(database, Member));
    }

    [Fact]
    public async Task Script_UserWhoIsAnActiveGovernanceMember_FailsAndGrantsNothing()
    {
        await using var database = await CreateAsync();
        await ExecuteAsync(database, $"""
            INSERT INTO "CompanyGovernanceMembers" ("Id", "CompanyId", "UserCompanyMembershipId", "Title", "Status", "StartAt", "CreatedAt")
              VALUES ('{G(0xf301)}', '{DefaultCompany}', '{G(0xe301)}', 'Director', 'Active', now(), now());
            """);

        var result = await RunAsync(database, "member1");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("active company governance member", result.Stderr);
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "PlatformAdministrators" """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "PlatformAuditEvents" """));
    }

    [Fact]
    public async Task Script_WithoutUsername_PrintsUsageAndChangesNothing()
    {
        await using var database = await CreateAsync();

        var result = await RunAsync(database, null);

        Assert.Contains("usage", result.Stdout + result.Stderr);
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "PlatformAdministrators" """));
    }

    [Fact]
    public async Task Script_ValueWithQuotes_IsHandledAsData_NotSql()
    {
        await using var database = await CreateAsync();

        var result = await RunAsync(database, "x'; SELECT 1; --");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("no user matches", result.Stderr);
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "PlatformAdministrators" """));
    }
}
