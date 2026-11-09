using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Expenses;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

public class ExpenseService(SelloraDbContext db, IExchangeRateService exchangeRateService) : IExpenseService
{
    public async Task<Expense> CreateAsync(CreateExpenseInput input, Guid createdByUserId, CancellationToken ct = default)
    {
        var category = await db.ExpenseCategories.SingleOrDefaultAsync(c => c.Id == input.CategoryId, ct)
            ?? throw new DomainValidationException("CategoryId", "Select a valid category.");
        if (!category.IsActive)
        {
            throw new DomainValidationException("CategoryId", "This category is inactive.");
        }

        var currency = await db.Currencies.SingleOrDefaultAsync(c => c.Id == input.CurrencyId, ct)
            ?? throw new DomainValidationException("CurrencyId", "Select a valid currency.");
        if (!currency.IsActive)
        {
            throw new DomainValidationException("CurrencyId", $"{currency.Code} is inactive.");
        }

        var amount = Math.Round(input.Amount, currency.DecimalPrecision, MidpointRounding.AwayFromZero);
        if (amount <= 0)
        {
            throw new DomainValidationException("Amount", "Amount must be greater than zero.");
        }
        if (input.ExpenseDate > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
        {
            throw new DomainValidationException("ExpenseDate", "Expense date cannot be in the future.");
        }

        // Optional explicit branch tag (never stamped from the header: company-level stays the default).
        var branch = input.BranchId is { } branchId
            ? await BranchRules.EnsureCanUseAsync(db, branchId, requireActive: true, "BranchId", ct)
            : null;
        await FinancialAccountRules.EnsureUsableAsync(db, input.AccountId, branch?.Id, ct);

        var rate = await exchangeRateService.ResolveRateToBaseAsync(currency.Id, input.ExpenseDate, ct);
        var basePrecision = (await db.GetCurrentCompanySettingsAsync(ct)).BaseCurrencyDecimalPrecision;
        var baseAmount = rate == 1m ? amount : Math.Round(amount * rate, basePrecision, MidpointRounding.AwayFromZero);

        // The counter row lock (DocumentNumbers) serialises concurrent creates like every other document series.
        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var expenseNo = await DocumentNumbers.NextNumberAsync(db, DocumentSeries.Expense, null, ct);

        var expense = new Expense
        {
            Id = Guid.NewGuid(),
            ExpenseNo = expenseNo,
            BranchId = branch?.Id,
            CategoryId = category.Id,
            ExpenseDate = input.ExpenseDate,
            CurrencyId = currency.Id,
            Amount = amount,
            ExchangeRateToBase = rate,
            BaseAmount = baseAmount,
            PaymentMethod = input.PaymentMethod,
            Reference = Clean(input.Reference),
            Notes = Clean(input.Notes),
            CreatedByUserId = createdByUserId,
        };

        db.Expenses.Add(expense);
        db.FinancialTransactions.Add(FinancialTransaction.ForExpense(expense, input.AccountId));
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return expense;
    }

    public async Task<Expense?> UpdateAsync(Guid expenseId, UpdateExpenseInput input, CancellationToken ct = default)
    {
        var expense = await db.Expenses.SingleOrDefaultAsync(e => e.Id == expenseId, ct);
        if (expense is null) return null;

        if (expense.Status == ExpenseStatus.Cancelled)
        {
            throw new DomainValidationException("Status", "This expense is cancelled and cannot be edited.");
        }

        // A category the expense already uses stays valid even if it was deactivated since.
        var category = await db.ExpenseCategories.SingleOrDefaultAsync(c => c.Id == input.CategoryId, ct)
            ?? throw new DomainValidationException("CategoryId", "Select a valid category.");
        if (!category.IsActive && category.Id != expense.CategoryId)
        {
            throw new DomainValidationException("CategoryId", "This category is inactive.");
        }

        if (input.BranchId != expense.BranchId)
        {
            if (input.BranchId is { } newBranchId)
            {
                var branch = await BranchRules.EnsureCanUseAsync(db, newBranchId, requireActive: true, "BranchId", ct);
                var accountBranchId = await db.FinancialTransactions
                    .Where(t => t.ExpenseId == expense.Id && t.AccountId != null)
                    .Join(db.FinancialAccounts, t => t.AccountId!.Value, a => a.Id, (t, a) => a.BranchId)
                    .FirstOrDefaultAsync(ct);
                if (accountBranchId is { } tagged && tagged != branch.Id)
                {
                    throw new DomainValidationException("BranchId", "This expense was paid from an account that belongs to another branch.");
                }
            }
            expense.BranchId = input.BranchId;
        }

        expense.CategoryId = category.Id;
        expense.Reference = Clean(input.Reference);
        expense.Notes = Clean(input.Notes);
        expense.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return expense;
    }

    public async Task<Expense?> CancelAsync(Guid expenseId, string? reason, Guid cancelledByUserId, CancellationToken ct = default)
    {
        var expense = await db.Expenses.SingleOrDefaultAsync(e => e.Id == expenseId, ct);
        if (expense is null) return null;

        if (expense.Status == ExpenseStatus.Cancelled)
        {
            throw new DomainValidationException("Status", "This expense has already been cancelled.");
        }

        expense.Status = ExpenseStatus.Cancelled;
        expense.CancelledAt = DateTimeOffset.UtcNow;
        expense.CancelledByUserId = cancelledByUserId;
        expense.CancellationReason = Clean(reason);

        // The money-out record stays for history but no longer counts toward totals.
        var transaction = await db.FinancialTransactions.SingleOrDefaultAsync(t => t.ExpenseId == expense.Id, ct);
        if (transaction is not null) transaction.Status = FinancialTransactionStatus.Cancelled;

        await db.SaveChangesAsync(ct);
        return expense;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
