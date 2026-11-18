using Microsoft.EntityFrameworkCore;
using Sellora.DbMigrator;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Releases;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.IntegrationTests.DbMigrator;

/// <summary>
/// Retry rules of the Database releases page on PostgreSQL (docs/features/db-release-automation.md, step 4, D21/D22). GitHub is
/// replaced by a recorder; the journal comes from the real migrator. Bound to Testcontainers in <see cref="DbReleaseRetryTests"/>.
/// </summary>
public abstract class DbReleaseRetryScenarios
{
    protected const string PipelineUrl = "https://github.com/acme/sellora-billing/actions/runs/777";

    /// <summary>A fully migrated database (EF schema), its connection string and a handle that drops it.</summary>
    protected abstract Task<(string ConnectionString, IAsyncDisposable Drop)> CreateMigratedDatabaseAsync();

    protected abstract SelloraDbContext CreateDbContext(string connectionString);

    protected sealed class FakeGitHub(bool configured = true, bool accept = true) : IGitHubActionsClient
    {
        public List<long> Reruns { get; } = [];
        public bool IsConfigured => configured;

        public long? ParseRunId(string? pipelineUrl) =>
            configured && pipelineUrl?.StartsWith("https://github.com/acme/sellora-billing/actions/runs/") == true
                ? long.Parse(pipelineUrl.Split('/')[^1])
                : null;

        public Task<GitHubCallResult> RerunFailedJobsAsync(long runId, CancellationToken ct)
        {
            lock (Reruns) Reruns.Add(runId);
            return Task.FromResult(accept ? GitHubCallResult.Ok : new GitHubCallResult(false, "403 run is older than 30 days"));
        }
    }

    protected sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task MigrateAsync(string cs, string sql, string? pipelineUrl = PipelineUrl)
    {
        var root = Directory.CreateTempSubdirectory("sellora-retry-it-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            File.WriteAllText(Path.Combine(root, "scripts", "2026.11.18.001__step.sql"), sql);
            var options = new MigratorOptions(cs, root, new RunInfo("claude-dev", "abc", "1", "Deploy", "ci", null, pipelineUrl),
                ViolationMode.Warn, TimeSpan.FromSeconds(30), EfBundlePath: null, PlanOnly: false);
            await new MigrationRunner(options, new StringWriter()).RunAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<Guid> AddPlatformUserAsync(SelloraDbContext db)
    {
        var user = new User { Id = Guid.NewGuid(), FirstName = "Asha", LastName = "Ops", Email = $"ops{Guid.NewGuid():N}@example.io", Username = $"ops{Guid.NewGuid():N}"[..20], PasswordHash = "x" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task Failed_latest_run_is_rerun_once_audited_and_blocked_for_ten_minutes()
    {
        var (cs, drop) = await CreateMigratedDatabaseAsync();
        await using var dropOnExit = drop;
        await MigrateAsync(cs, "SELECT 1/0;");
        await using var db = CreateDbContext(cs);
        var actor = await AddPlatformUserAsync(db);
        var github = new FakeGitHub();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var service = new DbReleaseRetryService(db, github, clock);

        var before = await service.GetInfoAsync(CancellationToken.None);
        Assert.True(before.CanRetry, before.UnavailableReason);

        var accepted = await service.RequestAsync("claude-dev", "Lock timeout on StockMovements, retrying", actor, "corr-1", CancellationToken.None);

        Assert.Equal([777L], github.Reruns);
        Assert.Equal(PipelineUrl, accepted.PipelineUrl);
        var audit = await db.PlatformAuditEvents.AsNoTracking().SingleAsync(e => e.Action == PlatformAuditActions.DbReleaseRetryRequested);
        Assert.Equal((accepted.RunId, actor, "Lock timeout on StockMovements, retrying"), (audit.TargetId, audit.ActorUserId, audit.Reason));

        var after = await service.GetInfoAsync(CancellationToken.None);
        Assert.False(after.CanRetry);
        Assert.Equal(("Asha Ops", "Lock timeout on StockMovements, retrying"), (after.LastRequest!.RequestedBy, after.LastRequest.Reason));
        var repeat = await Assert.ThrowsAsync<DbReleaseRetryException>(() =>
            service.RequestAsync("claude-dev", "Second click on the same button", actor, null, CancellationToken.None));
        Assert.Equal((409, DbReleaseRetryException.NotAllowed), (repeat.Status, repeat.Code));
        Assert.Single(github.Reruns);

        clock.Now = clock.Now.AddMinutes(11); // no new pipeline run appeared: allowed again
        Assert.True((await service.GetInfoAsync(CancellationToken.None)).CanRetry);
    }

    [Fact]
    public async Task Succeeded_latest_run_cannot_be_retried()
    {
        var (cs, drop) = await CreateMigratedDatabaseAsync();
        await using var dropOnExit = drop;
        await MigrateAsync(cs, "SELECT 1;");
        await using var db = CreateDbContext(cs);
        var service = new DbReleaseRetryService(db, new FakeGitHub(), TimeProvider.System);

        var info = await service.GetInfoAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<DbReleaseRetryException>(() =>
            service.RequestAsync("claude-dev", "Nothing failed but trying anyway", Guid.NewGuid(), null, CancellationToken.None));

        Assert.False(info.CanRetry);
        Assert.Equal("Only a failed latest run can be retried.", info.UnavailableReason);
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Run_without_a_pipeline_of_this_repository_cannot_be_retried()
    {
        var (cs, drop) = await CreateMigratedDatabaseAsync();
        await using var dropOnExit = drop;
        await MigrateAsync(cs, "SELECT 1/0;", pipelineUrl: "https://github.com/other/repo/actions/runs/5");
        await using var db = CreateDbContext(cs);

        var info = await new DbReleaseRetryService(db, new FakeGitHub(), TimeProvider.System).GetInfoAsync(CancellationToken.None);

        Assert.False(info.CanRetry);
        Assert.Contains("no pipeline run of this repository", info.UnavailableReason);
    }

    [Fact]
    public async Task Validation_configuration_and_github_refusal_are_reported_without_auditing()
    {
        var (cs, drop) = await CreateMigratedDatabaseAsync();
        await using var dropOnExit = drop;
        await MigrateAsync(cs, "SELECT 1/0;");
        await using var db = CreateDbContext(cs);
        var actor = await AddPlatformUserAsync(db);

        var shortReason = await Assert.ThrowsAsync<DomainValidationException>(() =>
            new DbReleaseRetryService(db, new FakeGitHub(), TimeProvider.System).RequestAsync("claude-dev", "too short", actor, null, CancellationToken.None));
        var wrongEnvironment = await Assert.ThrowsAsync<DomainValidationException>(() =>
            new DbReleaseRetryService(db, new FakeGitHub(), TimeProvider.System).RequestAsync("dev", "Retry after fixing the data", actor, null, CancellationToken.None));
        var notConfigured = await Assert.ThrowsAsync<DbReleaseRetryException>(() =>
            new DbReleaseRetryService(db, new FakeGitHub(configured: false), TimeProvider.System).RequestAsync("claude-dev", "Retry after fixing the data", actor, null, CancellationToken.None));
        var refused = await Assert.ThrowsAsync<DbReleaseRetryException>(() =>
            new DbReleaseRetryService(db, new FakeGitHub(accept: false), TimeProvider.System).RequestAsync("claude-dev", "Retry after fixing the data", actor, null, CancellationToken.None));

        Assert.Equal("reason", shortReason.Field);
        Assert.Equal("environment", wrongEnvironment.Field);
        Assert.Equal((503, DbReleaseRetryException.NotConfigured), (notConfigured.Status, notConfigured.Code));
        Assert.Equal((502, DbReleaseRetryException.GitHubRejected), (refused.Status, refused.Code));
        Assert.Contains("403 run is older than 30 days", refused.Message);

        // Validation/configuration refusals write nothing; a GitHub refusal leaves "requested" + "failed" and releases the window.
        var actions = await db.PlatformAuditEvents.AsNoTracking().Where(e => e.Action.StartsWith("platform.db_release_retry"))
            .OrderBy(e => e.OccurredAt).Select(e => e.Action).ToListAsync();
        Assert.Equal([PlatformAuditActions.DbReleaseRetryRequested, PlatformAuditActions.DbReleaseRetryFailed], actions);
        var info = await new DbReleaseRetryService(db, new FakeGitHub(), TimeProvider.System).GetInfoAsync(CancellationToken.None);
        Assert.True(info.CanRetry, info.UnavailableReason);
        Assert.Null(info.LastRequest);
    }

    [Fact]
    public async Task Concurrent_requests_start_exactly_one_rerun()
    {
        var (cs, drop) = await CreateMigratedDatabaseAsync();
        await using var dropOnExit = drop;
        await MigrateAsync(cs, "SELECT 1/0;");
        Guid actor;
        await using (var setup = CreateDbContext(cs)) actor = await AddPlatformUserAsync(setup);
        var github = new FakeGitHub();

        async Task<bool> Attempt()
        {
            await using var db = CreateDbContext(cs); // one context per request, like two API requests
            try
            {
                await new DbReleaseRetryService(db, github, TimeProvider.System).RequestAsync("claude-dev", "Two admins clicked at once", actor, null, CancellationToken.None);
                return true;
            }
            catch (DbReleaseRetryException ex) when (ex.Status == 409) { return false; }
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(Attempt)));

        Assert.Equal(1, results.Count(r => r));
        Assert.Single(github.Reruns);
    }
}
