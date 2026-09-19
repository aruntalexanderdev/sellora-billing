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

        var rate = await exchangeRateService.ResolveRateToBaseAsync(currency.Id, input.ExpenseDate, ct);
        var basePrecision = await db.BusinessSettings.Select(s => s.BaseCurrency.DecimalPrecision).SingleAsync(ct);
        var baseAmount = rate == 1m ? amount : Math.Round(amount * rate, basePrecision, MidpointRounding.AwayFromZero);

        var expense = new Expense
        {
            Id = Guid.NewGuid(),
            ExpenseNo = $"EXP-{await db.Expenses.CountAsync(ct) + 1:D4}",
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
        await db.SaveChangesAsync(ct);
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

        await db.SaveChangesAsync(ct);
        return expense;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
