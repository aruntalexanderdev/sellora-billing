using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// FI-D25 <c>AddStockItemsNonNegativeCheck</c>: the CHECK rejects a negative on-hand, the guard refuses (and rolls back) when a negative row already exists
/// without touching data, the shipped pre-deploy script lists the offender, and Down removes the constraint.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AddStockItemsNonNegativeCheckMigrationTests(PostgresFixture postgres)
{
    private const string Previous = "20261113032716_AddCashBankFundingAndReversal";
    private const string This = "20261113052229_AddStockItemsNonNegativeCheck";
    private const string Constraint = "CK_StockItems_QuantityOnHand_NonNegative";
    private static string G(int n) => $"00000000-0000-0000-0000-{n:x12}";

    private static Task<long> ConstraintCountAsync(TestDatabase database) =>
        ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM pg_constraint WHERE conname = '{Constraint}'""");

    [Fact]
    public async Task Up_CleanDatabase_AddsConstraint_AndRejectsNegativeUpdate()
    {
        await using var database = await CreateAtAsync(postgres, Previous);
        var before = await ScalarAsync<decimal>(database, """SELECT SUM("QuantityOnHand") FROM "StockItems" """);

        await MigrateToAsync(database, This);

        Assert.Equal(1L, await ConstraintCountAsync(database));
        Assert.Equal(before, await ScalarAsync<decimal>(database, """SELECT SUM("QuantityOnHand") FROM "StockItems" """));
        await ExecuteAsync(database, $"""UPDATE "StockItems" SET "QuantityOnHand" = 0 WHERE "ProductId" = '{G(20)}'""");
        await AssertRaisesAsync(() => ExecuteAsync(database, $"""UPDATE "StockItems" SET "QuantityOnHand" = -0.001 WHERE "ProductId" = '{G(20)}'"""), Constraint);
    }

    [Fact]
    public async Task Up_NegativeRowExists_GuardRefusesListsRowAndLeavesSchemaAtPrevious()
    {
        await using var database = await CreateAtAsync(postgres, Previous);
        await ExecuteAsync(database, $"""UPDATE "StockItems" SET "QuantityOnHand" = -2.5 WHERE "ProductId" = '{G(20)}'""");
        var schema = await SchemaSignatureAsync(database);
        var history = await AppliedAsync(database);

        var ex = await AssertRaisesAsync(() => MigrateToAsync(database, This), "negative QuantityOnHand");

        Assert.Contains(G(20), ex.MessageText);
        Assert.Contains("-2.500", ex.MessageText);
        Assert.Contains("stock-items-negative-check.sql", ex.MessageText);
        Assert.Equal(schema, await SchemaSignatureAsync(database));
        Assert.Equal(history, await AppliedAsync(database));
        Assert.Equal(0L, await ConstraintCountAsync(database));
        Assert.Equal(-2.5m, await ScalarAsync<decimal>(database, $"""SELECT "QuantityOnHand" FROM "StockItems" WHERE "ProductId" = '{G(20)}'"""));

        var listed = await QueryAsync(database, File.ReadAllText(ScriptPath()).Trim().TrimEnd(';'), r => r.GetGuid(3).ToString());
        Assert.Equal([G(20)], listed);
    }

    [Fact]
    public async Task Down_RemovesConstraint_AndRestoresPreviousSchema()
    {
        await using var reference = await CreateAtAsync(postgres, Previous);
        var expected = await SchemaSignatureAsync(reference);
        await using var database = await CreateAtAsync(postgres, This);
        Assert.Equal(1L, await ConstraintCountAsync(database));

        await MigrateToAsync(database, Previous);

        Assert.Equal(0L, await ConstraintCountAsync(database));
        Assert.Equal(expected, await SchemaSignatureAsync(database));
        await ExecuteAsync(database, $"""UPDATE "StockItems" SET "QuantityOnHand" = -1 WHERE "ProductId" = '{G(20)}'""");
    }

    private static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", "sql", "stock-items-negative-check.sql"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "scripts", "sql", "stock-items-negative-check.sql");
    }
}
