using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/dashboard")]
[Authorize]
public class DashboardController(SelloraDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DashboardDto>> Get(CancellationToken ct)
    {
        var canViewSales = User.HasClaim(AuthClaimTypes.Permission, "Sales.View");
        var canViewPurchases = User.HasClaim(AuthClaimTypes.Permission, "Purchase.View");
        var canViewInventory = User.HasClaim(AuthClaimTypes.Permission, "Inventory.View");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        DashboardSalesSummaryDto? todaysSales = null;
        decimal? outstandingCustomerDue = null;
        IReadOnlyList<DashboardRecentSaleDto>? recentSales = null;

        if (canViewSales)
        {
            var todaysInvoices = await db.SalesInvoices
                .Where(i => i.Status == SalesStatus.Completed && i.InvoiceDate == today)
                .Select(i => i.GrandTotal)
                .ToListAsync(ct);
            todaysSales = new DashboardSalesSummaryDto(todaysInvoices.Sum(), todaysInvoices.Count);

            outstandingCustomerDue = await db.SalesInvoices
                .Where(i => i.Status == SalesStatus.Completed)
                .Select(i => i.GrandTotal - i.ReturnedAmount - i.PaidAmount < 0 ? 0 : i.GrandTotal - i.ReturnedAmount - i.PaidAmount)
                .SumAsync(ct);

            recentSales = await db.SalesInvoices.Include(i => i.Customer)
                .OrderByDescending(i => i.CreatedAt).Take(5)
                .Select(i => new DashboardRecentSaleDto(i.Id, i.InvoiceNo, i.Customer.Name, i.GrandTotal, i.Status, i.CreatedAt))
                .ToListAsync(ct);
        }

        DashboardPurchaseSummaryDto? todaysPurchases = null;
        IReadOnlyList<DashboardRecentPurchaseDto>? recentPurchases = null;

        if (canViewPurchases)
        {
            var todaysOrders = await db.PurchaseOrders
                .Where(p => p.Status == PurchaseStatus.Completed && p.PurchaseDate == today)
                .Select(p => p.GrandTotal)
                .ToListAsync(ct);
            todaysPurchases = new DashboardPurchaseSummaryDto(todaysOrders.Sum(), todaysOrders.Count);

            recentPurchases = await db.PurchaseOrders.Include(p => p.Supplier)
                .OrderByDescending(p => p.CreatedAt).Take(5)
                .Select(p => new DashboardRecentPurchaseDto(p.Id, p.PurchaseNo, p.Supplier.Name, p.GrandTotal, p.Status, p.CreatedAt))
                .ToListAsync(ct);
        }

        decimal? currentStockValue = null;
        IReadOnlyList<DashboardLowStockItemDto>? lowStockItems = null;

        if (canViewInventory)
        {
            currentStockValue = await db.StockItems.SumAsync(s => s.QuantityOnHand * s.AverageCost, ct);

            var candidates = await db.Products.Where(p => p.IsActive)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.ReorderLevel,
                    BaseUnitLabel = p.BaseUnit.ShortCode,
                    QuantityOnHand = db.StockItems.Where(s => s.ProductId == p.Id).Select(s => s.QuantityOnHand).FirstOrDefault(),
                })
                .Where(p => p.QuantityOnHand < p.ReorderLevel)
                .ToListAsync(ct);

            lowStockItems = candidates
                .OrderBy(p => p.QuantityOnHand - p.ReorderLevel)
                .Take(10)
                .Select(p => new DashboardLowStockItemDto(
                    p.Id, p.Name, p.BaseUnitLabel, p.QuantityOnHand, p.ReorderLevel,
                    StockStatusCalculator.Calculate(p.QuantityOnHand, p.ReorderLevel)))
                .ToList();
        }

        return Ok(new DashboardDto(todaysSales, todaysPurchases, currentStockValue, outstandingCustomerDue, lowStockItems, recentSales, recentPurchases));
    }
}
