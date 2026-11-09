using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Accounts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

public class FinancialAccountService(SelloraDbContext db) : IFinancialAccountService
{
    public async Task<FinancialAccount> CreateAsync(CreateAccountInput input, Guid userId, CancellationToken ct = default)
    {
        var name = input.Name.Trim();
        if (name.Length == 0)
        {
            throw new DomainValidationException("Name", "Account name is required.");
        }
        if (await db.FinancialAccounts.AnyAsync(a => a.Name.ToLower() == name.ToLower(), ct))
        {
            throw new DomainValidationException("Name", "An account with this name already exists.");
        }
        if (input.OpeningBalance < 0)
        {
            throw new DomainValidationException("OpeningBalance", "Opening balance cannot be negative.");
        }
        EnsureNotFuture(input.OpeningDate, "OpeningDate");

        var branch = input.BranchId is { } branchId
            ? await BranchRules.EnsureCanUseAsync(db, branchId, requireActive: true, "BranchId", ct)
            : null;

        var (baseCurrencyId, basePrecision) = await BaseCurrencyAsync(ct);
        var opening = Math.Round(input.OpeningBalance, basePrecision, MidpointRounding.AwayFromZero);

        var account = new FinancialAccount
        {
            Id = Guid.NewGuid(), Name = name, Kind = input.Kind, Reference = Clean(input.Reference), BranchId = branch?.Id, CreatedByUserId = userId,
        };
        db.FinancialAccounts.Add(account);

        if (opening > 0)
        {
            db.FinancialTransactions.Add(Manual(FinancialTransactionType.OpeningBalance, MoneyDirection.In, account.Id, opening, input.OpeningDate, baseCurrencyId, "Opening balance", userId));
        }

        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<FinancialAccount?> UpdateAsync(Guid accountId, UpdateAccountInput input, CancellationToken ct = default)
    {
        var account = await db.FinancialAccounts.SingleOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null) return null;

        var name = input.Name.Trim();
        if (name.Length == 0)
        {
            throw new DomainValidationException("Name", "Account name is required.");
        }
        if (await db.FinancialAccounts.AnyAsync(a => a.Id != accountId && a.Name.ToLower() == name.ToLower(), ct))
        {
            throw new DomainValidationException("Name", "An account with this name already exists.");
        }

        if (input.BranchId != account.BranchId)
        {
            if (input.BranchId is { } newBranchId)
            {
                await BranchRules.EnsureCanUseAsync(db, newBranchId, requireActive: true, "BranchId", ct);
            }
            account.BranchId = input.BranchId;
        }

        account.Name = name;
        account.Reference = Clean(input.Reference);
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task TransferAsync(TransferInput input, Guid userId, CancellationToken ct = default)
    {
        if (input.FromAccountId == input.ToAccountId)
        {
            throw new DomainValidationException("ToAccountId", "Choose a different account to transfer to.");
        }

        var accounts = await db.FinancialAccounts
            .Where(a => a.Id == input.FromAccountId || a.Id == input.ToAccountId).ToListAsync(ct);
        var from = accounts.SingleOrDefault(a => a.Id == input.FromAccountId)
            ?? throw new DomainValidationException("FromAccountId", "Select a valid account to transfer from.");
        var to = accounts.SingleOrDefault(a => a.Id == input.ToAccountId)
            ?? throw new DomainValidationException("ToAccountId", "Select a valid account to transfer to.");
        if (!from.IsActive) throw new DomainValidationException("FromAccountId", $"{from.Name} is inactive.");
        if (!to.IsActive) throw new DomainValidationException("ToAccountId", $"{to.Name} is inactive.");

        var (baseCurrencyId, basePrecision) = await BaseCurrencyAsync(ct);
        var amount = Math.Round(input.Amount, basePrecision, MidpointRounding.AwayFromZero);
        if (amount <= 0)
        {
            throw new DomainValidationException("Amount", "Amount must be greater than zero.");
        }
        EnsureNotFuture(input.TransferDate, "TransferDate");

        // Both legs share a group id and are saved by the one SaveChanges, so a transfer is never half-written.
        var group = Guid.NewGuid();
        var notes = Clean(input.Notes);
        var outLeg = Manual(FinancialTransactionType.Transfer, MoneyDirection.Out, from.Id, amount, input.TransferDate, baseCurrencyId, notes, userId);
        var inLeg = Manual(FinancialTransactionType.Transfer, MoneyDirection.In, to.Id, amount, input.TransferDate, baseCurrencyId, notes, userId);
        outLeg.TransferGroupId = inLeg.TransferGroupId = group;
        db.FinancialTransactions.AddRange(outLeg, inLeg);

        await db.SaveChangesAsync(ct);
    }

    public async Task<int?> ReconcileAsync(Guid accountId, IReadOnlyCollection<Guid> transactionIds, Guid userId, CancellationToken ct = default)
    {
        var transactions = await LoadAsync(accountId, transactionIds, ct);
        if (transactions is null) return null;

        var changed = 0;
        foreach (var t in transactions.Where(t => t.ReconciledAt is null))
        {
            // A cancelled transaction no longer counts toward the balance, so there is nothing to match.
            if (t.Status == FinancialTransactionStatus.Cancelled)
            {
                throw new DomainValidationException("TransactionIds", "A cancelled transaction cannot be reconciled.");
            }
            t.ReconciledAt = DateTimeOffset.UtcNow;
            t.ReconciledByUserId = userId;
            changed++;
        }

        await db.SaveChangesAsync(ct);
        return changed;
    }

    public async Task<int?> UnreconcileAsync(Guid accountId, IReadOnlyCollection<Guid> transactionIds, CancellationToken ct = default)
    {
        var transactions = await LoadAsync(accountId, transactionIds, ct);
        if (transactions is null) return null;

        var changed = 0;
        foreach (var t in transactions.Where(t => t.ReconciledAt is not null))
        {
            t.ReconciledAt = null;
            t.ReconciledByUserId = null;
            changed++;
        }

        await db.SaveChangesAsync(ct);
        return changed;
    }

    private async Task<List<FinancialTransaction>?> LoadAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (!await db.FinancialAccounts.AnyAsync(a => a.Id == accountId, ct)) return null;

        var distinct = ids.Distinct().ToList();
        var transactions = await db.FinancialTransactions.Where(t => t.AccountId == accountId && distinct.Contains(t.Id)).ToListAsync(ct);
        if (transactions.Count != distinct.Count)
        {
            throw new DomainValidationException("TransactionIds", "Some transactions do not belong to this account.");
        }
        return transactions;
    }

    private async Task<(Guid Id, int Precision)> BaseCurrencyAsync(CancellationToken ct)
    {
        var settings = await db.GetCurrentCompanySettingsAsync(ct);
        return (settings.BaseCurrencyId, settings.BaseCurrencyDecimalPrecision);
    }

    private static void EnsureNotFuture(DateOnly date, string field)
    {
        if (date > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
        {
            throw new DomainValidationException(field, "Date cannot be in the future.");
        }
    }

    private static FinancialTransaction Manual(
        FinancialTransactionType type, MoneyDirection direction, Guid accountId, decimal amount, DateOnly date,
        Guid baseCurrencyId, string? notes, Guid userId) => new()
    {
        Id = Guid.NewGuid(), Type = type, Direction = direction, TransactionDate = date, AccountId = accountId,
        CurrencyId = baseCurrencyId, Amount = amount, ExchangeRateToBase = 1m, BaseAmount = amount,
        PaymentMethod = PaymentMethod.Cash, Notes = notes, CreatedByUserId = userId,
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
