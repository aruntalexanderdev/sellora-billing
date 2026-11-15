using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

public enum FinancialTransactionType
{
    /// <summary>Money received against a sales invoice.</summary>
    CustomerPayment,
    /// <summary>Money paid against a purchase.</summary>
    SupplierPayment,
    /// <summary>Money paid for an operating expense.</summary>
    ExpensePayment,
    /// <summary>Money paid back to a customer.</summary>
    CustomerRefund,
    /// <summary>Money a supplier paid back to us.</summary>
    SupplierRefund,
    /// <summary>The opposite of an earlier payment that was entered in error.</summary>
    PaymentReversal,
    /// <summary>The balance an account started with. Manual; has no source record.</summary>
    OpeningBalance,
    /// <summary>One leg of a transfer between two accounts (out of one, into the other). Manual; has no source record.</summary>
    Transfer,
    /// <summary>Money put into an account from outside the business's trading (capital, loan, deposit, unallocated customer receipt). Manual; In.</summary>
    Funding,
    /// <summary>The opposite entry of a manual entry (funding, transfer leg or opening balance) that was made in error. Manual.</summary>
    Reversal,
}

/// <summary>Where funded money came from (Phase 07).</summary>
public enum FundingSource
{
    OwnerCapital,
    Loan,
    CashDeposit,
    /// <summary>An unallocated receipt from a customer: no receivable or revenue effect.</summary>
    CustomerReceipt,
    Other,
}

public enum MoneyDirection
{
    In,
    Out,
}

public enum FinancialTransactionStatus
{
    Active,
    /// <summary>The source record was cancelled; the transaction is kept for history but excluded from totals.</summary>
    Cancelled,
}

/// <summary>
/// One movement of the business's money, answering "what happened to our money?". It is created in the same
/// database transaction as the source record that caused it and never replaces that record: purchase payments,
/// sales payments and expenses keep their own source-specific rows, and this points back to exactly one of
/// them. Amounts are always positive; <see cref="Direction"/> says whether money came in or went out.
/// </summary>
public class FinancialTransaction : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }

    public FinancialTransactionType Type { get; set; }
    public MoneyDirection Direction { get; set; }
    public FinancialTransactionStatus Status { get; set; } = FinancialTransactionStatus.Active;

    public DateOnly TransactionDate { get; set; }

    public Guid CurrencyId { get; set; }
    public Currency Currency { get; set; } = null!;

    /// <summary>Amount in <see cref="Currency"/>.</summary>
    public decimal Amount { get; set; }

    /// <summary>The rate snapshotted on the source record — never recomputed.</summary>
    public decimal ExchangeRateToBase { get; set; } = 1m;

    public decimal BaseAmount { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    // Exactly one source is set (enforced by a check constraint).
    public Guid? PurchasePaymentId { get; set; }
    public PurchasePayment? PurchasePayment { get; set; }
    public Guid? SalesPaymentId { get; set; }
    public SalesPayment? SalesPayment { get; set; }
    public Guid? ExpenseId { get; set; }
    public Expense? Expense { get; set; }

    /// <summary>The cash or bank account the money moved through. Null for movements made before accounts
    /// existed, or where the user chose not to track an account; those do not count toward any balance.
    /// Always set for manual transactions.</summary>
    public Guid? AccountId { get; set; }
    public FinancialAccount? Account { get; set; }

    /// <summary>Shared by the two legs of a transfer.</summary>
    public Guid? TransferGroupId { get; set; }

    /// <summary>Set exactly when <see cref="Type"/> is Funding.</summary>
    public FundingSource? FundingSource { get; set; }
    public string? SourceReference { get; set; }
    public string? Counterparty { get; set; }

    /// <summary>Only for CustomerReceipt funding.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Set exactly when <see cref="Type"/> is Reversal: the entry this one cancels out. An entry is reversed at most once.</summary>
    public Guid? ReversalOfTransactionId { get; set; }

    /// <summary>Mandatory for Reversal and for Funding from Other.</summary>
    public string? Reason { get; set; }

    public string? Notes { get; set; }

    /// <summary>Set when the transaction has been matched to the bank/cash statement.</summary>
    public DateTimeOffset? ReconciledAt { get; set; }
    public Guid? ReconciledByUserId { get; set; }
    public User? ReconciledByUser { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static FinancialTransaction ForPurchasePayment(PurchasePayment payment, Guid currencyId, Guid? accountId) => new()
    {
        Id = Guid.NewGuid(),
        Type = payment.Kind switch
        {
            PaymentEntryKind.Refund => FinancialTransactionType.SupplierRefund,
            PaymentEntryKind.Reversal => FinancialTransactionType.PaymentReversal,
            _ => FinancialTransactionType.SupplierPayment,
        },
        // Paying a supplier sends money out; a refund or a reversal of such a payment brings it back in.
        Direction = payment.Kind == PaymentEntryKind.Payment ? MoneyDirection.Out : MoneyDirection.In,
        TransactionDate = payment.PaymentDate,
        CurrencyId = currencyId,
        Amount = payment.Amount,
        ExchangeRateToBase = payment.ExchangeRateToBase,
        BaseAmount = payment.BaseAmount,
        PaymentMethod = payment.PaymentMethod,
        PurchasePaymentId = payment.Id,
        AccountId = accountId,
        CreatedByUserId = payment.CreatedByUserId,
    };

    public static FinancialTransaction ForSalesPayment(SalesPayment payment, Guid currencyId, Guid? accountId) => new()
    {
        Id = Guid.NewGuid(),
        Type = payment.Kind switch
        {
            PaymentEntryKind.Refund => FinancialTransactionType.CustomerRefund,
            PaymentEntryKind.Reversal => FinancialTransactionType.PaymentReversal,
            _ => FinancialTransactionType.CustomerPayment,
        },
        // Receiving from a customer brings money in; a refund or a reversal of such a payment sends it back out.
        Direction = payment.Kind == PaymentEntryKind.Payment ? MoneyDirection.In : MoneyDirection.Out,
        TransactionDate = payment.PaymentDate,
        CurrencyId = currencyId,
        Amount = payment.Amount,
        ExchangeRateToBase = payment.ExchangeRateToBase,
        BaseAmount = payment.BaseAmount,
        PaymentMethod = payment.PaymentMethod,
        SalesPaymentId = payment.Id,
        AccountId = accountId,
        CreatedByUserId = payment.CreatedByUserId,
    };

    public static FinancialTransaction ForExpense(Expense expense, Guid? accountId) => new()
    {
        Id = Guid.NewGuid(),
        Type = FinancialTransactionType.ExpensePayment,
        Direction = MoneyDirection.Out,
        TransactionDate = expense.ExpenseDate,
        CurrencyId = expense.CurrencyId,
        Amount = expense.Amount,
        ExchangeRateToBase = expense.ExchangeRateToBase,
        BaseAmount = expense.BaseAmount,
        PaymentMethod = expense.PaymentMethod,
        ExpenseId = expense.Id,
        AccountId = accountId,
        CreatedByUserId = expense.CreatedByUserId,
    };
}
