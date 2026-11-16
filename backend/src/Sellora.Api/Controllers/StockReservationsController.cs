using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>
/// FI-D5/D30: the reservation report (oldest first, source-scoped through the shipment's Branch filter and the source location's visibility) and the
/// dedicated, company-only release. Releasing also cancels the consignment line and reduces the request's AllocatedBaseQty.
/// </summary>
[ApiController]
[Route("api/v1/stock-reservations")]
public class StockReservationsController(IShipmentService shipments, SelloraDbContext db, IEffectivePermissionService effectivePermissions) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Shipments.View")]
    [CompanyScopeRead]
    public async Task<ActionResult<PagedResult<StockReservationDto>>> List(
        [FromQuery] Guid? locationId, [FromQuery] Guid? productId, [FromQuery] StockReservationStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var visibility = await StockVisibility.LoadAsync(db, effectivePermissions, "Shipments.View", ct);
        if (visibility.DenyUnlessVisible(this, locationId) is { } denied) return denied;
        var scope = visibility.Scope(locationId);
        var wanted = status ?? StockReservationStatus.Active; // the report of live reservations unless another status is asked for

        var query = from r in db.StockReservations.AsNoTracking()
                    join l in db.ConsignmentLines.AsNoTracking() on r.ConsignmentLineId equals l.Id
                    join c in db.Consignments.AsNoTracking() on l.ConsignmentId equals c.Id
                    join s in db.Shipments.AsNoTracking() on c.ShipmentId equals s.Id // Branch filter: source scope
                    where r.Status == wanted && scope.Contains(r.SourceLocationId)
                    select new { r, l, c, s };
        if (productId is not null) query = query.Where(x => x.r.ProductId == productId);

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.r.CreatedAt).ThenBy(x => x.r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.r, x.l.StockTransferId, ShipmentId = x.s.Id, x.s.ShipmentNo, ConsignmentId = x.c.Id, x.c.ConsignmentNo, x.c.DestinationLocationId,
            }).ToListAsync(ct);

        var items = await MapAsync(rows.Select(r => new Row(r.r, r.StockTransferId, r.ShipmentId, r.ShipmentNo, r.ConsignmentId, r.ConsignmentNo, r.DestinationLocationId)).ToList(), ct);
        return Ok(new PagedResult<StockReservationDto>(items, total, page, pageSize));
    }

    [HttpPost("{id:guid}/release")]
    [Authorize(Policy = "Reservations.Release")]
    public async Task<ActionResult<StockReservationDto>> Release(Guid id, ReleaseStockReservationRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var released = await shipments.ReleaseReservationAsync(id, request.Reason.Trim(), UserId(), HttpContext.TraceIdentifier, ct);
            if (released is null) return NotFound();

            var row = await (from r in db.StockReservations.AsNoTracking()
                             join l in db.ConsignmentLines.AsNoTracking() on r.ConsignmentLineId equals l.Id
                             join c in db.Consignments.AsNoTracking() on l.ConsignmentId equals c.Id
                             join s in db.Shipments.AsNoTracking() on c.ShipmentId equals s.Id
                             where r.Id == id
                             select new Row(r, l.StockTransferId, s.Id, s.ShipmentNo, c.Id, c.ConsignmentNo, c.DestinationLocationId)).SingleOrDefaultAsync(ct);
            return row is null ? NotFound() : Ok((await MapAsync([row], ct))[0]);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    private sealed record Row(StockReservation R, Guid StockTransferId, Guid ShipmentId, string ShipmentNo, Guid ConsignmentId, string ConsignmentNo, Guid DestinationLocationId);

    private async Task<List<StockReservationDto>> MapAsync(List<Row> rows, CancellationToken ct)
    {
        var lookups = await ShipmentLookups.LoadAsync(db, ct);
        await lookups.LoadUsersAsync(db, rows.SelectMany(x => new Guid?[] { x.R.CreatedByUserId, x.R.ReleasedByUserId }), ct);
        var products = await ShipmentReads.ProductsAsync(db, rows.Select(x => x.R.ProductId), ct);
        var transferIds = rows.Select(x => x.StockTransferId).Distinct().ToList();
        var transferNos = await db.StockTransfers.AsNoTracking().IgnoreQueryFilters([BranchQueryFilter.Name])
            .Where(t => transferIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.TransferNo, ct);
        var now = DateTimeOffset.UtcNow;

        return rows.Select(x =>
        {
            products.TryGetValue(x.R.ProductId, out var p);
            return new StockReservationDto(
                x.R.Id, x.R.Status, lookups.Location(x.R.SourceLocationId), x.R.ProductId, p?.Sku, p?.Name, p?.BaseUnitCode, x.R.BaseQty,
                x.ShipmentId, x.ShipmentNo, x.ConsignmentId, x.ConsignmentNo, x.R.ConsignmentLineId,
                lookups.Location(x.DestinationLocationId), transferNos.GetValueOrDefault(x.StockTransferId),
                x.R.CreatedAt, lookups.User(x.R.CreatedByUserId), Math.Max(0, (int)(now - x.R.CreatedAt).TotalDays),
                x.R.ReleasedAt, lookups.User(x.R.ReleasedByUserId), x.R.ReleaseReason, x.R.ConsumedAt);
        }).ToList();
    }

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
}
