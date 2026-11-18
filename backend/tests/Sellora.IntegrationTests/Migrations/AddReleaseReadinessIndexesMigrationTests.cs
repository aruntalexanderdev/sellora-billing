using Npgsql;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Phase 09 <c>AddReleaseReadinessIndexes</c> (FI-D43 e): additive indexes only. Up creates the six indexes (and drops the one the new account index supersedes), tolerates a pre-created index,
/// Down restores the previous schema exactly, and the partial open-discrepancy index is actually chosen for the transit "discrepancies" predicate.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddReleaseReadinessIndexesMigrationTests(PostgresFixture postgres)
{
    private const string Previous = "20261117095458_AddReceiptApprovalAndDispositions";
    private const string This = "20261117105841_AddReleaseReadinessIndexes";

    private static readonly string[] New =
    [
        "IX_FinancialTransactions_CompanyId_Date_CreatedAt_Id", "IX_FinancialTransactions_CompanyId_TransferGroupId", "IX_FinancialTransactions_CompanyId_AccountId_Status",
        "IX_Consignments_CompanyId_Status_DispatchedAt", "IX_StockTransfers_CompanyId_Status_ApprovedAt", "IX_ConsignmentLines_CompanyId_ConsignmentId_OpenDiscrepancy",
    ];

    private static async Task<List<string>> IndexDefsAsync(TestDatabase d) =>
        await QueryAsync(d, $"""SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname IN ({string.Join(", ", New.Select(n => $"'{n}'"))}) ORDER BY indexname""", r => r.GetString(0));

    [Fact]
    public async Task Up_CreatesTheIndexes_WithTheExpectedDefinitions_AndDropsOnlyTheSupersededOne()
    {
        await using var database = await CreateAtAsync(postgres, Previous);
        Assert.Empty(await IndexDefsAsync(database));
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_FinancialTransactions_CompanyId_AccountId'"""));

        await MigrateToAsync(database, This);

        var defs = await IndexDefsAsync(database);
        Assert.Equal(New.Length, defs.Count);
        Assert.Contains(defs, d => d.Contains("\"CompanyId\", \"TransactionDate\" DESC, \"CreatedAt\" DESC, \"Id\""));
        Assert.Contains(defs, d => d.Contains("\"TransferGroupId\"") && d.Contains("WHERE (\"TransferGroupId\" IS NOT NULL)"));
        Assert.Contains(defs, d => d.Contains("(\"CompanyId\", \"AccountId\", \"Status\") INCLUDE (\"Direction\", \"BaseAmount\", \"ReconciledAt\")"));
        Assert.Contains(defs, d => d.Contains("\"Consignments\"") && d.Contains("\"DispatchedAt\""));
        Assert.Contains(defs, d => d.Contains("\"StockTransfers\"") && d.Contains("\"ApprovedAt\""));
        Assert.Contains(defs, d => d.Contains("\"ConsignmentLines\"") && d.Contains("'Active'") && d.Contains("DispositionedMissingBaseQty"));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_FinancialTransactions_CompanyId_AccountId'"""));
        // The plain FK index on consignment lines stays (a partial index cannot serve the FK).
        Assert.Equal(1L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_ConsignmentLines_CompanyId_ConsignmentId'"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM pg_index WHERE NOT indisvalid"""));
    }

    [Fact]
    public async Task Up_ToleratesIndexesThatTheDbaPreCreated()
    {
        await using var database = await CreateAtAsync(postgres, Previous);
        // CONCURRENTLY cannot share a command (implicit transaction) with another statement.
        await ExecuteAsync(database, """CREATE INDEX CONCURRENTLY "IX_Consignments_CompanyId_Status_DispatchedAt" ON "Consignments" ("CompanyId", "Status", "DispatchedAt")""");
        await ExecuteAsync(database, """CREATE INDEX CONCURRENTLY "IX_StockTransfers_CompanyId_Status_ApprovedAt" ON "StockTransfers" ("CompanyId", "Status", "ApprovedAt")""");

        await MigrateToAsync(database, This);

        Assert.Equal(New.Length, (await IndexDefsAsync(database)).Count);
        Assert.Contains(This, await AppliedAsync(database));
    }

    [Fact]
    public async Task Down_RestoresThePreviousSchemaExactly()
    {
        await using var reference = await CreateAtAsync(postgres, Previous);
        var expected = await SchemaSignatureAsync(reference);
        await using var database = await CreateAtAsync(postgres, This);
        Assert.Equal(New.Length, (await IndexDefsAsync(database)).Count);

        await MigrateToAsync(database, Previous);

        Assert.Empty(await IndexDefsAsync(database));
        Assert.Equal(expected, await SchemaSignatureAsync(database));
    }

    [Fact]
    public async Task OpenDiscrepancyIndex_IsUsedForTheTransitDiscrepanciesPredicate()
    {
        await using var database = await CreateAtAsync(postgres, This);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        // Same shape EF emits for ConsignmentLines.Any(l => Active && damaged + rejected + missing - dispositioned... > 0), correlated on the consignment.
        await using var command = new NpgsqlCommand("""
            SET enable_seqscan = off;
            EXPLAIN SELECT 1 FROM "Consignments" c WHERE EXISTS (
                SELECT 1 FROM "ConsignmentLines" l WHERE c."Id" = l."ConsignmentId" AND l."CompanyId" = c."CompanyId" AND l."Status" = 'Active'
                AND l."DamagedBaseQty" + l."RejectedBaseQty" + l."MissingBaseQty" - l."DispositionedDamagedBaseQty" - l."DispositionedRejectedBaseQty" - l."DispositionedMissingBaseQty" > 0)
            """, connection);
        var plan = new System.Text.StringBuilder();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) plan.AppendLine(reader.GetString(0));
        }
        Assert.Contains("IX_ConsignmentLines_CompanyId_ConsignmentId_OpenDiscrepancy", plan.ToString());
    }
}
