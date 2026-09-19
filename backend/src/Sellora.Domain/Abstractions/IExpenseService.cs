using Sellora.Domain.Entities;
using Sellora.Domain.Expenses;

namespace Sellora.Domain.Abstractions;

public interface IExpenseService
{
    /// <summary>Records an expense as paid, rounding the amount to its currency's precision and snapshotting the
    /// exchange rate for the expense date. Throws <see cref="DomainValidationException"/> for an unknown or
    /// inactive category or currency, a non-positive amount, a future date, or a missing exchange rate.</summary>
    Task<Expense> CreateAsync(CreateExpenseInput input, Guid createdByUserId, CancellationToken ct = default);

    /// <summary>Changes an active expense's category, reference and notes. Returns null if it does not exist;
    /// throws <see cref="DomainValidationException"/> if it is cancelled or the category is invalid.</summary>
    Task<Expense?> UpdateAsync(Guid expenseId, UpdateExpenseInput input, CancellationToken ct = default);

    /// <summary>Cancels an active expense, keeping the record. Returns null if it does not exist; throws
    /// <see cref="DomainValidationException"/> if it is already cancelled.</summary>
    Task<Expense?> CancelAsync(Guid expenseId, string? reason, Guid cancelledByUserId, CancellationToken ct = default);
}
