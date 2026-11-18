using System.Text.RegularExpressions;
using Npgsql;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.TestData;

/// <summary>Runs a read-only backend/db/validation (V__) script statement by statement against a test database; every statement must return zero rows (FI-D43b/c).</summary>
public static class ValidationScripts
{
    public static string Text(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "db", "validation", "V__" + name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException($"backend/db/validation/V__{name} not found above {AppContext.BaseDirectory}");
    }

    public static List<(string Id, string Sql)> Statements(string file)
    {
        var result = new List<(string, string)>();
        var current = new List<string>();
        foreach (var line in Text(file).Split('\n'))
        {
            if (line.TrimStart().StartsWith("--")) continue;
            if (string.IsNullOrWhiteSpace(line) && current.Count == 0) continue;
            current.Add(line);
            if (!line.TrimEnd().EndsWith(';')) continue;
            var sql = string.Join('\n', current);
            var id = Regex.Match(sql, @"'([\w-]+)'\s+AS\s+chk", RegexOptions.IgnoreCase);
            result.Add((id.Success ? id.Groups[1].Value : $"stmt#{result.Count + 1}", sql));
            current.Clear();
        }
        Assert.Empty(current);
        return result;
    }

    public static async Task AssertZeroRowsAsync(TestDatabase database, string file, int minimumStatements, string when)
    {
        var statements = Statements(file);
        Assert.True(statements.Count >= minimumStatements, $"{file}: only {statements.Count} statements parsed (expected >= {minimumStatements}).");
        foreach (var (id, sql) in statements)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync()) rows.Add(string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i)))));
            Assert.True(rows.Count == 0, $"{when}: {file} {id} returned {rows.Count} row(s): {string.Join(" | ", rows.Take(5))}");
        }
    }
}
