namespace Sellora.Infrastructure.Data;

/// <summary>
/// Name of the EF named global query filter that narrows branch documents to the caller's allowed branches (Phase 5, D6).
/// Applied to PurchaseOrders, PurchaseReturns, SalesInvoices, SalesReturns, StockAdjustments (mandatory BranchId) and to Expenses and
/// FinancialAccounts (optional BranchId: NULL = company-level, always visible). Allowed = <c>IBranchContext.AllowedBranchIds</c>, null = every branch.
/// Company-integrity reads bypass it with <c>IgnoreQueryFilters([BranchQueryFilter.Name])</c> (keeps the "Company" filter).
/// </summary>
public static class BranchQueryFilter
{
    public const string Name = "Branch";
}
