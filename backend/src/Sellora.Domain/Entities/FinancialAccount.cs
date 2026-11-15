using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

public enum FinancialAccountKind
{
    Cash,
    Bank,
}

/// <summary>
/// A place the business's money actually sits: a cash drawer or a bank account. Accounts are kept in the
/// base currency, so a foreign-currency payment moves the account by its base-currency amount. An account has
/// no stored balance: it is the sum of its active financial transactions (an opening balance is just the
/// first one), so it can never drift from the ledger. Accounts are deactivated, never deleted.
/// </summary>
public class FinancialAccount : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    /// <summary>Optional branch tag; null = company-level (Phase 2).</summary>
    public Guid? BranchId { get; set; }

    public Guid Id { get; set; }
    public required string Name { get; set; }
    public FinancialAccountKind Kind { get; set; }

    /// <summary>Bank name, branch or the last digits of the account number — free text for recognising the account.</summary>
    public string? Reference { get; set; }

    /// <summary>How far below zero a bank account may go through transfers and reversals. Always 0 for cash.</summary>
    public decimal OverdraftLimit { get; set; }

    public bool IsActive { get; set; } = true;

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
