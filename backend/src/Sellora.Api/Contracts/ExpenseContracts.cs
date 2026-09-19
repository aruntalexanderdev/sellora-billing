using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

public record ExpenseCategoryDto(Guid Id, string Name, bool IsActive);

public record UpsertExpenseCategoryRequest([Required, MaxLength(60)] string Name);

public record ExpenseListItemDto(
    Guid Id, string ExpenseNo, DateOnly ExpenseDate, string CategoryName, string? Reference,
    PaymentMethod PaymentMethod, decimal Amount, string CurrencyCode, decimal BaseAmount, ExpenseStatus Status
);

public record ExpenseDetailDto(
    Guid Id, string ExpenseNo, DateOnly ExpenseDate, Guid CategoryId, string CategoryName,
    Guid CurrencyId, string CurrencyCode, string CurrencySymbol, int CurrencyDecimalPrecision,
    decimal Amount, decimal ExchangeRateToBase, decimal BaseAmount,
    string BaseCurrencyCode, string BaseCurrencySymbol, int BaseCurrencyDecimalPrecision,
    PaymentMethod PaymentMethod, string? Reference, string? Notes, ExpenseStatus Status,
    string CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
    DateTimeOffset? CancelledAt, string? CancelledByName, string? CancellationReason
);

public record CreateExpenseRequest(
    [Required] Guid? CategoryId,
    [Required] DateOnly ExpenseDate,
    [Required] Guid? CurrencyId,
    [Range(0.000001, 999999999)] decimal Amount,
    PaymentMethod PaymentMethod,
    [MaxLength(60)] string? Reference,
    [MaxLength(500)] string? Notes
);

public record UpdateExpenseRequest(
    [Required] Guid? CategoryId,
    [MaxLength(60)] string? Reference,
    [MaxLength(500)] string? Notes
);

public record CancelExpenseRequest([MaxLength(200)] string? Reason);
