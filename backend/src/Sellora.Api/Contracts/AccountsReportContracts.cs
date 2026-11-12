using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

// All Accounts reports present money in the business base currency, so figures in different currencies can be
// added together. Where a document has its own currency, the row also carries the original amount and rate.

public record BaseCurrencyDto(string Code, string Symbol, int DecimalPrecision);

/// <summary>One entry of a purchase or sales payment ledger. Amounts are positive; <see cref="Kind"/> says whether the
/// entry is a payment, a refund or the reversal of a payment.</summary>
public record PaymentReportRowDto(
    Guid Id, DateOnly PaymentDate, PaymentEntryKind Kind, string Party, Guid DocumentId, string DocumentNo,
    PaymentMethod PaymentMethod, decimal Amount, string CurrencyCode, decimal ExchangeRateToBase, decimal BaseAmount,
    PaymentStatus DocumentPaymentStatus, string? AccountName, string? Reason
);

/// <param name="Payments">Money paid or received.</param>
/// <param name="Refunds">Money paid back (refunds).</param>
/// <param name="Reversals">Payments undone.</param>
/// <param name="Net">Payments less refunds and reversals.</param>
public record PaymentReportDto(
    IReadOnlyList<PaymentReportRowDto> Items, int Total, int Page, int PageSize,
    decimal Payments, decimal Refunds, decimal Reversals, decimal Net, BaseCurrencyDto BaseCurrency
);

/// <param name="Total">Documents' grand total, before returns.</param>
/// <param name="Returned">Value of returned goods.</param>
/// <param name="Paid">Net money paid or received on live documents (payments less refunds and reversals).</param>
/// <param name="Outstanding">Still owed on live documents.</param>
/// <param name="CreditBalance">Held beyond what is owed, or paid on cancelled documents — refundable.</param>
public record OutstandingRowDto(
    Guid PartyId, string PartyName, int DocumentCount, decimal Total, decimal Returned, decimal Net, decimal Paid,
    decimal Outstanding, decimal CreditBalance, int UnpaidCount, int PartialCount, int PaidCount
);

public record OutstandingReportDto(
    IReadOnlyList<OutstandingRowDto> Items, int Total, int Page, int PageSize,
    decimal TotalNet, decimal TotalPaid, decimal TotalOutstanding, decimal TotalCredit, BaseCurrencyDto BaseCurrency
);

public record ExpenseReportRowDto(
    Guid Id, string ExpenseNo, DateOnly ExpenseDate, string Category, string? Reference, PaymentMethod PaymentMethod,
    decimal Amount, string CurrencyCode, decimal ExchangeRateToBase, decimal BaseAmount, ExpenseStatus Status, string? AccountName
);

public record ExpenseCategoryTotalDto(string Category, int Count, decimal BaseTotal);

/// <param name="TotalBase">Total of the active expenses matching the filters, in the base currency.</param>
public record ExpenseReportDto(
    IReadOnlyList<ExpenseReportRowDto> Items, int Total, int Page, int PageSize,
    decimal TotalBase, IReadOnlyList<ExpenseCategoryTotalDto> ByCategory, BaseCurrencyDto BaseCurrency
);

public record CashFlowPeriodDto(
    string Period, decimal MoneyIn, decimal MoneyOut, decimal Net,
    decimal Received, decimal PaidToSuppliers, decimal Expenses, decimal Refunds
);

/// <param name="MoneyIn">All money that came into the business: customer payments, supplier refunds and reversed supplier payments.</param>
/// <param name="MoneyOut">All money that left: supplier payments, expenses, customer refunds and reversed customer payments.</param>
/// <param name="Refunds">Customer refunds paid out, less supplier refunds received — the net cost of refunds.</param>
/// <param name="Transfers">Moved between the business's own accounts. Shown for completeness; not part of <see cref="Net"/>.</param>
public record CashFlowReportDto(
    DateOnly? DateFrom, DateOnly? DateTo,
    decimal ReceivedFromCustomers, decimal PaidToSuppliers, decimal Expenses,
    decimal CustomerRefunds, decimal SupplierRefunds, decimal Refunds,
    decimal ReversalsIn, decimal ReversalsOut,
    decimal TransfersIn, decimal TransfersOut,
    decimal MoneyIn, decimal MoneyOut, decimal Net,
    IReadOnlyList<CashFlowPeriodDto> Months, BaseCurrencyDto BaseCurrency
);

/// <summary>Closing = Opening + MoneyIn − MoneyOut + net transfers. Opening is the balance at the start of the period,
/// and includes an opening balance entered during it.</summary>
public record CashBankReportRowDto(
    Guid AccountId, string AccountName, FinancialAccountKind Kind, bool IsActive,
    // Phase 01 (FI-D12): all figures null for a company-level account when the reader does not hold Reports.Accounts company-level.
    decimal? Opening, decimal? MoneyIn, decimal? MoneyOut, decimal? TransfersIn, decimal? TransfersOut, decimal? Closing,
    int UnreconciledCount
);

public record CashBankReportDto(
    DateOnly? DateFrom, DateOnly? DateTo, IReadOnlyList<CashBankReportRowDto> Items,
    decimal Opening, decimal MoneyIn, decimal MoneyOut, decimal TransfersIn, decimal TransfersOut, decimal Closing,
    BaseCurrencyDto BaseCurrency
);

public record PaymentMethodSummaryRowDto(PaymentMethod PaymentMethod, int Transactions, decimal Received, decimal Paid, decimal Net);

public record PaymentMethodSummaryDto(
    DateOnly? DateFrom, DateOnly? DateTo, IReadOnlyList<PaymentMethodSummaryRowDto> Items,
    decimal TotalReceived, decimal TotalPaid, decimal TotalNet, BaseCurrencyDto BaseCurrency
);
