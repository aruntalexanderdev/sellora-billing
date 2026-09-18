using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/stock")]
[Authorize(Policy = "Inventory.View")]
public class StockController(SelloraDbContext db) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<ActionResult<PagedResult<StockOverviewItemDto>>> Overview(
        [FromQuery] string? search, [FromQuery] string? category, [FromQuery] Guid? unitId, [FromQuery] StockStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Products.Where(p => p.IsActive)
            .Select(p => new
            {
                p.Id, p.Sku, p.Name, p.Category, p.ReorderLevel,
                BaseUnitLabel = p.BaseUnit.ShortCode,
                QuantityOnHand = db.StockItems.Where(s => s.ProductId == p.Id).Select(s => s.QuantityOnHand).FirstOrDefault(),
                AverageCost = db.StockItems.Where(s => s.ProductId == p.Id).Select(s => s.AverageCost).FirstOrDefault(),
                UnitId = p.BaseUnitId,
            });

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(s) || p.Sku.ToLower().Contains(s));
        }
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(p => p.Category == category);
        if (unitId is not null) query = query.Where(p => p.UnitId == unitId);
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

        var items = rows.Select(p => new StockOverviewItemDto(
            p.Id, p.Sku, p.Name, p.Category, p.BaseUnitLabel,
            p.QuantityOnHand, p.AverageCost, p.QuantityOnHand * p.AverageCost,
            StockStatusCalculator.Calculate(p.QuantityOnHand, p.ReorderLevel)
        )).ToList();

        return Ok(new PagedResult<StockOverviewItemDto>(items, total, page, pageSize));
    }

    [HttpGet("{productId:guid}")]
    public async Task<ActionResult<StockDetailDto>> Detail(Guid productId, CancellationToken ct)
    {
        var product = await db.Products.Include(p => p.BaseUnit).Include(p => p.Tax)
            .SingleOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null) return NotFound();

        var stockItem = await db.StockItems.SingleOrDefaultAsync(s => s.ProductId == productId, ct);
        var qty = stockItem?.QuantityOnHand ?? 0;
        var avgCost = stockItem?.AverageCost ?? 0;

        var movements = await db.StockMovements.Include(m => m.User)
            .Where(m => m.ProductId == productId)
            .OrderByDescending(m => m.OccurredAt)
            .Select(m => new StockMovementDto(m.Id, m.OccurredAt, m.Type, m.QuantityBaseUnits, m.ReferenceType, m.ReferenceNo, $"{m.User.FirstName} {m.User.LastName}", m.Notes))
            .ToListAsync(ct);

        var batches = await db.PurchaseBatches
            .Include(b => b.PurchaseOrderLine).ThenInclude(l => l!.PurchaseOrder)
            .Include(b => b.StockAdjustment)
            .Where(b => b.ProductId == productId)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new StockBatchDto(
                b.Id, b.CreatedAt,
                b.PurchaseOrderLineId != null ? "Purchase" : "Adjustment",
                b.PurchaseOrderLineId != null ? b.PurchaseOrderLine!.PurchaseOrder.PurchaseNo : b.StockAdjustment!.AdjustmentNo,
                b.PurchasedBaseQty, b.RemainingBaseQty, b.UnitCost))
            .ToListAsync(ct);

        return Ok(new StockDetailDto(
            product.Id, product.Sku, product.Name, product.Category, product.BaseUnit.ShortCode, product.Tax.Name,
            qty, avgCost, qty * avgCost, product.ReorderLevel, StockStatusCalculator.Calculate(qty, product.ReorderLevel),
            movements, batches));
    }
}
