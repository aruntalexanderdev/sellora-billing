using Microsoft.EntityFrameworkCore;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.TestData;

/// <summary>Runs <c>backend/db/validation/V__van-operations-validation.sql</c> (R1-R5) exactly as shipped: every statement must return no rows (reuses the statement splitter of <see cref="TransferValidation"/>).</summary>
public static class VanValidation
{
    public static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend", "db", "validation", "V__van-operations-validation.sql"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "backend", "db", "validation", "V__van-operations-validation.sql");
    }

    public static List<string> Statements()
    {
        var sql = string.Join('\n', File.ReadAllLines(ScriptPath()).Where(l => !l.TrimStart().StartsWith("--")));
        return sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s.Length > 0).ToList();
    }

    public static async Task AssertCleanAsync(TestDatabase database, string step)
    {
        var statements = Statements();
        Assert.Equal(5, statements.Count); // R1 R2 R3 R4 R5
        await using var db = database.CreateDbContext();
        foreach (var statement in statements)
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            var offenders = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) offenders.Add(string.Join("/", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
            }
            Assert.True(offenders.Count == 0, $"after {step}: van validation script returned rows: {string.Join("; ", offenders.Take(5))} for {statement[..Math.Min(40, statement.Length)]}");
        }
    }
}
