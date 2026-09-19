using Sellora.Domain.Entities;

namespace Sellora.Domain.Expenses;

/// <summary><see cref="Amount"/> is in <see cref="CurrencyId"/>'s currency.</summary>
public sealed record CreateExpenseInput(
    Guid CategoryId,
    DateOnly ExpenseDate,
    Guid CurrencyId,
    decimal Amount,
    PaymentMethod PaymentMethod,
    string? Reference,
    string? Notes,
    Guid? AccountId = null
);

/// <summary>The only fields that can change once an expense is saved.</summary>
public sealed record UpdateExpenseInput(Guid CategoryId, string? Reference, string? Notes);
