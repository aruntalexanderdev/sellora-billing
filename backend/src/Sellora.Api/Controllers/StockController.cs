using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/stock")]
[Authorize(Policy = "Inventory.View")]
public class StockController(SelloraDbContext db, IEffectivePermissionService effectivePermissions) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<ActionResult<PagedResult<StockOverviewItemDto>>> Overview(
        [FromQuery] string? search, [FromQuery] string? category, [FromQuery] Guid? unitId, [FromQuery] StockStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] Guid? stockLocationId = null, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Phase 8: quantities are summed per product over the visible locations (or the one requested); value is the sum of qty x average cost.
        var visibility = await StockVisibility.LoadAsync(db, effectivePermissions, "Inventory.View", ct);
        if (visibility.DenyUnlessVisible(this, stockLocationId) is { } denied) return denied;
        var scope = visibility.Scope(stockLocationId);

        var query = db.Products.Where(p => p.IsActive)
            .Select(p => new
            {
                p.Id, p.Sku, p.Name, p.Category, p.ReorderLevel,
                BaseUnitLabel = p.BaseUnit.ShortCode,
                BaseDecimalPlaces = (int)p.BaseUnit.DecimalPlaces,
                QuantityOnHand = db.StockItems.Where(s => s.ProductId == p.Id && scope.Contains(s.StockLocationId)).Sum(s => s.QuantityOnHand),
                StockValue = db.StockItems.Where(s => s.ProductId == p.Id && scope.Contains(s.StockLocationId)).Sum(s => s.QuantityOnHand * s.AverageCost),
                MaxAverageCost = db.StockItems.Where(s => s.ProductId == p.Id && scope.Contains(s.StockLocationId)).Max(s => (decimal?)s.AverageCost) ?? 0m,
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

        var pageIds = rows.Select(r => r.Id).ToList();
        var balances = await visibility.BalancesAsync(db, pageIds, scope, ct);
        var breakdowns = await BreakdownUnits.LoadAsync(db, pageIds, ct);
        var items = rows.Select(p => new StockOverviewItemDto(
            p.Id, p.Sku, p.Name, p.Category, p.BaseUnitLabel,
            p.QuantityOnHand, StockVisibility.WeightedAverage(p.QuantityOnHand, p.StockValue, p.MaxAverageCost), p.StockValue,
            StockStatusCalculator.Calculate(p.QuantityOnHand, p.ReorderLevel),
            balances.GetValueOrDefault(p.Id),
            p.BaseDecimalPlaces, breakdowns.GetValueOrDefault(p.Id) ?? []
        )).ToList();

        return Ok(new PagedResult<StockOverviewItemDto>(items, total, page, pageSize));
    }

    /// <summary>Which sales drew stock from this batch, and how much. Needs Sales.View as well, since it lists invoices.</summary>
    [HttpGet("batches/{batchId:guid}/consumptions")]
    [Authorize(Policy = "Sales.View")]
    public async Task<ActionResult<IReadOnlyList<BatchConsumptionDto>>> BatchConsumptions(Guid batchId, CancellationToken ct)
    {
        if (!await db.PurchaseBatches.AnyAsync(b => b.Id == batchId, ct)) return NotFound();

        var rows = await db.SalesInvoiceLineBatchConsumptions
            .Where(c => c.PurchaseBatchId == batchId)
            .OrderBy(c => c.SalesInvoiceLine.SalesInvoice.CreatedAt)
            .Select(c => new BatchConsumptionDto(
                c.SalesInvoiceLine.SalesInvoiceId, c.SalesInvoiceLine.SalesInvoice.InvoiceNo, c.SalesInvoiceLine.SalesInvoice.InvoiceDate,
                c.SalesInvoiceLine.SalesInvoice.Status, c.SalesInvoiceLine.Product.Name,
                c.QtyBaseUnits, c.ReturnedQtyBaseUnits, c.UnitCost))
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("{productId:guid}")]
    public async Task<ActionResult<StockDetailDto>> Detail(Guid productId, CancellationToken ct = default, [FromQuery] Guid? stockLocationId = null)
    {
        var product = await db.Products.Include(p => p.BaseUnit).Include(p => p.Tax)
            .SingleOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null) return NotFound();

        var visibility = await StockVisibility.LoadAsync(db, effectivePermissions, "Inventory.View", ct);
        if (visibility.DenyUnlessVisible(this, stockLocationId) is { } denied) return denied;
        var scope = visibility.Scope(stockLocationId);

        // Phase 8: one row per (location, product). Total = sum of this one product's quantity over the scope; per-location rows alongside.
        var locationRows = (await visibility.BalancesAsync(db, [productId], scope, ct)).GetValueOrDefault(productId) ?? [];
        var qty = locationRows.Sum(r => r.Qty);
        var stockValue = locationRows.Sum(r => r.Value);
        var avgCost = StockVisibility.WeightedAverage(qty, stockValue, locationRows.Count == 0 ? 0m : locationRows.Max(r => r.AvgCost));

        var movementRows = await db.StockMovements.Include(m => m.User)
            .Where(m => m.ProductId == productId && scope.Contains(m.StockLocationId))
            .OrderByDescending(m => m.OccurredAt)
            .Select(m => new
            {
                m.Id, m.OccurredAt, m.Type, m.QuantityBaseUnits, m.StockLocationId, m.ReferenceId, m.ReferenceType, m.ReferenceNo,
                UserName = m.User.FirstName + " " + m.User.LastName, m.Notes,
                m.PurchaseBatchId, BatchSequence = m.PurchaseBatch != null ? m.PurchaseBatch.BatchSequence : (long?)null,
                m.ReasonCode, m.EnteredQuantity, m.EnteredProductUnitId, m.EnteredUnitCode, m.ConversionFactorSnapshot,
                EnteredDecimalPlaces = m.EnteredProductUnit != null ? m.EnteredProductUnit.Unit.DecimalPlaces : (int?)null,
            })
            .ToListAsync(ct);
        var movements = movementRows.Select(m => new StockMovementDto(
            m.Id, m.OccurredAt, m.Type, m.QuantityBaseUnits, m.ReferenceType, m.ReferenceNo, m.UserName, m.Notes,
            m.PurchaseBatchId, m.BatchSequence is { } seq ? PurchaseBatch.FormatInternalBatchNo(seq) : null,
            m.ReasonCode, m.EnteredQuantity, m.EnteredProductUnitId, m.EnteredUnitCode, m.ConversionFactorSnapshot, m.EnteredDecimalPlaces, product.BaseUnit.DecimalPlaces,
            m.StockLocationId, visibility.Code(m.StockLocationId), visibility.Name(m.StockLocationId), m.ReferenceId)).ToList();

        // Documented "Branch"-filter bypass (D7): the batch's source reference (PO / adjustment number) is company-internal
        // traceability; a batch bought at another branch must not turn into a null/"Adjustment" label for a restricted user.
        var batchRows = await db.PurchaseBatches.IgnoreQueryFilters([BranchQueryFilter.Name])
            .Where(b => b.ProductId == productId
                && (scope.Contains(b.OriginStockLocationId) || db.BatchLocationBalances.Any(x => x.PurchaseBatchId == b.Id && scope.Contains(x.StockLocationId))))
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new
            {
                b.Id, b.CreatedAt, b.BatchSequence, b.SupplierBatchNumber, b.ReceivedDate,
                b.PurchasedBaseQty, b.RemainingBaseQty, b.UnitCost,
                IsPurchase = b.PurchaseOrderLineId != null,
                RefNo = b.PurchaseOrderLineId != null ? b.PurchaseOrderLine!.PurchaseOrder.PurchaseNo : b.StockAdjustment!.AdjustmentNo,
                PurchaseOrderId = b.PurchaseOrderLineId != null ? b.PurchaseOrderLine!.PurchaseOrderId : (Guid?)null,
                b.PurchaseOrderLineId,
                b.StockAdjustmentId,
            })
            .ToListAsync(ct);

        // Per-batch remainders at the scoped locations (the batch total can span locations once stock moves between them).
        var balanceRows = await db.BatchLocationBalances.AsNoTracking()
            .Where(x => x.ProductId == productId && scope.Contains(x.StockLocationId))
            .Select(x => new { x.PurchaseBatchId, x.StockLocationId, x.RemainingBaseQty })
            .ToListAsync(ct);
        var balancesByBatch = balanceRows.GroupBy(x => x.PurchaseBatchId).ToDictionary(g => g.Key, g => g
            .Select(x => new StockBatchLocationDto(x.StockLocationId, visibility.Code(x.StockLocationId) ?? "", visibility.Name(x.StockLocationId) ?? "", x.RemainingBaseQty))
            .OrderBy(x => x.Code, StringComparer.Ordinal).ToList());

        var batches = batchRows.Select(b => new StockBatchDto(
            b.Id, b.CreatedAt, b.IsPurchase ? "Purchase" : "Adjustment", b.RefNo,
            b.PurchasedBaseQty, balancesByBatch.TryGetValue(b.Id, out var at) ? at.Sum(x => x.Qty) : 0m, b.UnitCost,
            PurchaseBatch.FormatInternalBatchNo(b.BatchSequence), b.SupplierBatchNumber, b.ReceivedDate,
            b.PurchaseOrderId, b.PurchaseOrderLineId, b.StockAdjustmentId,
            balancesByBatch.GetValueOrDefault(b.Id))).ToList();

        var breakdown = (await BreakdownUnits.LoadAsync(db, [productId], ct)).GetValueOrDefault(productId) ?? [];

        return Ok(new StockDetailDto(
            product.Id, product.Sku, product.Name, product.Category, product.BaseUnit.ShortCode, product.Tax.Name,
            qty, avgCost, stockValue, product.ReorderLevel, StockStatusCalculator.Calculate(qty, product.ReorderLevel),
            movements, batches, locationRows, product.BaseUnit.DecimalPlaces, breakdown));
    }
}
