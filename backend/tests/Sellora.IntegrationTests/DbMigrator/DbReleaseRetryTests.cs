using Sellora.Infrastructure.Data;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.DbMigrator;

[Collection(PostgresCollection.Name)]
public sealed class DbReleaseRetryTests(PostgresFixture postgres) : DbReleaseRetryScenarios
{
    protected override async Task<(string ConnectionString, IAsyncDisposable Drop)> CreateMigratedDatabaseAsync()
    {
        var database = await postgres.CreateDatabaseAsync(migrate: true);
        return (database.ConnectionString, database);
    }

    protected override SelloraDbContext CreateDbContext(string connectionString) => PostgresFixture.CreateDbContext(connectionString);
}
