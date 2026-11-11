using Sellora.Domain.Entities;
using Sellora.Domain.Stock;

namespace Sellora.Api.Contracts;

public record DashboardSalesSummaryDto(decimal Amount, int TransactionCount);

public record DashboardPurchaseSummaryDto(decimal Amount, int TransactionCount);

public record DashboardLowStockItemDto(
    Guid ProductId, string Name, string BaseUnitLabel, decimal QuantityOnHand, decimal ReorderLevel, StockStatus Status,
    int BaseDecimalPlaces = 0
);

public record DashboardRecentSaleDto(Guid Id, string InvoiceNo, string CustomerName, decimal GrandTotal, SalesStatus Status, DateTimeOffset CreatedAt);

public record DashboardRecentPurchaseDto(Guid Id, string PurchaseNo, string SupplierName, decimal GrandTotal, PurchaseStatus Status, DateTimeOffset CreatedAt);

/// <summary>
/// Every section is nullable and independently populated based on the caller's own permissions
/// (docx §7/§14 — Dashboard is open to all authenticated users, but a Sales-only user shouldn't see
/// purchase figures any more than the Purchases screen would show them). This is enforced here, not
/// just hidden in the UI, per the app-wide rule that frontend checks never replace backend authorization.
/// </summary>
public record DashboardDto(
    DashboardSalesSummaryDto? TodaysSales,
    DashboardPurchaseSummaryDto? TodaysPurchases,
    decimal? CurrentStockValue,
    decimal? OutstandingCustomerDue,
    IReadOnlyList<DashboardLowStockItemDto>? LowStockItems,
    IReadOnlyList<DashboardRecentSaleDto>? RecentSales,
    IReadOnlyList<DashboardRecentPurchaseDto>? RecentPurchases
);
