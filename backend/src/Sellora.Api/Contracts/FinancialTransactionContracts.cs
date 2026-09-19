using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

/// <param name="Party">The supplier or customer involved, or the category for an expense.</param>
/// <param name="RelatedType">"Purchase", "Sale" or "Expense" — the source document this came from.</param>
public record FinancialTransactionDto(
    Guid Id, DateOnly TransactionDate, FinancialTransactionType Type, MoneyDirection Direction, FinancialTransactionStatus Status,
    string Party, string RelatedType, Guid RelatedId, string RelatedNo, PaymentMethod PaymentMethod,
    decimal Amount, string CurrencyCode, decimal ExchangeRateToBase, decimal BaseAmount, DateTimeOffset CreatedAt
);

/// <summary><see cref="MoneyIn"/> and <see cref="MoneyOut"/> total the active transactions matching the filters,
/// in the base currency.</summary>
public record FinancialTransactionListDto(
    IReadOnlyList<FinancialTransactionDto> Items, int Total, int Page, int PageSize,
    decimal MoneyIn, decimal MoneyOut, string BaseCurrencyCode, string BaseCurrencySymbol
);
