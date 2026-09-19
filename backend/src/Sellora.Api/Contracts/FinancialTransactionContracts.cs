using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

/// <param name="Party">The supplier or customer involved, the category for an expense, the account for an opening
/// balance, or the other account of a transfer.</param>
/// <param name="RelatedType">"Purchase", "Sale" or "Expense" — the source document; null for opening balances and transfers.</param>
public record FinancialTransactionDto(
    Guid Id, DateOnly TransactionDate, FinancialTransactionType Type, MoneyDirection Direction, FinancialTransactionStatus Status,
    string Party, string? RelatedType, Guid? RelatedId, string? RelatedNo, PaymentMethod PaymentMethod,
    decimal Amount, string CurrencyCode, decimal ExchangeRateToBase, decimal BaseAmount, DateTimeOffset CreatedAt,
    Guid? AccountId, string? AccountName, bool IsReconciled, string? Notes
);

/// <summary><see cref="MoneyIn"/> and <see cref="MoneyOut"/> total the active transactions matching the filters,
/// in the base currency. Opening balances and transfers between accounts are not money entering or leaving the
/// business, so they are left out of both.</summary>
public record FinancialTransactionListDto(
    IReadOnlyList<FinancialTransactionDto> Items, int Total, int Page, int PageSize,
    decimal MoneyIn, decimal MoneyOut, string BaseCurrencyCode, string BaseCurrencySymbol
);
