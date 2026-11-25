using Microsoft.AspNetCore.Authorization;
using Sellora.Api.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/reports")]
[CompanyScopeRead]
public class ReportsController(SelloraDbContext db, IEffectivePermissionService effectivePermissions) : ControllerBase
{
    [HttpGet("current-stock")]
    [Authorize(Policy = "Reports.Stock")]
    public async Task<ActionResult<PagedResult<CurrentStockReportRowDto>>> CurrentStock(
        [FromQuery] string? search, [FromQuery] string? category, [FromQuery] StockStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] Guid? stockLocationId = null, CancellationToken ct = default,
        [FromQuery] Guid? categoryId = null, [FromQuery] Guid? subcategoryId = null)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Phase 8: quantity summed per product over the visible (or requested) locations; value only is aggregated across products.
        var visibility = await StockVisibility.LoadAsync(db, effectivePermissions, "Reports.Stock", ct);
        if (visibility.DenyUnlessVisible(this, stockLocationId) is { } denied) return denied;
        var scope = visibility.Scope(stockLocationId);

        var query = db.Products.Where(p => p.IsActive)
            .Select(p => new
            {
                p.Id,
                p.Sku,
                p.Name,
                p.Category,
                p.CategoryId,
                p.SubcategoryId,
                SubcategoryName = db.ProductSubcategories.Where(s => s.Id == p.SubcategoryId).Select(s => s.Name).FirstOrDefault(),
                p.ReorderLevel,
                BaseUnitLabel = p.BaseUnit.ShortCode,
                BaseDecimalPlaces = (int)p.BaseUnit.DecimalPlaces,
                QuantityOnHand = db.StockItems.Where(s => s.ProductId == p.Id && scope.Contains(s.StockLocationId)).Sum(s => s.QuantityOnHand),
                StockValue = db.StockItems.Where(s => s.ProductId == p.Id && scope.Contains(s.StockLocationId)).Sum(s => s.QuantityOnHand * s.AverageCost),
                MaxAverageCost = db.StockItems.Where(s => s.ProductId == p.Id && scope.Contains(s.StockLocationId)).Max(s => (decimal?)s.AverageCost) ?? 0m,
            });

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(s) || p.Sku.ToLower().Contains(s));
        }
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(p => p.Category == category); // legacy name filter (FI-D17)
        if (categoryId is not null) query = query.Where(p => p.CategoryId == categoryId);
        if (subcategoryId is not null) query = query.Where(p => p.SubcategoryId == subcategoryId);
        if (status is not null)
        {
            query = status switch
            {
                StockStatus.OutOfStock => query.Where(p => p.QuantityOnHand <= 0),
                StockStatus.Low => query.Where(p => p.QuantityOnHand > 0 && p.QuantityOnHand < p.ReorderLevel),
                _ => query.Where(p => p.QuantityOnHand > 0 && p.QuantityOnHand >= p.ReorderLevel),
            };
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(p => p.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var productIds = rows.Select(p => p.Id).ToList();
        var pricedUnits = await db.ProductUnits
            .Where(u => productIds.Contains(u.ProductId) && u.IsActive && u.IsSellable)
            .Select(u => new
            {
                u.ProductId,
                u.IsBaseUnit,
                u.ConversionFactorToBase,
                Price = db.SellingPrices.Where(sp => sp.ProductUnitId == u.Id && sp.EffectiveTo == null).Select(sp => (decimal?)sp.Price).FirstOrDefault(),
            })
            .Where(u => u.Price != null && u.ConversionFactorToBase > 0)
            .ToListAsync(ct);

        // Per-base-unit selling rate: the base unit's own price when it is priced, otherwise the finest-grained
        // (smallest factor) priced active unit's price divided by its factor. Inactive and non-sellable units are ignored,
        // matching the pricing grid's SuggestedPrice anchor.
        var bestPriceByProduct = pricedUnits
            .GroupBy(u => u.ProductId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var best = g.OrderByDescending(u => u.IsBaseUnit).ThenBy(u => u.ConversionFactorToBase).First();
                    return (decimal?)(best.Price!.Value / best.ConversionFactorToBase);
                });

        var balances = await visibility.BalancesAsync(db, productIds, scope, ct);
        var breakdowns = await BreakdownUnits.LoadAsync(db, productIds, ct);
        var items = rows.Select(p =>
        {
            var pricePerBaseUnit = bestPriceByProduct.GetValueOrDefault(p.Id);
            return new CurrentStockReportRowDto(
                p.Id, p.Sku, p.Name, p.Category, p.BaseUnitLabel,
                p.QuantityOnHand, StockVisibility.WeightedAverage(p.QuantityOnHand, p.StockValue, p.MaxAverageCost), p.StockValue,
                pricePerBaseUnit, pricePerBaseUnit is null ? null : p.QuantityOnHand * pricePerBaseUnit,
                StockStatusCalculator.Calculate(p.QuantityOnHand, p.ReorderLevel),
                balances.GetValueOrDefault(p.Id),
                p.BaseDecimalPlaces, breakdowns.GetValueOrDefault(p.Id) ?? [],
                p.CategoryId, p.SubcategoryName);
        }).ToList();

        return Ok(new PagedResult<CurrentStockReportRowDto>(items, total, page, pageSize));
    }

    [HttpGet("current-stock/categories")]
    [Authorize(Policy = "Reports.Stock")]
    public async Task<ActionResult<IReadOnlyList<string>>> CurrentStockCategories(CancellationToken ct)
    {
        var categories = await db.ProductCategories
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => c.Name)
            .ToListAsync(ct);
        return Ok(categories);
    }

    [HttpGet("stock-movement")]
    [Authorize(Policy = "Reports.Movement")]
    public async Task<ActionResult<PagedResult<StockMovementReportRowDto>>> StockMovement(
        [FromQuery] string? search, [FromQuery] StockMovementType? type,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] Guid? stockLocationId = null, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var visibility = await StockVisibility.LoadAsync(db, effectivePermissions, "Reports.Movement", ct);
        if (visibility.DenyUnlessVisible(this, stockLocationId) is { } denied) return denied;
        var scope = visibility.Scope(stockLocationId);

        var query = db.StockMovements.Include(m => m.Product).ThenInclude(p => p.BaseUnit).Include(m => m.User)
            .Where(m => scope.Contains(m.StockLocationId));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(m => m.Product.Name.ToLower().Contains(s) || m.Product.Sku.ToLower().Contains(s) || m.ReferenceNo.ToLower().Contains(s));
        }
        if (type is not null) query = query.Where(m => m.Type == type);
        if (dateFrom is not null)
        {
            var from = new DateTimeOffset(dateFrom.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(m => m.OccurredAt >= from);
        }
        if (dateTo is not null)
        {
            var to = new DateTimeOffset(dateTo.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(m => m.OccurredAt < to);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(m => m.OccurredAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(m => new StockMovementReportRowDto(
                m.Id, m.OccurredAt, m.Product.Name, m.Product.Sku, m.Product.BaseUnit.ShortCode,
                m.Type, m.QuantityBaseUnits, m.ReferenceType, m.ReferenceNo, $"{m.User.FirstName} {m.User.LastName}",
                m.ReasonCode, m.EnteredQuantity, m.EnteredProductUnitId, m.EnteredUnitCode, m.ConversionFactorSnapshot,
                m.EnteredProductUnit != null ? m.EnteredProductUnit.Unit.DecimalPlaces : (int?)null,
                m.Product.BaseUnit.DecimalPlaces,
                m.StockLocationId, null, null, m.ReferenceId))
            .ToListAsync(ct);
        items = items.Select(r => r with { StockLocationCode = visibility.Code(r.StockLocationId!.Value), StockLocationName = visibility.Name(r.StockLocationId!.Value) }).ToList();

        return Ok(new PagedResult<StockMovementReportRowDto>(items, total, page, pageSize));
    }

    [HttpGet("purchases")]
    [Authorize(Policy = "Reports.Purchase")]
    public async Task<ActionResult<PagedResult<PurchaseReportRowDto>>> Purchases(
        [FromQuery] string? search, [FromQuery] Guid? supplierId,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] Guid? branchId = null, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        if (await this.DenyUnlessAllowedAsync(db, branchId, ct) is { } denied) return denied;

        var query = db.PurchaseOrders.Include(p => p.Supplier).AsQueryable();
        if (branchId is not null) query = query.Where(p => p.BranchId == branchId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(p => p.PurchaseNo.ToLower().Contains(s) || p.Supplier.Name.ToLower().Contains(s)
                || p.Lines.Any(l => l.Product.Name.ToLower().Contains(s) || l.Product.Sku.ToLower().Contains(s)));
        }
        if (supplierId is not null) query = query.Where(p => p.SupplierId == supplierId);
        if (dateFrom is not null) query = query.Where(p => p.PurchaseDate >= dateFrom);
        if (dateTo is not null) query = query.Where(p => p.PurchaseDate <= dateTo);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(p => p.PurchaseDate).ThenByDescending(p => p.PurchaseNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new PurchaseReportRowDto(
                p.Id, p.PurchaseNo, p.PurchaseDate, p.Supplier.Name,
                p.Subtotal, p.TaxTotal, p.OtherChargesTotal, p.GrandTotal, p.PaidAmount, p.DueAmount, p.Status,
                p.Currency.Code, p.OriginalGrandTotal, p.BranchId, null, null,
                p.TaxableTotal ?? p.Subtotal + p.OtherChargesTotal - p.DiscountTotal)) // NULL on history (TP3-Q1)
            .ToListAsync(ct);

        // Tax by component kind: one grouped query for the page. A row with no component (legacy) counts as Other; zero rows (non-taxable) add nothing.
        var pageIds = items.Select(i => i.Id).ToList();
        var byKind = (await db.PurchaseOrderLineTaxes.Where(t => pageIds.Contains(t.Line.PurchaseOrderId) && t.TaxAmount != 0m)
                .GroupBy(t => new { t.Line.PurchaseOrderId, Kind = t.TaxComponentId == null ? TaxComponentKind.Other : t.Kind })
                .Select(g => new { g.Key.PurchaseOrderId, g.Key.Kind, Amount = g.Sum(t => t.TaxAmount) })
                .ToListAsync(ct))
            .GroupBy(x => x.PurchaseOrderId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PurchaseReportTaxDto>)g.OrderBy(x => x.Kind).Select(x => new PurchaseReportTaxDto(x.Kind, x.Amount)).ToList());

        var labels = await BranchLabels.LoadAsync(db, ct);
        items = items.Select(i => i with
        {
            BranchCode = labels.Code(i.BranchId), BranchName = labels.Name(i.BranchId),
            TaxByKind = byKind.GetValueOrDefault(i.Id) ?? [],
        }).ToList();

        return Ok(new PagedResult<PurchaseReportRowDto>(items, total, page, pageSize));
    }

    [HttpGet("sales")]
    [Authorize(Policy = "Reports.Sales")]
    public async Task<ActionResult<PagedResult<SalesReportRowDto>>> Sales(
        [FromQuery] string? search, [FromQuery] PaymentMethod? paymentMethod,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] Guid? branchId = null, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        if (await this.DenyUnlessAllowedAsync(db, branchId, ct) is { } denied) return denied;

        var query = db.SalesInvoices.Include(i => i.Customer).AsQueryable();
        if (branchId is not null) query = query.Where(i => i.BranchId == branchId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(i => i.InvoiceNo.ToLower().Contains(s) || i.Customer.Name.ToLower().Contains(s)
                || i.Lines.Any(l => l.Product.Name.ToLower().Contains(s) || l.Product.Sku.ToLower().Contains(s)));
        }
        if (paymentMethod is not null) query = query.Where(i => i.PaymentMethod == paymentMethod);
        if (dateFrom is not null) query = query.Where(i => i.InvoiceDate >= dateFrom);
        if (dateTo is not null) query = query.Where(i => i.InvoiceDate <= dateTo);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.InvoiceNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(i => new SalesReportRowDto(
                i.Id, i.InvoiceNo, i.InvoiceDate, i.Customer.Name,
                i.Subtotal, i.TaxTotal, i.DiscountTotal, i.GrandTotal, i.PaidAmount, i.DueAmount, i.Status, i.BranchId, null, null))
            .ToListAsync(ct);

        var labels = await BranchLabels.LoadAsync(db, ct);
        items = items.Select(i => i with { BranchCode = labels.Code(i.BranchId), BranchName = labels.Name(i.BranchId) }).ToList();

        return Ok(new PagedResult<SalesReportRowDto>(items, total, page, pageSize));
    }

    [HttpGet("profit-summary")]
    [Authorize(Policy = "Reports.Profit")]
    public async Task<ActionResult<ProfitSummaryDto>> ProfitSummary(
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo, [FromQuery] Guid? branchId, CancellationToken ct)
    {
        if (await this.DenyUnlessAllowedAsync(db, branchId, ct) is { } denied) return denied;

        var query = db.SalesInvoices.Where(i => i.Status == SalesStatus.Completed).AsQueryable();
        if (branchId is not null) query = query.Where(i => i.BranchId == branchId);
        if (dateFrom is not null) query = query.Where(i => i.InvoiceDate >= dateFrom);
        if (dateTo is not null) query = query.Where(i => i.InvoiceDate <= dateTo);

        var invoiceCount = await query.CountAsync(ct);
        var lineTotals = await query.SelectMany(i => i.Lines)
            .Select(l => new { l.TaxableValue, l.CostOfGoodsSold })
            .ToListAsync(ct);

        // Returned goods come off both sides: their pre-tax value leaves revenue and their cost leaves COGS.
        var returned = await db.SalesReturnLines.Where(l => query.Any(i => i.Id == l.SalesReturn.SalesInvoiceId))
            .Select(l => new { l.TaxableValue, l.CostRestored }).ToListAsync(ct);

        var salesRevenue = lineTotals.Sum(l => l.TaxableValue) - returned.Sum(l => l.TaxableValue);
        var cogs = lineTotals.Sum(l => l.CostOfGoodsSold) - returned.Sum(l => l.CostRestored);
        var grossProfit = salesRevenue - cogs;
        var grossMarginPercent = salesRevenue > 0 ? Math.Round(grossProfit / salesRevenue * 100, 2) : (decimal?)null;

        return Ok(new ProfitSummaryDto(dateFrom, dateTo, invoiceCount, salesRevenue, cogs, grossProfit, grossMarginPercent, branchId));
    }
}
