using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

/// <param name="Balance">Opening balance plus active money in, less money out, in the base currency.</param>
/// <param name="ReconciledBalance">The same, counting only transactions already matched to the statement.</param>
public record FinancialAccountDto(
    Guid Id, string Name, FinancialAccountKind Kind, string? Reference, bool IsActive,
    decimal Balance, decimal ReconciledBalance, int UnreconciledCount,
    Guid? BranchId, string? BranchCode, string? BranchName
);

public record FinancialAccountListDto(
    IReadOnlyList<FinancialAccountDto> Items, decimal CashBalance, decimal BankBalance,
    string BaseCurrencyCode, string BaseCurrencySymbol, int BaseCurrencyDecimalPrecision
);

/// <summary>What a payment form needs to offer an account: no balances.</summary>
/// <param name="BranchId">Null = company-level account (usable for any branch); otherwise only for documents of that branch.</param>
public record AccountOptionDto(Guid Id, string Name, FinancialAccountKind Kind, Guid? BranchId = null);

public record CreateFinancialAccountRequest(
    [Required, MaxLength(60)] string Name,
    FinancialAccountKind Kind,
    [MaxLength(60)] string? Reference,
    [Range(0, 999999999999)] decimal OpeningBalance,
    DateOnly? OpeningDate,
    Guid? BranchId = null
);

public record UpdateFinancialAccountRequest(
    [Required, MaxLength(60)] string Name,
    [MaxLength(60)] string? Reference,
    Guid? BranchId = null
);

public record TransferRequest(
    [Required] Guid? FromAccountId,
    [Required] Guid? ToAccountId,
    [Range(0.000001, 999999999999)] decimal Amount,
    [Required] DateOnly TransferDate,
    [MaxLength(500)] string? Notes
);

public record ReconcileRequest([Required, MinLength(1), MaxLength(1000)] Guid[] TransactionIds);
