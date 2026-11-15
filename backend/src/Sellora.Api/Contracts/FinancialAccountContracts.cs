using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

/// <param name="Balance">Opening balance plus active money in, less money out, in the base currency.</param>
/// <param name="ReconciledBalance">The same, counting only transactions already matched to the statement.</param>
public record FinancialAccountDto(
    Guid Id, string Name, FinancialAccountKind Kind, string? Reference, bool IsActive,
    // Phase 01 (FI-D12): null for a company-level account (BranchId null) when the reader does not hold CashBank.View company-level.
    decimal? Balance, decimal? ReconciledBalance, int UnreconciledCount,
    Guid? BranchId, string? BranchCode, string? BranchName,
    // Phase 07 (FI-D20): how far below zero the account may go through transfers and reversals; 0 for cash.
    decimal OverdraftLimit = 0m
);

public record FinancialAccountListDto(
    IReadOnlyList<FinancialAccountDto> Items, decimal CashBalance, decimal BankBalance, // totals cover only the accounts whose balance is visible to the reader
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
    Guid? BranchId = null,
    [Range(0, 999999999999)] decimal OverdraftLimit = 0m
);

public record UpdateFinancialAccountRequest(
    [Required, MaxLength(60)] string Name,
    [MaxLength(60)] string? Reference,
    Guid? BranchId = null,
    [Range(0, 999999999999)] decimal? OverdraftLimit = null
);

public record TransferRequest(
    [Required] Guid? FromAccountId,
    [Required] Guid? ToAccountId,
    [Range(0.000001, 999999999999)] decimal Amount,
    [Required] DateOnly TransferDate,
    [MaxLength(500)] string? Notes
);

public record FundingRequest(
    [Required] FundingSource? Source,
    [Range(0.000001, 999999999999)] decimal Amount,
    [Required] DateOnly? FundingDate,
    [MaxLength(100)] string? SourceReference,
    [MaxLength(120)] string? Counterparty,
    Guid? CustomerId,
    [MaxLength(300)] string? Reason,
    [MaxLength(500)] string? Notes
);

public record FundingResultDto(Guid TransactionId);

public record ReverseTransactionRequest([Required, MaxLength(300)] string Reason);

public record ReversalResultDto(IReadOnlyList<Guid> ReversalTransactionIds);

/// <param name="MoneyIn">Zero when the row is a debit.</param>
/// <param name="ReversedByTransactionId">The reversal entry that cancelled this one, when any.</param>
public record AccountStatementRowDto(
    Guid TransactionId, DateOnly Date, FinancialTransactionType Type, string? FundingSource, string Description, string? Reference,
    decimal MoneyIn, decimal MoneyOut, decimal RunningBalance, bool Reconciled, bool IsReversal, Guid? ReversedByTransactionId
);

/// <summary>Opening = balance before <see cref="From"/>; Closing = Opening + TotalIn - TotalOut; rows are active entries dated From..To
/// in date then creation order, each with the balance after it.</summary>
public record AccountStatementDto(
    Guid AccountId, DateOnly From, DateOnly To, decimal OpeningBalance, decimal ClosingBalance, decimal TotalIn, decimal TotalOut,
    IReadOnlyList<AccountStatementRowDto> Rows
);

public record ReconcileRequest([Required, MinLength(1), MaxLength(1000)] Guid[] TransactionIds);
