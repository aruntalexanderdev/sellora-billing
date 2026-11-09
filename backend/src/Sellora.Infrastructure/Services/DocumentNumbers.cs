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
}

/// <summary>
/// Gap-free per-company document numbering (ADR-08). The counter row is incremented with <c>UPDATE … RETURNING</c>, which takes
/// the row lock until the surrounding transaction ends: concurrent creates of the same company and series queue, other
/// companies never block, and a rolled-back create hands its number back (no gap). Callers must be inside a transaction.
/// </summary>
internal static partial class DocumentNumbers
{
    /// <summary>Returns the next number of the company-level series and advances the counter.</summary>
    public static async Task<long> NextAsync(SelloraDbContext db, string series, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();

        if (db.Database.IsRelational())
        {
            // First document of a company/series without a counter row: create it (concurrent creators wait on the
            // first one's uncommitted row, then do nothing).
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"DocumentSequences\" (\"Id\", \"CompanyId\", \"BranchId\", \"Series\", \"NextValue\", \"UpdatedAt\") VALUES ({Guid.NewGuid()}, {companyId}, NULL, {series}, 1, now()) ON CONFLICT (\"CompanyId\", \"Series\") WHERE \"BranchId\" IS NULL DO NOTHING",
                ct);

            // Raw ADO.NET: EF would wrap a composed UPDATE … RETURNING in a sub-select, which Postgres rejects.
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                var connection = db.Database.GetDbConnection();
                await using var command = connection.CreateCommand();
                command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText =
                    "UPDATE \"DocumentSequences\" SET \"NextValue\" = \"NextValue\" + 1, \"UpdatedAt\" = now() " +
                    "WHERE \"CompanyId\" = @company AND \"BranchId\" IS NULL AND \"Series\" = @series " +
                    "RETURNING \"NextValue\" - 1";
                var company = command.CreateParameter();
                company.ParameterName = "company";
                company.Value = companyId;
                command.Parameters.Add(company);
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
        var sequence = db.DocumentSequences.Local.FirstOrDefault(s => s.CompanyId == companyId && s.BranchId == null && s.Series == series)
            ?? await db.DocumentSequences.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.BranchId == null && s.Series == series, ct);
        if (sequence is null)
        {
            sequence = new DocumentSequence
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                Series = series,
                NextValue = await ExistingMaxAsync(db, series, ct) + 1,
            };
            db.DocumentSequences.Add(sequence);
        }

        var number = sequence.NextValue;
        sequence.NextValue = number + 1;
        sequence.UpdatedAt = DateTimeOffset.UtcNow;
        return number;
    }

    private static async Task<long> ExistingMaxAsync(SelloraDbContext db, string series, CancellationToken ct)
    {
        var numbers = series switch
        {
            DocumentSeries.Purchase => await db.PurchaseOrders.Select(x => x.PurchaseNo).ToListAsync(ct),
            DocumentSeries.PurchaseReturn => await db.PurchaseReturns.Select(x => x.ReturnNo).ToListAsync(ct),
            DocumentSeries.Invoice => await db.SalesInvoices.Select(x => x.InvoiceNo).ToListAsync(ct),
            DocumentSeries.SalesReturn => await db.SalesReturns.Select(x => x.ReturnNo).ToListAsync(ct),
            DocumentSeries.StockAdjustment => await db.StockAdjustments.Select(x => x.AdjustmentNo).ToListAsync(ct),
            DocumentSeries.Expense => await db.Expenses.Select(x => x.ExpenseNo).ToListAsync(ct),
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
