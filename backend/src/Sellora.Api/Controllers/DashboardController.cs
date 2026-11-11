using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/dashboard")]
[Authorize]
public class DashboardController(SelloraDbContext db, IEffectivePermissionService effectivePermissions, BranchContext branchContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DashboardDto>> Get(CancellationToken ct)
    {
        // Phase 7: sections are shown by effective permission at the working branch. Branch-scoped holders see only their branch's documents:
        // the section's queries run through the Branch filter, which is narrowed to the branches where the code is held.
        var set = await effectivePermissions.GetCurrentAsync(ct);
        var working = branchContext.RequestedBranchId ?? branchContext.PrimaryBranchId;
        bool Holds(string code) => set.HasCompanyLevel(code) || (working is { } w && set.HasAtBranch(code, w));
        var canViewSales = Holds("Sales.View");
        var canViewPurchases = Holds("Purchase.View");
        // null = company-level grant (no extra narrowing); otherwise only the branches whose roles hold the code.
        var salesBranches = set.PermittedBranches("Sales.View")?.ToArray();
        var purchaseBranches = set.PermittedBranches("Purchase.View")?.ToArray();
        var canViewInventory = Holds("Inventory.View");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        DashboardSalesSummaryDto? todaysSales = null;
        decimal? outstandingCustomerDue = null;
        IReadOnlyList<DashboardRecentSaleDto>? recentSales = null;

        if (canViewSales)
        {
            var todaysInvoices = await db.SalesInvoices
                .Where(i => salesBranches == null || salesBranches.Contains(i.BranchId))
                .Where(i => i.Status == SalesStatus.Completed && i.InvoiceDate == today)
                .Select(i => i.GrandTotal)
                .ToListAsync(ct);
            todaysSales = new DashboardSalesSummaryDto(todaysInvoices.Sum(), todaysInvoices.Count);

            outstandingCustomerDue = await db.SalesInvoices
                .Where(i => salesBranches == null || salesBranches.Contains(i.BranchId))
                .Where(i => i.Status == SalesStatus.Completed)
                .Select(i => i.GrandTotal - i.ReturnedAmount - i.PaidAmount < 0 ? 0 : i.GrandTotal - i.ReturnedAmount - i.PaidAmount)
                .SumAsync(ct);

            recentSales = await db.SalesInvoices.Include(i => i.Customer)
                .Where(i => salesBranches == null || salesBranches.Contains(i.BranchId))
                .OrderByDescending(i => i.CreatedAt).Take(5)
                .Select(i => new DashboardRecentSaleDto(i.Id, i.InvoiceNo, i.Customer.Name, i.GrandTotal, i.Status, i.CreatedAt))
                .ToListAsync(ct);
        }

        DashboardPurchaseSummaryDto? todaysPurchases = null;
        IReadOnlyList<DashboardRecentPurchaseDto>? recentPurchases = null;

        if (canViewPurchases)
        {
            var todaysOrders = await db.PurchaseOrders
                .Where(p => purchaseBranches == null || purchaseBranches.Contains(p.BranchId))
                .Where(p => p.Status == PurchaseStatus.Completed && p.PurchaseDate == today)
                .Select(p => p.GrandTotal)
                .ToListAsync(ct);
            todaysPurchases = new DashboardPurchaseSummaryDto(todaysOrders.Sum(), todaysOrders.Count);

            recentPurchases = await db.PurchaseOrders.Include(p => p.Supplier)
                .Where(p => purchaseBranches == null || purchaseBranches.Contains(p.BranchId))
                .OrderByDescending(p => p.CreatedAt).Take(5)
                .Select(p => new DashboardRecentPurchaseDto(p.Id, p.PurchaseNo, p.Supplier.Name, p.GrandTotal, p.Status, p.CreatedAt))
                .ToListAsync(ct);
        }

        decimal? currentStockValue = null;
        IReadOnlyList<DashboardLowStockItemDto>? lowStockItems = null;

        if (canViewInventory)
        {
            // Phase 8: only locations visible under Inventory.View (branch access + permitted branches + company-level locations).
            var visibility = await StockVisibility.LoadAsync(db, effectivePermissions, "Inventory.View", ct);
            var scope = visibility.Scope(null);
            currentStockValue = await db.StockItems.Where(s => scope.Contains(s.StockLocationId)).SumAsync(s => s.QuantityOnHand * s.AverageCost, ct);

            var candidates = await db.Products.Where(p => p.IsActive)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.ReorderLevel,
                    BaseUnitLabel = p.BaseUnit.ShortCode,
                    // Summed over the visible locations; compared with the product-level ReorderLevel (per-location reorder levels are a later phase).
                    QuantityOnHand = db.StockItems.Where(s => s.ProductId == p.Id && scope.Contains(s.StockLocationId)).Sum(s => s.QuantityOnHand),
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
