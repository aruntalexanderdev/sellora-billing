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
)
{
    /// <summary>Optional explicit branch tag (null = company-level). Not stamped from the X-Branch-Id header.</summary>
    public Guid? BranchId { get; init; }
}

/// <summary>The only fields that can change once an expense is saved (category, reference, notes, branch tag).</summary>
public sealed record UpdateExpenseInput(Guid CategoryId, string? Reference, string? Notes)
{
    /// <summary>Optional branch tag; null = company-level (PUT replaces the tag).</summary>
    public Guid? BranchId { get; init; }
}
