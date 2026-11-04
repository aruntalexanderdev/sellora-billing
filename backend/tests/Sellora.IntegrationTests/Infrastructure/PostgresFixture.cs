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
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>Builds a context the same way <c>Sellora.Infrastructure.DependencyInjection.AddInfrastructure</c>
    /// does (<c>UseNpgsql(connectionString)</c> with no further options).</summary>
    public static SelloraDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new SelloraDbContext(options);
    }

    /// <summary>Context on the container's default database (not migrated; prefer <see cref="CreateDatabaseAsync"/>).</summary>
    public SelloraDbContext CreateDbContext() => CreateDbContext(ConnectionString);

    /// <summary>Creates a uniquely named empty database on the container and, unless <paramref name="migrate"/> is
    /// false, applies all migrations to it.</summary>
    public async Task<TestDatabase> CreateDatabaseAsync(bool migrate = true)
    {
        var name = "sellora_test_" + Guid.NewGuid().ToString("N");

        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync();
        }

        var database = new TestDatabase(name, new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString, ConnectionString);
        if (migrate)
        {
            await using var db = database.CreateDbContext();
            await db.Database.MigrateAsync();
        }
        return database;
    }
}

/// <summary>An isolated database for one test. Dispose it (<c>await using</c>) to drop the database.</summary>
public sealed class TestDatabase(string name, string connectionString, string adminConnectionString) : IAsyncDisposable
{
    public string Name { get; } = name;

    public string ConnectionString { get; } = connectionString;

    /// <summary>A new, independent context (own connection) on this database.</summary>
    public SelloraDbContext CreateDbContext() => PostgresFixture.CreateDbContext(ConnectionString);

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
