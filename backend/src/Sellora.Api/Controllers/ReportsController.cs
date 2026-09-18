using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/reports")]
public class ReportsController(SelloraDbContext db) : ControllerBase
{
    [HttpGet("current-stock")]
    [Authorize(Policy = "Reports.Stock")]
    public async Task<ActionResult<PagedResult<CurrentStockReportRowDto>>> CurrentStock(
        [FromQuery] string? search, [FromQuery] string? category, [FromQuery] StockStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Products.Where(p => p.IsActive)
            .Select(p => new
            {
                p.Id,
                p.Sku,
                p.Name,
                p.Category,
                p.ReorderLevel,
                BaseUnitLabel = p.BaseUnit.ShortCode,
                QuantityOnHand = db.StockItems.Where(s => s.ProductId == p.Id).Select(s => s.QuantityOnHand).FirstOrDefault(),
                AverageCost = db.StockItems.Where(s => s.ProductId == p.Id).Select(s => s.AverageCost).FirstOrDefault(),
            });

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(s) || p.Sku.ToLower().Contains(s));
        }
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(p => p.Category == category);
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
        var sellingPricePerBaseUnit = await db.ProductSellingModes
            .Where(m => productIds.Contains(m.ProductId))
            .Select(m => new
            {
                m.ProductId,
                m.ConversionFactorToBase,
                Price = db.SellingPrices.Where(sp => sp.ProductSellingModeId == m.Id && sp.EffectiveTo == null).Select(sp => (decimal?)sp.Price).FirstOrDefault(),
            })
            .Where(m => m.Price != null)
            .ToListAsync(ct);

        // When a product has more than one currently-priced selling mode, the finest-grained one
        // (smallest conversion factor) is the most representative per-base-unit rate.
        var bestPriceByProduct = sellingPricePerBaseUnit
            .GroupBy(m => m.ProductId)
            .ToDictionary(
                g => g.Key,
                g => (decimal?)(g.OrderBy(m => m.ConversionFactorToBase).First().Price!.Value / g.OrderBy(m => m.ConversionFactorToBase).First().ConversionFactorToBase));

        var items = rows.Select(p =>
        {
            var pricePerBaseUnit = bestPriceByProduct.GetValueOrDefault(p.Id);
            return new CurrentStockReportRowDto(
                p.Id, p.Sku, p.Name, p.Category, p.BaseUnitLabel,
                p.QuantityOnHand, p.AverageCost, p.QuantityOnHand * p.AverageCost,
                pricePerBaseUnit, pricePerBaseUnit is null ? null : p.QuantityOnHand * pricePerBaseUnit,
                StockStatusCalculator.Calculate(p.QuantityOnHand, p.ReorderLevel));
        }).ToList();

        return Ok(new PagedResult<CurrentStockReportRowDto>(items, total, page, pageSize));
    }

    [HttpGet("current-stock/categories")]
    [Authorize(Policy = "Reports.Stock")]
    public async Task<ActionResult<IReadOnlyList<string>>> CurrentStockCategories(CancellationToken ct)
    {
        var categories = await db.Products
            .Where(p => p.IsActive && p.Category != null)
            .Select(p => p.Category!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);
        return Ok(categories);
    }

    [HttpGet("stock-movement")]
    [Authorize(Policy = "Reports.Movement")]
    public async Task<ActionResult<PagedResult<StockMovementReportRowDto>>> StockMovement(
        [FromQuery] string? search, [FromQuery] StockMovementType? type,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.StockMovements.Include(m => m.Product).ThenInclude(p => p.BaseUnit).Include(m => m.User).AsQueryable();

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
                m.Type, m.QuantityBaseUnits, m.ReferenceType, m.ReferenceNo, $"{m.User.FirstName} {m.User.LastName}"))
            .ToListAsync(ct);

        return Ok(new PagedResult<StockMovementReportRowDto>(items, total, page, pageSize));
    }

    [HttpGet("purchases")]
    [Authorize(Policy = "Reports.Purchase")]
    public async Task<ActionResult<PagedResult<PurchaseReportRowDto>>> Purchases(
        [FromQuery] string? search, [FromQuery] Guid? supplierId,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.PurchaseOrders.Include(p => p.Supplier).AsQueryable();

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
                p.Subtotal, p.TaxTotal, p.OtherChargesTotal, p.GrandTotal, p.PaidAmount, p.DueAmount, p.Status))
            .ToListAsync(ct);

        return Ok(new PagedResult<PurchaseReportRowDto>(items, total, page, pageSize));
    }

    [HttpGet("sales")]
    [Authorize(Policy = "Reports.Sales")]
    public async Task<ActionResult<PagedResult<SalesReportRowDto>>> Sales(
        [FromQuery] string? search, [FromQuery] PaymentMethod? paymentMethod,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.SalesInvoices.Include(i => i.Customer).AsQueryable();

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
                i.Subtotal, i.TaxTotal, i.DiscountTotal, i.GrandTotal, i.PaidAmount, i.DueAmount, i.Status))
            .ToListAsync(ct);

        return Ok(new PagedResult<SalesReportRowDto>(items, total, page, pageSize));
    }

    [HttpGet("profit-summary")]
    [Authorize(Policy = "Reports.Profit")]
    public async Task<ActionResult<ProfitSummaryDto>> ProfitSummary(
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo, CancellationToken ct)
    {
        var query = db.SalesInvoices.Where(i => i.Status == SalesStatus.Completed).AsQueryable();
        if (dateFrom is not null) query = query.Where(i => i.InvoiceDate >= dateFrom);
        if (dateTo is not null) query = query.Where(i => i.InvoiceDate <= dateTo);

        var invoiceCount = await query.CountAsync(ct);
        var lineTotals = await query.SelectMany(i => i.Lines)
            .Select(l => new { l.TaxableValue, l.CostOfGoodsSold })
            .ToListAsync(ct);

        var salesRevenue = lineTotals.Sum(l => l.TaxableValue);
        var cogs = lineTotals.Sum(l => l.CostOfGoodsSold);
        var grossProfit = salesRevenue - cogs;
        var grossMarginPercent = salesRevenue > 0 ? Math.Round(grossProfit / salesRevenue * 100, 2) : (decimal?)null;

        return Ok(new ProfitSummaryDto(dateFrom, dateTo, invoiceCount, salesRevenue, cogs, grossProfit, grossMarginPercent));
    }
}
