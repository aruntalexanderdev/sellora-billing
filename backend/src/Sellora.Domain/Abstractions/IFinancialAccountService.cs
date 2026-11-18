using Sellora.Domain.Accounts;
using Sellora.Domain.Entities;

namespace Sellora.Domain.Abstractions;

public interface IFinancialAccountService
{
    /// <summary>Creates an account; a positive opening balance is recorded as its first transaction. Throws
    /// <see cref="DomainValidationException"/> for a duplicate name, a negative opening balance or a future date.</summary>
    Task<FinancialAccount> CreateAsync(CreateAccountInput input, Guid userId, CancellationToken ct = default);

    /// <summary>Renames an account and edits its reference. Returns null if it does not exist. The kind cannot change.</summary>
    Task<FinancialAccount?> UpdateAsync(Guid accountId, UpdateAccountInput input, CancellationToken ct = default);

    /// <summary>Moves money between two active accounts as two linked transactions (out of one, into the other)
    /// written together. Throws <see cref="DomainValidationException"/> if the accounts are the same, missing or
    /// inactive, the amount is not positive, or the date is in the future.</summary>
    Task TransferAsync(TransferInput input, Guid userId, CancellationToken ct = default);

    /// <summary>Phase 07: the same, with the correlation id stamped on the <c>cash.transferred</c> audit event. Also throws when the source
    /// account is not usable as a source (FI-D22, <c>fromAccountId</c>) or the transfer would take it below its overdraft limit (<c>amount</c>).</summary>
    Task TransferAsync(TransferInput input, Guid userId, string? correlationId, CancellationToken ct = default);

    /// <summary>Records funding into an account (FI-D19) in one transaction and returns the new transaction id. Throws
    /// <see cref="DomainValidationException"/> with the request field name for any rule that fails.</summary>
    Task<Guid> FundAsync(FundingInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Posts the opposite entries of a Funding, Transfer (both legs, as a new group) or OpeningBalance transaction (FI-D21) and returns
    /// their ids. Returns null when the transaction does not exist for the caller. Throws <see cref="DomainValidationException"/> (<c>id</c>,
    /// <c>reason</c>, <c>amount</c>) for a type that cannot be reversed, an already reversed, reconciled or inactive entry, or an overdraft.</summary>
    Task<IReadOnlyList<Guid>?> ReverseAsync(ReverseTransactionInput input, Guid userId, string? correlationId = null, CancellationToken ct = default);

    /// <summary>Marks the given transactions of an account as matched to its statement. Ids that do not belong to
    /// the account are rejected; already-reconciled ones are left as they are. Returns null if the account does not exist.</summary>
    Task<int?> ReconcileAsync(Guid accountId, IReadOnlyCollection<Guid> transactionIds, Guid userId, CancellationToken ct = default);

    /// <summary>Clears the reconciled mark from the given transactions of an account. Returns null if the account does not exist.</summary>
    Task<int?> UnreconcileAsync(Guid accountId, IReadOnlyCollection<Guid> transactionIds, CancellationToken ct = default);
}
