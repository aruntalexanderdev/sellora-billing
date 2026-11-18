using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Infrastructure.Data;
using Testcontainers.PostgreSql;

namespace Sellora.IntegrationTests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}

/// <summary>
/// One PostgreSQL container (postgres:17-alpine) shared by every test in the "Postgres" collection. Tests do
/// not share data: each calls <see cref="CreateDatabaseAsync"/> to get its own freshly created, fully migrated
/// database. Requires a reachable Docker daemon; if there is none the fixture fails loudly rather than skipping.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:17-alpine";

    private PostgreSqlContainer? _container;

    /// <summary>Connection string to the container's default (maintenance) database.</summary>
    public string ConnectionString => (_container ?? throw new InvalidOperationException("The PostgreSQL container has not been started."))
        .GetConnectionString();

    public async Task InitializeAsync()
    {
        try
        {
            // Build() is inside the try as well: Testcontainers resolves the Docker endpoint there and throws if none is found.
            _container = new PostgreSqlBuilder(Image).Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Sellora.IntegrationTests need Docker to run PostgreSQL ({Image}) via Testcontainers, but the container " +
                "could not be started. Make sure a Docker daemon is running and reachable by the current user " +
                "(`docker ps` must work), then re-run the tests. These tests are not skipped when Docker is missing. " +
                $"Underlying error: {ex.GetType().Name}: {ex.Message}", ex);
        }
        await CreateTemplateAsync();
    }

    /// <summary>
    /// CI speed-up: the fully migrated schema is built ONCE per container in this database; every <see cref="CreateDatabaseAsync"/>
    /// with <c>migrate: true</c> is then a file-level copy (<c>CREATE DATABASE … TEMPLATE …</c>, milliseconds) instead of replaying
    /// every migration (~2 s each). The copy is identical to a freshly migrated database (same history rows, same migration-inserted
    /// data), so tests see exactly what they saw before. Tests that need a specific or empty schema (migration tests, the startup
    /// migration check) keep using <c>migrate: false</c> and migrate themselves. The template refuses connections once built, so
    /// nothing can change it or block a copy.
    /// </summary>
    public const string TemplateName = "sellora_template";

    private async Task CreateTemplateAsync()
    {
        await ExecuteAdminAsync($"CREATE DATABASE \"{TemplateName}\"");
        // No pooling: no idle connection may survive on the template, or a copy fails ("source database is being accessed by other users").
        var templateConnection = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = TemplateName, Pooling = false }.ConnectionString;
        await using (var db = CreateDbContext(templateConnection))
        {
            await db.Database.MigrateAsync();
        }
        // The server ends a closed session asynchronously; copying while it is still listed fails with 55006 "being accessed by other users".
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            for (var attempt = 0; ; attempt++)
            {
                await using var check = connection.CreateCommand();
                check.CommandText = $"SELECT count(*) FROM pg_stat_activity WHERE datname = '{TemplateName}'";
                if ((long)(await check.ExecuteScalarAsync())! == 0) break;
                if (attempt >= 100) throw new InvalidOperationException($"{TemplateName} still has open sessions after migrating it.");
                await Task.Delay(50);
            }
        }
        await ExecuteAdminAsync($"ALTER DATABASE \"{TemplateName}\" WITH IS_TEMPLATE true ALLOW_CONNECTIONS false");
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>Builds a context the same way <c>Sellora.Infrastructure.DependencyInjection.AddInfrastructure</c>
    /// does (<c>UseNpgsql(connectionString)</c> with no further options).</summary>
    public static SelloraDbContext CreateDbContext(string connectionString, Guid? companyId = null, Sellora.Domain.Abstractions.IBranchContext? branch = null)
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        // Default company unless a test asks for another one; a context's company is fixed once set.
        return new SelloraDbContext(options, Sellora.IntegrationTests.TestData.TestCompany.For(companyId ?? Sellora.Domain.Companies.DefaultTenant.CompanyId),
            branch ?? Sellora.IntegrationTests.TestData.TestCompany.Branch);
    }

    /// <summary>Result of a psql run inside the container.</summary>
    public sealed record PsqlResult(long ExitCode, string Stdout, string Stderr);

    /// <summary>Runs a psql script (meta-commands such as \if and :'var' included) inside the container against <paramref name="databaseName"/>,
    /// exactly as an operator would (<c>psql -v ON_ERROR_STOP=1 -v name=value -f script</c>); the host needs no psql client.</summary>
    public async Task<PsqlResult> RunPsqlScriptAsync(string databaseName, string scriptText, IReadOnlyDictionary<string, string>? variables = null)
    {
        var container = _container ?? throw new InvalidOperationException("The PostgreSQL container has not been started.");
        var path = $"/tmp/{Guid.NewGuid():N}.sql";
        await container.CopyAsync(System.Text.Encoding.UTF8.GetBytes(scriptText), path);
        var command = new List<string> { "psql", "-U", "postgres", "-d", databaseName, "-v", "ON_ERROR_STOP=1" };
        foreach (var (name, value) in variables ?? new Dictionary<string, string>()) { command.Add("-v"); command.Add($"{name}={value}"); }
        command.Add("-f"); command.Add(path);
        var result = await container.ExecAsync(command);
        return new PsqlResult(result.ExitCode ?? -1, result.Stdout, result.Stderr);
    }

    /// <summary>Context on the container's default database (not migrated; prefer <see cref="CreateDatabaseAsync"/>).</summary>
    public SelloraDbContext CreateDbContext() => CreateDbContext(ConnectionString);

    /// <summary>
    /// Connection string for a per-test database (FI-D43h). Shared worlds and class fixtures keep their databases (and hosts) alive for a whole run, and every one of them used to keep an idle
    /// pooled connection until the end, so the full suite approached the container's 100-connection cap (53300) and only passed in a lucky order. Idle connections are now pruned within
    /// ~2-3 s (Npgsql's pool keeps none below <c>Minimum Pool Size</c>=0), so the number of open server connections follows what is in use right now, whatever the test order.
    /// </summary>
    public static string ConnectionStringFor(string adminConnectionString, string databaseName) => new NpgsqlConnectionStringBuilder(adminConnectionString)
    {
        Database = databaseName,
        MinPoolSize = 0,
        ConnectionIdleLifetime = 2,
        ConnectionPruningInterval = 1,
    }.ConnectionString;

    /// <summary>Creates a uniquely named database on the container: a copy of the fully migrated <see cref="TemplateName"/>
    /// (the schema and data every migration produces), or an empty one when <paramref name="migrate"/> is false.</summary>
    public async Task<TestDatabase> CreateDatabaseAsync(bool migrate = true)
    {
        var name = "sellora_test_" + Guid.NewGuid().ToString("N");
        await ExecuteAdminAsync(migrate ? $"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateName}\"" : $"CREATE DATABASE \"{name}\"");
        return new TestDatabase(name, ConnectionStringFor(ConnectionString, name), ConnectionString);
    }
}

/// <summary>An isolated database for one test. Dispose it (<c>await using</c>) to drop the database.</summary>
public sealed class TestDatabase(string name, string connectionString, string adminConnectionString) : IAsyncDisposable
{
    public string Name { get; } = name;

    public string ConnectionString { get; } = connectionString;

    /// <summary>A new, independent context (own connection) on this database.</summary>
    public SelloraDbContext CreateDbContext(Guid? companyId = null, Sellora.Domain.Abstractions.IBranchContext? branch = null) => PostgresFixture.CreateDbContext(ConnectionString, companyId, branch);

    /// <summary>Releases pooled connections to this database and drops it (forcing off any remaining sessions).</summary>
    public async ValueTask DisposeAsync()
    {
        await using (var pooled = new NpgsqlConnection(ConnectionString))
        {
            NpgsqlConnection.ClearPool(pooled);
        }

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}
