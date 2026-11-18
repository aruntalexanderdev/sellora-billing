using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>Document series codes stored in <c>DocumentSequences.Series</c> (ADR-08).</summary>
internal static class DocumentSeries
{
    public const string Purchase = "PUR";
    public const string PurchaseReturn = "PRT";
    public const string Invoice = "INV";
    public const string SalesReturn = "SRT";
    public const string StockAdjustment = "ADJ";
    public const string Expense = "EXP";
    public const string Transfer = "TRF";
    public const string Shipment = "SHP";
    public const string Consignment = "CON";
}

/// <summary>
/// Number shapes (Phase 2 D4). <c>(CompanyId, No)</c> stays unique, so numbers of different branches must differ by shape:
/// the default branch keeps <c>{SERIES}-{n:D4}</c> (invoices <c>{CompanyPrefix}-{n:D4}</c>); another branch gets
/// <c>{SERIES}-{Code}-{n:D4}</c>; its invoices use its own prefix (<c>{Prefix}-{n:D4}</c>) or <c>{CompanyPrefix}-{Code}-{n:D4}</c>.
/// Expenses stay one company-level <c>EXP-{n:D4}</c> series.
/// </summary>
public static class DocumentNumberFormat
{
    /// <summary>GST invoice numbers are at most 16 characters; the check assumes a 5-digit counter.</summary>
    public const int MaxInvoiceLength = 16;
    public const int WorstCaseCounterDigits = 5;

    public static string Format(string series, Branch? branch, string companyInvoicePrefix, long number)
    {
        var counter = number.ToString("D4");
        if (series == DocumentSeries.Invoice)
        {
            return $"{InvoicePrefix(branch, companyInvoicePrefix)}-{counter}";
        }
        return series == DocumentSeries.Expense || branch is null || branch.IsDefault
            ? $"{series}-{counter}"
            : $"{series}-{branch.Code}-{counter}";
    }

    /// <summary>The prefix invoices of the branch carry (without the counter).</summary>
    public static string InvoicePrefix(Branch? branch, string companyInvoicePrefix) =>
        branch is null || branch.IsDefault
            ? companyInvoicePrefix
            : branch.InvoicePrefix ?? $"{companyInvoicePrefix}-{branch.Code}";

    /// <summary>Longest invoice number the prefix can produce, counter included.</summary>
    public static int WorstCaseInvoiceLength(string prefix) => prefix.Length + 1 + WorstCaseCounterDigits;
}

/// <summary>
/// Gap-free per-company-and-branch document numbering (ADR-08, Phase 2). The counter row is incremented with <c>UPDATE … RETURNING</c>, which takes
/// the row lock until the surrounding transaction ends: concurrent creates of the same company, branch and series queue, other
/// branches and companies never block, and a rolled-back create hands its number back (no gap). Callers must be inside a transaction.
/// </summary>
internal static partial class DocumentNumbers
{
    /// <summary>Next formatted number of a series for the branch (null branch only for the company-level EXP series); advances the counter.</summary>
    public static async Task<string> NextNumberAsync(SelloraDbContext db, string series, Branch? branch, CancellationToken ct)
    {
        if (series == DocumentSeries.Expense)
        {
            return DocumentNumberFormat.Format(series, null, string.Empty, await NextAsync(db, series, null, ct));
        }

        ArgumentNullException.ThrowIfNull(branch);
        var number = await NextAsync(db, series, branch.Id, ct);
        var companyPrefix = series == DocumentSeries.Invoice ? (await db.GetCurrentCompanySettingsAsync(ct)).InvoicePrefix : string.Empty;
        return DocumentNumberFormat.Format(series, branch, companyPrefix, number);
    }

    /// <summary>Returns the next counter value of the series and advances it; <paramref name="branchId"/> null = the company-level series.</summary>
    public static async Task<long> NextAsync(SelloraDbContext db, string series, Guid? branchId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();

        if (db.Database.IsRelational())
        {
            // First document of a company/branch/series without a counter row: create it (concurrent creators wait on the
            // first one's uncommitted row, then do nothing). Both partial unique indexes are targeted explicitly.
            if (branchId is { } insertBranch)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO \"DocumentSequences\" (\"Id\", \"CompanyId\", \"BranchId\", \"Series\", \"NextValue\", \"UpdatedAt\") VALUES ({Guid.NewGuid()}, {companyId}, {insertBranch}, {series}, 1, now()) ON CONFLICT (\"CompanyId\", \"BranchId\", \"Series\") WHERE \"BranchId\" IS NOT NULL DO NOTHING",
                    ct);
            }
            else
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO \"DocumentSequences\" (\"Id\", \"CompanyId\", \"BranchId\", \"Series\", \"NextValue\", \"UpdatedAt\") VALUES ({Guid.NewGuid()}, {companyId}, NULL, {series}, 1, now()) ON CONFLICT (\"CompanyId\", \"Series\") WHERE \"BranchId\" IS NULL DO NOTHING",
                    ct);
            }

            // Raw ADO.NET: EF would wrap a composed UPDATE … RETURNING in a sub-select, which Postgres rejects.
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                var connection = db.Database.GetDbConnection();
                await using var command = connection.CreateCommand();
                command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText =
                    "UPDATE \"DocumentSequences\" SET \"NextValue\" = \"NextValue\" + 1, \"UpdatedAt\" = now() " +
                    "WHERE \"CompanyId\" = @company AND " + (branchId is null ? "\"BranchId\" IS NULL" : "\"BranchId\" = @branch") + " AND \"Series\" = @series " +
                    "RETURNING \"NextValue\" - 1";
                var company = command.CreateParameter();
                company.ParameterName = "company";
                company.Value = companyId;
                command.Parameters.Add(company);
                if (branchId is { } updateBranch)
                {
                    var branchParameter = command.CreateParameter();
                    branchParameter.ParameterName = "branch";
                    branchParameter.Value = updateBranch;
                    command.Parameters.Add(branchParameter);
                }
                var seriesParameter = command.CreateParameter();
                seriesParameter.ParameterName = "series";
                seriesParameter.Value = series;
                command.Parameters.Add(seriesParameter);

                var result = await command.ExecuteScalarAsync(ct);
                return Convert.ToInt64(result);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        // In-memory provider (unit tests): tracked-entity increment, no locking. A missing counter starts after the
        // numbers already present, mirroring the migration's seeding.
        var sequence = db.DocumentSequences.Local.FirstOrDefault(s => s.CompanyId == companyId && s.BranchId == branchId && s.Series == series)
            ?? await db.DocumentSequences.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.BranchId == branchId && s.Series == series, ct);
        if (sequence is null)
        {
            sequence = new DocumentSequence
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                BranchId = branchId,
                Series = series,
                NextValue = await ExistingMaxAsync(db, series, branchId, ct) + 1,
            };
            db.DocumentSequences.Add(sequence);
        }

        var number = sequence.NextValue;
        sequence.NextValue = number + 1;
        sequence.UpdatedAt = DateTimeOffset.UtcNow;
        return number;
    }

    private static async Task<long> ExistingMaxAsync(SelloraDbContext db, string series, Guid? branchId, CancellationToken ct)
    {
        // Documented "Branch"-filter bypass (D7, company-integrity read): numbering must see every row of the series, including
        // other branches' (the EXP series is company-wide); a restricted caller would otherwise reissue existing numbers.
        var numbers = series switch
        {
            DocumentSeries.Purchase => await db.PurchaseOrders.IgnoreQueryFilters([BranchQueryFilter.Name]).Where(x => x.BranchId == branchId).Select(x => x.PurchaseNo).ToListAsync(ct),
            DocumentSeries.PurchaseReturn => await db.PurchaseReturns.IgnoreQueryFilters([BranchQueryFilter.Name]).Where(x => x.BranchId == branchId).Select(x => x.ReturnNo).ToListAsync(ct),
            DocumentSeries.Invoice => await db.SalesInvoices.IgnoreQueryFilters([BranchQueryFilter.Name]).Where(x => x.BranchId == branchId).Select(x => x.InvoiceNo).ToListAsync(ct),
            DocumentSeries.SalesReturn => await db.SalesReturns.IgnoreQueryFilters([BranchQueryFilter.Name]).Where(x => x.BranchId == branchId).Select(x => x.ReturnNo).ToListAsync(ct),
            DocumentSeries.StockAdjustment => await db.StockAdjustments.IgnoreQueryFilters([BranchQueryFilter.Name]).Where(x => x.BranchId == branchId).Select(x => x.AdjustmentNo).ToListAsync(ct),
            DocumentSeries.Transfer => await db.StockTransfers.IgnoreQueryFilters([BranchQueryFilter.Name]).Where(x => x.BranchId == branchId).Select(x => x.TransferNo).ToListAsync(ct),
            DocumentSeries.Expense => await db.Expenses.IgnoreQueryFilters([BranchQueryFilter.Name]).Select(x => x.ExpenseNo).ToListAsync(ct),
            _ => [],
        };

        long max = 0;
        foreach (var number in numbers)
        {
            var match = TrailingDigits().Match(number);
            if (match.Success && long.TryParse(match.Value, out var value) && value > max) max = value;
        }
        return Math.Max(max, numbers.Count);
    }

    [GeneratedRegex("[0-9]+$")]
    private static partial Regex TrailingDigits();
}
