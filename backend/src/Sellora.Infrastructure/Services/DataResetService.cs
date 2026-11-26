using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Platform;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// The embedded scripts/sql/dev-reset-fresh-setup.sql (linked into this assembly, logical name <see cref="ResourceName"/>). The
/// Platform Console runs exactly the text an operator would paste into a SQL tool: the reset list, the fail-closed checks and the
/// all-or-nothing statement exist once. Only the four operator values at the top of the script's DO block are filled in here.
/// </summary>
internal static partial class DataResetScript
{
    public const string ResourceName = "dev-reset-fresh-setup.sql";

    /// <summary>First words of the script's RESULT row when it only checked, and when it reset.</summary>
    public const string CheckOnlyPrefix = "CHECK ONLY";
    public const string DonePrefix = "RESET DONE";

    public static string Read()
    {
        using var stream = typeof(DataResetScript).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded reset script '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Fills in the four operator values. Fails closed: each value line must exist exactly once in its committed, disarmed
    /// form, otherwise nothing is run.</summary>
    public static string Arm(string script, string database, bool apply, DataResetOptions options)
    {
        script = ReplaceOnce(script, ConfirmLine(), Literal(database), "v_confirm_database");
        script = ReplaceOnce(script, ApplyLine(), apply ? "true" : "false", "v_apply");
        script = ReplaceOnce(script, WipeLine(), Literal(options.WipeList), "v_wipe");
        return ReplaceOnce(script, KeepLine(), Literal(options.KeepList), "v_keep");
    }

    private static string ReplaceOnce(string script, Regex line, string value, string name)
    {
        if (line.Matches(script).Count != 1)
        {
            throw new InvalidOperationException($"The reset script does not contain exactly one disarmed '{name}' line; refusing to run it.");
        }
        return line.Replace(script, match => match.Groups[1].Value + value + ";");
    }

    /// <summary>A SQL string literal placed inside the script's dollar-quoted DO block: quotes are doubled, and a value that could
    /// end the block or break the line is refused.</summary>
    private static string Literal(string value)
    {
        if (value.Contains('$') || value.Any(char.IsControl))
        {
            throw new DataResetRefusedException("The database name contains characters the reset script cannot carry safely. Nothing was changed.");
        }
        return "'" + value.Replace("'", "''") + "'";
    }

    [GeneratedRegex(@"(v_confirm_database\s+constant text\s+:= )'TYPE-THE-DATABASE-NAME-HERE';")]
    private static partial Regex ConfirmLine();

    [GeneratedRegex(@"(v_apply\s+constant boolean\s+:= )false;")]
    private static partial Regex ApplyLine();

    [GeneratedRegex(@"(v_wipe\s+constant text\s+:= )'';")]
    private static partial Regex WipeLine();

    [GeneratedRegex(@"(v_keep\s+constant text\s+:= )'';")]
    private static partial Regex KeepLine();
}

/// <summary>Platform Console data reset (<see cref="IDataResetService"/>). Runs the embedded reset script inside one transaction on
/// the request's connection, with the session switch the script requires set for that transaction only.</summary>
public class DataResetService(SelloraDbContext db, IOptions<PlatformOptions> options, ILogger<DataResetService> logger) : IDataResetService
{
    public bool IsEnabled => options.Value.DataResetEnabled;

    public Task<DataResetReport> PreviewAsync(DataResetOptions resetOptions, CancellationToken ct = default)
    {
        EnsureEnabled();
        return RunAsync(resetOptions, apply: false, confirmDatabase: null, actorUserId: null, correlationId: null, ct);
    }

    public Task<DataResetReport> ResetAsync(DataResetOptions resetOptions, string confirmDatabase, Guid actorUserId, string? correlationId, CancellationToken ct = default)
    {
        EnsureEnabled();
        return RunAsync(resetOptions, apply: true, confirmDatabase, actorUserId, correlationId, ct);
    }

    private void EnsureEnabled()
    {
        if (!IsEnabled) throw new DataResetDisabledException();
    }

    private async Task<DataResetReport> RunAsync(
        DataResetOptions resetOptions, bool apply, string? confirmDatabase, Guid? actorUserId, string? correlationId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = db.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();

        string database;
        await using (var command = NewCommand(connection, dbTransaction, "SELECT current_database()"))
        {
            database = (string)(await command.ExecuteScalarAsync(ct))!;
        }
        if (apply && !string.Equals(confirmDatabase?.Trim(), database, StringComparison.Ordinal))
        {
            throw new DomainValidationException("confirmDatabase", "Type the name of the connected database exactly to confirm the reset.");
        }

        // Transaction-local (is_local = true): the switch the script requires, and who runs it for the audit row. Nothing stays on
        // the pooled connection.
        await using (var command = NewCommand(connection, dbTransaction,
            "SELECT set_config('sellora.allow_dev_reset', 'on', true), set_config('sellora.reset_actor_user_id', @actor, true), set_config('sellora.reset_correlation_id', @correlation, true)"))
        {
            AddParameter(command, "actor", actorUserId?.ToString("D") ?? string.Empty);
            AddParameter(command, "correlation", correlationId is { Length: > 64 } c ? c[..64] : correlationId ?? string.Empty);
            await command.ExecuteNonQueryAsync(ct);
        }

        string? result = null;
        var tables = new List<DataResetTable>();
        try
        {
            await using var command = NewCommand(connection, dbTransaction, DataResetScript.Arm(DataResetScript.Read(), database, apply, resetOptions));
            command.CommandTimeout = 300;
            // The script's last statement returns its result sheet: one RESULT row, then one row per table.
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var area = reader.GetString(0);
                var action = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                if (area == "RESULT")
                {
                    result = action;
                    continue;
                }
                tables.Add(new DataResetTable(area, reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), action));
            }
        }
        catch (DbException ex)
        {
            // Lock timeout, missing privilege, ...: the script's single statement failed as a whole. Disposing the transaction rolls
            // back. The database's own message stays in the log (it can name tables and constraints); the client gets an authored one.
            logger.LogError(ex, "Platform data reset of {Database} did not complete (apply: {Apply}, request {CorrelationId}); nothing was changed.",
                database, apply, correlationId);
            throw new DataResetRefusedException(ex.SqlState switch
            {
                "55P03" => "The tables are in use: a lock was not granted within 15 seconds. Nothing was changed. Stop other activity and try again.",
                "42501" => "The API's database login is not allowed to empty these tables (it must own them). Nothing was changed.",
                _ => "The reset did not complete and nothing was changed. The cause is in the API log.",
            });
        }

        if (result is null || !result.StartsWith(apply ? DataResetScript.DonePrefix : DataResetScript.CheckOnlyPrefix, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(ct);
            throw new DataResetRefusedException(result ?? "The reset script returned no result. Nothing was changed.");
        }

        var seedNote = string.Empty;
        if (apply)
        {
            // T23: taxes not kept: the standard profiles and company default are back before the transaction ends; kept: a missing company default is restored (T21).
            // Same connection and transaction, one context per company (a context's company is fixed once set).
            seedNote = await RestoreTaxSetupAsync(connection, dbTransaction, resetOptions, ct);
        }

        if (apply)
        {
            // The result sheet is a session temp table: do not leave it on the pooled connection.
            await using (var command = NewCommand(connection, dbTransaction, "DROP TABLE IF EXISTS pg_temp.sellora_reset_report"))
            {
                await command.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        else
        {
            await transaction.RollbackAsync(ct);
        }

        return new DataResetReport(database, apply,
            (apply ? "Reset done: business data removed and document numbers reset." : "Preview only: nothing was changed.") + seedNote,
            resetOptions.KeptMasters, tables);
    }

    /// <summary>Re-seeds (taxes not kept) or restores the company default of (taxes kept) every company that has a country. Returns the sentence for the report
    /// message. Companies without a country, and countries without a standard seed, are named in the sentence.</summary>
    private async Task<string> RestoreTaxSetupAsync(DbConnection connection, DbTransaction dbTransaction, DataResetOptions resetOptions, CancellationToken ct)
    {
        var companies = new List<(Guid CompanyId, Guid? CountryId, string? Code)>();
        await using (var command = NewCommand(connection, dbTransaction,
            "SELECT b.\"CompanyId\", b.\"CountryId\", c.\"Code\" FROM \"BusinessSettings\" b LEFT JOIN \"Countries\" c ON c.\"Id\" = b.\"CountryId\" ORDER BY b.\"CompanyId\""))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                companies.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var options = new DbContextOptionsBuilder<SelloraDbContext>().UseNpgsql(connection).Options;
        int added = 0, companiesSeeded = 0, defaultsRestored = 0;
        var noSeed = new List<string>();
        var noCountry = 0;
        foreach (var (companyId, countryId, code) in companies)
        {
            if (countryId is not { } country) { noCountry++; continue; }
            await using var scoped = new SelloraDbContext(options, CompanyContext.For(companyId), BranchContext.Unrestricted());
            await scoped.Database.UseTransactionAsync(dbTransaction, ct);
            if (resetOptions.KeepTaxes)
            {
                if (await TaxSeeder.RestoreCompanyDefaultAsync(scoped, companyId, country, null, ct)) defaultsRestored++;
            }
            else
            {
                var result = await TaxSeeder.SeedCompanyAsync(scoped, companyId, country, null, ct);
                if (!result.CountrySupported) noSeed.Add(code ?? "?");
                else { added += result.ProfilesAdded; companiesSeeded++; }
            }
            await scoped.SaveChangesAsync(ct);
        }

        var parts = new List<string>();
        if (resetOptions.KeepTaxes) parts.Add(defaultsRestored == 0 ? "tax profiles kept, no company default had to be restored" : $"tax profiles kept, company default restored for {defaultsRestored} compan{(defaultsRestored == 1 ? "y" : "ies")}");
        else parts.Add($"standard tax profiles re-seeded for {companiesSeeded} compan{(companiesSeeded == 1 ? "y" : "ies")} ({added} profiles)");
        if (noSeed.Count > 0) parts.Add($"no standard tax seed exists for {string.Join(", ", noSeed.Distinct())}: those companies have no tax profiles until they create them");
        if (noCountry > 0) parts.Add($"{noCountry} compan{(noCountry == 1 ? "y has" : "ies have")} no country and no tax profiles");
        var text = string.Join("; ", parts);
        return " Tax setup: " + text + ".";
    }

    private static DbCommand NewCommand(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
