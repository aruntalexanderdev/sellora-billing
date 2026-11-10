using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data;

/// <summary>
/// Phase 5 (D6/D7) branch visibility of the financial transaction ledger, kept in one place so every read agrees.
/// A transaction is visible iff its source document is visible to the caller (purchase payment -> purchase order, sales payment ->
/// sales invoice, expense), or, when it has no source document (opening balance, transfer), its account is visible
/// (company-level accounts are always visible). Visibility is resolved by id lookups against the branch-filtered roots —
/// never through navigation projections.
/// </summary>
public static class FinancialTransactionVisibility
{
    public static IQueryable<FinancialTransaction> VisibleTo(this IQueryable<FinancialTransaction> query, SelloraDbContext db) =>
        query.Where(t =>
            (t.PurchasePaymentId != null && db.PurchasePayments.Any(p => p.Id == t.PurchasePaymentId && db.PurchaseOrders.Any(o => o.Id == p.PurchaseOrderId)))
            || (t.SalesPaymentId != null && db.SalesPayments.Any(p => p.Id == t.SalesPaymentId && db.SalesInvoices.Any(i => i.Id == p.SalesInvoiceId)))
            || (t.ExpenseId != null && db.Expenses.Any(e => e.Id == t.ExpenseId))
            || (t.PurchasePaymentId == null && t.SalesPaymentId == null && t.ExpenseId == null
                && (t.AccountId == null || db.FinancialAccounts.Any(a => a.Id == t.AccountId))));
}
