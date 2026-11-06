using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Validation;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

// No class-level policy: policies combine with AND, and reading adjustments needs only Inventory.View.
[ApiController]
[Route("api/v1/stock-adjustments")]
public class StockAdjustmentsController(IStockAdjustmentService adjustmentService, SelloraDbContext db) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Inventory.View")]
    public async Task<ActionResult<PagedResult<StockAdjustmentListItemDto>>> List(
        [FromQuery] string? search, [FromQuery] Guid? productId,
        [FromQuery] AdjustmentType? type, [FromQuery] AdjustmentDirection? direction,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.StockAdjustments.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(a => a.AdjustmentNo.ToLower().Contains(s) || a.Product.Name.ToLower().Contains(s) || a.Product.Sku.ToLower().Contains(s));
        }
        if (productId is not null) query = query.Where(a => a.ProductId == productId);
        if (type is not null) query = query.Where(a => a.Type == type);
        if (direction is not null) query = query.Where(a => a.Direction == direction);
        if (dateFrom is not null)
        {
            var from = new DateTimeOffset(dateFrom.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(a => a.CreatedAt >= from);
        }
        if (dateTo is not null)
        {
            var to = new DateTimeOffset(dateTo.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(a => a.CreatedAt < to);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.AdjustmentNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new StockAdjustmentListItemDto(
                a.Id, a.AdjustmentNo, a.CreatedAt, a.ProductId, a.Product.Name, a.Product.Sku,
                a.Type, a.Direction, a.EnteredQuantity, a.UnitCode, a.Unit.DecimalPlaces,
                a.QuantityBaseUnits, a.Product.BaseUnit.ShortCode, a.Product.BaseUnit.DecimalPlaces, a.EnteredUnitCost, a.UnitCost,
                a.BeforeQty, a.AfterQty, a.User.FirstName + " " + a.User.LastName))
            .ToListAsync(ct);

        return Ok(new PagedResult<StockAdjustmentListItemDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Inventory.View")]
    public async Task<ActionResult<StockAdjustmentDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await LoadDtoAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpPost]
    [Authorize(Policy = "Inventory.Adjust")]
    public async Task<ActionResult<StockAdjustmentDto>> Create(CreateStockAdjustmentRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);

        var input = new CreateAdjustmentInput(
            request.ProductId!.Value, request.ProductUnitId!.Value, request.Type, request.Direction,
            request.Quantity, request.UnitCost, string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(), key);

        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
            var adjustment = await adjustmentService.CreateAdjustmentAsync(input, userId, ct);

            // Re-read through the shared projection, so a fresh post and an idempotent replay return the same shape.
            return Ok(await LoadDtoAsync(adjustment.Id, ct));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    /// <summary>The adjustment detail: posting-time snapshots for the entered side, live decimal places and display
    /// name (decimal places are frozen once a unit is in use), and the batches it created or drew from (its movements).</summary>
    private async Task<StockAdjustmentDto?> LoadDtoAsync(Guid id, CancellationToken ct)
    {
        var a = await db.StockAdjustments.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id, x.AdjustmentNo, x.CreatedAt, x.ProductId, ProductName = x.Product.Name, x.Product.Sku,
                x.Type, x.Direction, x.EnteredQuantity, x.ProductUnitId, x.UnitCode,
                UnitDisplayName = x.ProductUnit.Label ?? x.ProductUnit.Unit.Name,
                x.ConversionFactorToBase, EnteredDecimalPlaces = x.Unit.DecimalPlaces, x.QuantityBaseUnits,
                BaseUnitCode = x.Product.BaseUnit.ShortCode, BaseDecimalPlaces = x.Product.BaseUnit.DecimalPlaces,
                x.EnteredUnitCost, x.UnitCost, x.Reason, x.BeforeQty, x.AfterQty,
                UserName = x.User.FirstName + " " + x.User.LastName,
            })
            .SingleOrDefaultAsync(ct);
        if (a is null) return null;

        var batchRows = await db.StockMovements.AsNoTracking()
            .Where(m => m.ReferenceType == "StockAdjustment" && m.ReferenceId == id && m.PurchaseBatchId != null)
            .OrderBy(m => m.PurchaseBatch!.CreatedAt).ThenBy(m => m.PurchaseBatch!.BatchSequence)
            .Select(m => new { PurchaseBatchId = m.PurchaseBatchId!.Value, m.PurchaseBatch!.BatchSequence, m.QuantityBaseUnits, m.PurchaseBatch.UnitCost })
            .ToListAsync(ct);
        var batches = batchRows
            .Select(b => new StockAdjustmentBatchDto(b.PurchaseBatchId, PurchaseBatch.FormatInternalBatchNo(b.BatchSequence), b.QuantityBaseUnits, b.UnitCost))
            .ToList();

        return new StockAdjustmentDto(
            a.Id, a.AdjustmentNo, a.CreatedAt, a.ProductId, a.ProductName, a.Sku,
            a.Type, a.Direction, a.EnteredQuantity, a.ProductUnitId, a.UnitCode, a.UnitDisplayName,
            a.ConversionFactorToBase, a.EnteredDecimalPlaces, a.QuantityBaseUnits, a.BaseUnitCode, a.BaseDecimalPlaces,
            a.EnteredUnitCost, a.UnitCost, a.Reason, a.BeforeQty, a.AfterQty, a.UserName, batches);
    }
}
