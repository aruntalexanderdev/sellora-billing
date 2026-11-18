using Sellora.Domain.Entities;

namespace Sellora.Domain.Accounts;

/// <summary>An opening balance of zero writes no transaction; anything above becomes the account's first.</summary>
public sealed record CreateAccountInput(
    string Name,
    FinancialAccountKind Kind,
    string? Reference,
    decimal OpeningBalance,
    DateOnly OpeningDate
)
{
    /// <summary>Optional branch tag (null = company-level account usable everywhere).</summary>
    public Guid? BranchId { get; init; }

    /// <summary>How far below zero a bank account may go (FI-D20); must be 0 for cash.</summary>
    public decimal OverdraftLimit { get; init; }
}

public sealed record UpdateAccountInput(string Name, string? Reference)
{
    /// <summary>Null keeps the current limit.</summary>
    public decimal? OverdraftLimit { get; init; }

    /// <summary>Optional branch tag; null = company-level (PUT replaces the tag).</summary>
    public Guid? BranchId { get; init; }
}

public sealed record TransferInput(
    Guid FromAccountId,
    Guid ToAccountId,
    decimal Amount,
    DateOnly TransferDate,
    string? Notes
)
{
    /// <summary>Client-generated Idempotency-Key; a replay with the same key posts nothing more.</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>Money put into one account from outside the business's trading (FI-D19).</summary>
public sealed record FundingInput(
    Guid AccountId,
    FundingSource Source,
    decimal Amount,
    DateOnly FundingDate,
    string? SourceReference,
    string? Counterparty,
    Guid? CustomerId,
    string? Reason,
    string? Notes
)
{
    public string? IdempotencyKey { get; init; }
}

/// <summary>Reverses a funding, a transfer (both legs) or an opening balance (FI-D21).</summary>
public sealed record ReverseTransactionInput(Guid TransactionId, string Reason)
{
    public string? IdempotencyKey { get; init; }
}
