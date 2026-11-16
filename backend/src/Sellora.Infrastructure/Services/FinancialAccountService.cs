using Microsoft.EntityFrameworkCore;
using Npgsql;
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
        // Documented "Branch"-filter bypass (D7): the name unique index is company-wide, so accounts of other branches must count.
        if (await db.FinancialAccounts.IgnoreQueryFilters([BranchQueryFilter.Name]).AnyAsync(a => a.Name.ToLower() == name.ToLower(), ct))
        {
            throw new DomainValidationException("Name", "An account with this name already exists.");
        }
        if (input.OpeningBalance < 0)
        {
            throw new DomainValidationException("OpeningBalance", "Opening balance cannot be negative.");
        }
        EnsureNotFuture(input.OpeningDate, "OpeningDate");
        var overdraft = ValidOverdraft(input.OverdraftLimit, input.Kind);

        var branch = input.BranchId is { } branchId
            ? await BranchRules.EnsureCanUseAsync(db, branchId, requireActive: true, "BranchId", ct)
            : null;

        var (baseCurrencyId, basePrecision) = await BaseCurrencyAsync(ct);
        var opening = Math.Round(input.OpeningBalance, basePrecision, MidpointRounding.AwayFromZero);

        var account = new FinancialAccount
        {
            Id = Guid.NewGuid(), Name = name, Kind = input.Kind, Reference = Clean(input.Reference), BranchId = branch?.Id, OverdraftLimit = overdraft, CreatedByUserId = userId,
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
        // Locked like a posting, so the overdraft check below cannot race a concurrent transfer out of this account.
        await using var tx = await StockLocks.BeginAsync(db, ct);
        await StockLocks.LockFinancialAccountsAsync(db, [accountId], ct);
        var account = await db.FinancialAccounts.SingleOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null) return null;

        var name = input.Name.Trim();
        if (name.Length == 0)
        {
            throw new DomainValidationException("Name", "Account name is required.");
        }
        // Documented "Branch"-filter bypass (D7): the name unique index is company-wide, so accounts of other branches must count.
        if (await db.FinancialAccounts.IgnoreQueryFilters([BranchQueryFilter.Name]).AnyAsync(a => a.Id != accountId && a.Name.ToLower() == name.ToLower(), ct))
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

        if (input.OverdraftLimit is { } limit)
        {
            var newLimit = ValidOverdraft(limit, account.Kind);
            // FI-D20 (review): a lower limit may not strand an account that is already overdrawn beyond it.
            if (newLimit < account.OverdraftLimit && await BalanceAsync(accountId, ct) < -newLimit)
            {
                throw new DomainValidationException("overdraftLimit",
                    "The account is already overdrawn beyond this limit. Fund it first or choose a higher limit.");
            }
            account.OverdraftLimit = newLimit;
        }

        account.Name = name;
        account.Reference = Clean(input.Reference);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return account;
    }

    public Task TransferAsync(TransferInput input, Guid userId, CancellationToken ct = default) =>
        TransferAsync(input, userId, null, ct);

    public async Task TransferAsync(TransferInput input, Guid userId, string? correlationId, CancellationToken ct = default)
    {
        if (input.FromAccountId == input.ToAccountId)
        {
            throw new DomainValidationException("ToAccountId", "Choose a different account to transfer to.");
        }

        // FI-D20: one transaction; both accounts are locked in id order before the source balance is read.
        await using var tx = await StockLocks.BeginAsync(db, ct);
        if (await Idempotency.FindAsync(db, userId, Idempotency.CashTransfer, input.IdempotencyKey, ct) is not null)
        {
            return;
        }
        await StockLocks.LockFinancialAccountsAsync(db, [input.FromAccountId, input.ToAccountId], ct);

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
        EnsureUsableAsSource(from, "fromAccountId");

        var fromBefore = await BalanceAsync(from.Id, ct);
        var toBefore = await BalanceAsync(to.Id, ct);
        EnsureWithinOverdraft(from, fromBefore, amount, "amount");

        // Both legs share a group id and are saved by the one SaveChanges, so a transfer is never half-written.
        var group = Guid.NewGuid();
        var notes = Clean(input.Notes);
        var outLeg = Manual(FinancialTransactionType.Transfer, MoneyDirection.Out, from.Id, amount, input.TransferDate, baseCurrencyId, notes, userId);
        var inLeg = Manual(FinancialTransactionType.Transfer, MoneyDirection.In, to.Id, amount, input.TransferDate, baseCurrencyId, notes, userId);
        outLeg.TransferGroupId = inLeg.TransferGroupId = group;
        db.FinancialTransactions.AddRange(outLeg, inLeg);

        AuditLog.Append(db, userId, correlationId, AuditActions.CashTransferred, AuditTargets.FinancialTransaction, group, null, null, new
        {
            amount, date = input.TransferDate,
            from = new { id = from.Id, name = from.Name, balanceBefore = fromBefore, balanceAfter = fromBefore - amount },
            to = new { id = to.Id, name = to.Name, balanceBefore = toBefore, balanceAfter = toBefore + amount },
            transactionIds = new[] { outLeg.Id, inLeg.Id },
        });
        Idempotency.Record(db, userId, Idempotency.CashTransfer, input.IdempotencyKey, group);

        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }

    public async Task<Guid> FundAsync(FundingInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(input.Source)) throw new DomainValidationException("source", "Select where the money came from.");

        var reference = Clean(input.SourceReference);
        var counterparty = Clean(input.Counterparty);
        var reason = Clean(input.Reason);
        var notes = Clean(input.Notes);
        if (reference is { Length: > 100 }) throw new DomainValidationException("sourceReference", "The reference must be at most 100 characters.");
        if (counterparty is { Length: > 120 }) throw new DomainValidationException("counterparty", "The counterparty must be at most 120 characters.");
        if (reason is { Length: > 300 }) throw new DomainValidationException("reason", "The reason must be at most 300 characters.");
        if (input.Source == FundingSource.CustomerReceipt)
        {
            if (input.CustomerId is null) throw new DomainValidationException("customerId", "Select the customer the money came from.");
            if (reference is null) throw new DomainValidationException("sourceReference", "A receipt or reference number is required for a customer receipt.");
        }
        else if (input.CustomerId is not null)
        {
            throw new DomainValidationException("customerId", "A customer can only be named on a customer receipt.");
        }
        if (input.Source == FundingSource.Other && reason is null)
        {
            throw new DomainValidationException("reason", "A reason is required for other funding.");
        }

        await using var tx = await StockLocks.BeginAsync(db, ct);
        if (await Idempotency.FindAsync(db, userId, Idempotency.CashFunding, input.IdempotencyKey, ct) is { } existing)
        {
            return existing;
        }
        await StockLocks.LockFinancialAccountsAsync(db, [input.AccountId], ct);

        var account = await db.FinancialAccounts.SingleOrDefaultAsync(a => a.Id == input.AccountId, ct)
            ?? throw new DomainValidationException("accountId", "Select a valid account to fund.");
        if (!account.IsActive) throw new DomainValidationException("accountId", $"{account.Name} is inactive.");
        EnsureUsableAsSource(account, "accountId");

        var (baseCurrencyId, basePrecision) = await BaseCurrencyAsync(ct);
        var amount = Math.Round(input.Amount, basePrecision, MidpointRounding.AwayFromZero);
        if (amount <= 0) throw new DomainValidationException("amount", "Amount must be greater than zero.");
        EnsureNotFuture(input.FundingDate, "fundingDate");

        if (input.CustomerId is { } customerId
            && !await db.Customers.AnyAsync(c => c.Id == customerId && c.IsActive, ct))
        {
            throw new DomainValidationException("customerId", "Select a valid, active customer.");
        }

        var before = await BalanceAsync(account.Id, ct);
        var funding = Manual(FinancialTransactionType.Funding, MoneyDirection.In, account.Id, amount, input.FundingDate, baseCurrencyId, notes, userId);
        funding.FundingSource = input.Source;
        funding.SourceReference = reference;
        funding.Counterparty = counterparty;
        funding.CustomerId = input.CustomerId;
        funding.Reason = reason;
        db.FinancialTransactions.Add(funding);

        AuditLog.Append(db, userId, correlationId, AuditActions.CashFunded, AuditTargets.FinancialTransaction, funding.Id, reason, null, new
        {
            source = input.Source.ToString(), amount, date = input.FundingDate, sourceReference = reference, counterparty, customerId = input.CustomerId,
            account = new { id = account.Id, name = account.Name, balanceBefore = before, balanceAfter = before + amount },
        });
        Idempotency.Record(db, userId, Idempotency.CashFunding, input.IdempotencyKey, funding.Id);

        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return funding.Id;
    }

    public async Task<IReadOnlyList<Guid>?> ReverseAsync(ReverseTransactionInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var reason = Clean(input.Reason);
        if (reason is null) throw new DomainValidationException("reason", "A reason is required.");
        if (reason.Length > 300) throw new DomainValidationException("reason", "The reason must be at most 300 characters.");

        await using var tx = await StockLocks.BeginAsync(db, ct);
        if (await Idempotency.FindAsync(db, userId, Idempotency.CashReversal, input.IdempotencyKey, ct) is { } resource)
        {
            return await db.FinancialTransactions.AsNoTracking()
                .Where(t => t.Type == FinancialTransactionType.Reversal && (t.Id == resource || t.TransferGroupId == resource))
                .Select(t => t.Id).OrderBy(id => id).ToListAsync(ct);
        }

        // The original is found through the company filter, so another company's id is simply "not found" (the self-FK itself is not company-scoped).
        var companyId = db.CompanyContext.RequireCompanyId();
        var probe = await db.FinancialTransactions.AsNoTracking()
            .Where(t => t.Id == input.TransactionId && t.CompanyId == companyId).Select(t => new { t.Type, t.TransferGroupId, t.AccountId }).SingleOrDefaultAsync(ct);
        if (probe is null || probe.AccountId is null) return null;

        var groupMode = probe.Type == FinancialTransactionType.Transfer && probe.TransferGroupId is not null;
        var legIds = groupMode
            ? await db.FinancialTransactions.AsNoTracking().Where(t => t.CompanyId == companyId && t.TransferGroupId == probe.TransferGroupId && t.Type == FinancialTransactionType.Transfer)
                .Select(t => t.Id).ToListAsync(ct)
            : [input.TransactionId];

        // FI-D20: lock every involved account in id order, then re-read the entries under the locks so a concurrent reversal or posting is seen.
        var accountIds = await db.FinancialTransactions.AsNoTracking().Where(t => legIds.Contains(t.Id)).Select(t => t.AccountId!.Value).ToListAsync(ct);
        await StockLocks.LockFinancialAccountsAsync(db, accountIds, ct);

        var originals = await db.FinancialTransactions.Where(t => t.CompanyId == companyId && legIds.Contains(t.Id)).ToListAsync(ct);
        var accounts = await db.FinancialAccounts.Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        if (originals.Count != legIds.Count || accountIds.Any(id => !accounts.ContainsKey(id))) return null; // not visible to the caller's branch scope

        var type = originals[0].Type;
        if (type is not (FinancialTransactionType.Funding or FinancialTransactionType.Transfer or FinancialTransactionType.OpeningBalance))
        {
            throw new DomainValidationException("id", "Only funding, transfers and opening balances can be reversed here; use the source document for payments.");
        }
        if (type == FinancialTransactionType.Transfer && originals.Count != 2)
        {
            throw new DomainValidationException("id", "This transfer is incomplete and cannot be reversed.");
        }
        if (originals.Any(o => o.Status != FinancialTransactionStatus.Active))
        {
            throw new DomainValidationException("id", "Only an active transaction can be reversed.");
        }
        if (originals.Any(o => o.ReconciledAt is not null))
        {
            throw new DomainValidationException("id", "A reconciled transaction cannot be reversed; unreconcile it first.");
        }
        if (await db.FinancialTransactions.AnyAsync(t => t.ReversalOfTransactionId != null && legIds.Contains(t.ReversalOfTransactionId.Value), ct))
        {
            throw new DomainValidationException("id", "This transaction has already been reversed.");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var group = groupMode ? Guid.NewGuid() : (Guid?)null;
        var reversals = new List<FinancialTransaction>();
        var audit = new List<object>();
        foreach (var original in originals.OrderBy(o => o.Direction == MoneyDirection.In ? 1 : 0).ThenBy(o => o.Id))
        {
            var account = accounts[original.AccountId!.Value];
            if (!account.IsActive) throw new DomainValidationException("id", $"{account.Name} is inactive.");

            var direction = original.Direction == MoneyDirection.In ? MoneyDirection.Out : MoneyDirection.In;
            var before = await BalanceAsync(account.Id, ct);
            if (direction == MoneyDirection.Out)
            {
                EnsureUsableAsSource(account, "id");
                EnsureWithinOverdraft(account, before, original.BaseAmount, "amount");
            }

            reversals.Add(new FinancialTransaction
            {
                Id = Guid.NewGuid(), Type = FinancialTransactionType.Reversal, Direction = direction, TransactionDate = today, AccountId = account.Id,
                CurrencyId = original.CurrencyId, Amount = original.Amount, ExchangeRateToBase = original.ExchangeRateToBase, BaseAmount = original.BaseAmount,
                PaymentMethod = original.PaymentMethod, ReversalOfTransactionId = original.Id, Reason = reason, TransferGroupId = group, CreatedByUserId = userId,
            });
            var delta = direction == MoneyDirection.In ? original.BaseAmount : -original.BaseAmount;
            audit.Add(new { reversalOf = original.Id, accountId = account.Id, account = account.Name, direction = direction.ToString(), amount = original.BaseAmount, balanceBefore = before, balanceAfter = before + delta });
        }
        db.FinancialTransactions.AddRange(reversals);

        AuditLog.Append(db, userId, correlationId, AuditActions.CashReversed, AuditTargets.FinancialTransaction, input.TransactionId, reason,
            new { type = type.ToString(), transactionIds = legIds }, new { postings = audit, reversalTransactionIds = reversals.Select(r => r.Id) });
        Idempotency.Record(db, userId, Idempotency.CashReversal, input.IdempotencyKey, group ?? reversals[0].Id);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_FinancialTransactions_ReversalOfTransactionId" })
        {
            throw new DomainValidationException("id", "This transaction has already been reversed.");
        }
        if (tx is not null) await tx.CommitAsync(ct);
        return reversals.Select(r => r.Id).ToList();
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

    // Balances are derived from the ledger, never stored: active money in less money out, in the base currency.
    private async Task<decimal> BalanceAsync(Guid accountId, CancellationToken ct) =>
        await db.FinancialTransactions.Where(t => t.AccountId == accountId && t.Status == FinancialTransactionStatus.Active)
            .SumAsync(t => (decimal?)(t.Direction == MoneyDirection.In ? t.BaseAmount : -t.BaseAmount), ct) ?? 0m;

    private static decimal ValidOverdraft(decimal limit, FinancialAccountKind kind)
    {
        if (limit < 0) throw new DomainValidationException("overdraftLimit", "The overdraft limit cannot be negative.");
        var rounded = Math.Round(limit, 2, MidpointRounding.AwayFromZero);
        if (kind == FinancialAccountKind.Cash && rounded != 0)
        {
            throw new DomainValidationException("overdraftLimit", "A cash account cannot have an overdraft limit.");
        }
        return rounded;
    }

    // FI-D20: an account may not go below -OverdraftLimit through a transfer or reversal (cash: below zero).
    private static void EnsureWithinOverdraft(FinancialAccount account, decimal balance, decimal debit, string field)
    {
        var limit = account.Kind == FinancialAccountKind.Cash ? 0m : account.OverdraftLimit;
        if (balance - debit < -limit)
        {
            throw new DomainValidationException(field, limit == 0m
                ? $"This would take {account.Name} below zero (balance {StockQuantity.Format(balance)})."
                : $"This would take {account.Name} below its overdraft limit of {StockQuantity.Format(limit)} (balance {StockQuantity.Format(balance)}).");
        }
    }

    // FI-D22: money may only be taken from / put into the active branch's accounts, or company-level accounts by a member covering every branch.
    // The destination of a transfer is not subject to this. A context with no active branch (tests, seeder) falls back to the access scope alone.
    private void EnsureUsableAsSource(FinancialAccount account, string field)
    {
        var context = db.BranchContext;
        var allowed = account.BranchId is { } branchId
            ? context.CanAccess(branchId) && (context.ActiveBranchId is not { } active || active == branchId)
            : context.AllowedBranchIds is null;
        if (!allowed)
        {
            throw new DomainValidationException(field, account.BranchId is null
                ? $"{account.Name} is a company-level account; only members covering every branch can use it."
                : $"{account.Name} belongs to another branch; switch to that branch to use it.");
        }
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
