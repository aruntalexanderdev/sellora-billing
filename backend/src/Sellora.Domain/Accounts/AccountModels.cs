using Sellora.Domain.Entities;

namespace Sellora.Domain.Accounts;

/// <summary>An opening balance of zero writes no transaction; anything above becomes the account's first.</summary>
public sealed record CreateAccountInput(
    string Name,
    FinancialAccountKind Kind,
    string? Reference,
    decimal OpeningBalance,
    DateOnly OpeningDate
);

public sealed record UpdateAccountInput(string Name, string? Reference);

public sealed record TransferInput(
    Guid FromAccountId,
    Guid ToAccountId,
    decimal Amount,
    DateOnly TransferDate,
    string? Notes
);
