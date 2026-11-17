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
/// FI-D37 transit views over consignments that have been dispatched. Visibility follows FI-D32: a consignment is listed when the caller reads its shipment's
/// source (Shipments.View, source-location visibility; direction "outgoing", ShipmentId present) or reads its destination (Shipments.View /
/// StockTransfers.Receive / StockTransfers.View; direction "incoming", no ShipmentId, never another destination's consignment). The read scope is the active
/// branch, or the whole allowed company under <c>?scope=company</c> for company-level holders.
/// Views: current (InTransit / PartiallyReceived, either side), incoming (InTransit / PartiallyReceived, destination read), outgoing (InTransit / PartiallyReceived,
/// source read), completed (Received), discrepancies (any damaged / rejected / missing, or a Received consignment with posted &lt; dispatched).
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/transit")]
public class TransitController(SelloraDbContext db, IEffectivePermissionService effectivePermissions) : ControllerBase
{
    private static readonly string[] IncomingCodes = ["Shipments.View", "StockTransfers.Receive", "StockTransfers.View"];
    private static readonly string[] Views = ["current", "incoming", "outgoing", "completed", "discrepancies"];

    [HttpGet]
    [CompanyScopeRead(Permission = "Shipments.View")]
    public async Task<ActionResult<PagedResult<TransitItemDto>>> List(
        [FromQuery] string? view, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var which = string.IsNullOrWhiteSpace(view) ? "current" : view.Trim().ToLowerInvariant();
        if (!Views.Contains(which))
        {
            ModelState.AddModelError("view", "View must be current, incoming, outgoing, completed or discrepancies.");
            return ValidationProblem(ModelState);
        }

        var incomingVisibility = await StockVisibility.LoadAnyAsync(db, effectivePermissions, IncomingCodes, ct);
        if (incomingVisibility is null) return Forbid();
        var destinations = incomingVisibility.Ids.ToList();

        var held = await effectivePermissions.GetCurrentAsync(ct);
        var permittedSources = held.PermittedBranches("Shipments.View");
        var sources = permittedSources is not null && permittedSources.Count == 0
            ? []
            : (await StockVisibility.LoadAsync(db, effectivePermissions, "Shipments.View", ct)).ShipmentSourceIds();

        var inFlight = new[] { ConsignmentStatus.InTransit, ConsignmentStatus.PartiallyReceived };
        var flown = new[] { ConsignmentStatus.InTransit, ConsignmentStatus.PartiallyReceived, ConsignmentStatus.Received, ConsignmentStatus.Reconciled };

        // The shipment's source-branch filter is bypassed on purpose (a destination reader is not the source branch); the id lists above are the scope.
        var query = from c in db.Consignments.AsNoTracking()
                    join s in db.Shipments.AsNoTracking().IgnoreQueryFilters([BranchQueryFilter.Name]) on c.ShipmentId equals s.Id
                    where flown.Contains(c.Status) && s.Status != ShipmentStatus.Cancelled
                    select new { c, s };

        query = which switch
        {
            "incoming" => query.Where(x => inFlight.Contains(x.c.Status) && destinations.Contains(x.c.DestinationLocationId)),
            "outgoing" => query.Where(x => inFlight.Contains(x.c.Status) && sources.Contains(x.s.SourceLocationId)),
            "completed" => query.Where(x => x.c.Status == ConsignmentStatus.Reconciled && (destinations.Contains(x.c.DestinationLocationId) || sources.Contains(x.s.SourceLocationId))),
            "discrepancies" => query.Where(x => (destinations.Contains(x.c.DestinationLocationId) || sources.Contains(x.s.SourceLocationId))
                && x.c.Lines.Any(l => l.Status == ConsignmentLineStatus.Active
                    && l.DamagedBaseQty + l.RejectedBaseQty + l.MissingBaseQty - l.DispositionedDamagedBaseQty - l.DispositionedRejectedBaseQty - l.DispositionedMissingBaseQty > 0)),
            _ => query.Where(x => inFlight.Contains(x.c.Status) && (destinations.Contains(x.c.DestinationLocationId) || sources.Contains(x.s.SourceLocationId))),
        };

        var lookups = await ShipmentLookups.LoadAsync(db, ct);
        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            var lower = term.ToLower();
            var matchingLocations = lookups.LocationsMatching(term);
            var matchingProducts = await db.Products.AsNoTracking()
                .Where(p => p.Name.ToLower().Contains(lower) || p.Sku.ToLower().Contains(lower)).Select(p => p.Id).Take(200).ToListAsync(ct);
            query = query.Where(x => x.c.ConsignmentNo.ToLower().Contains(lower) || x.s.ShipmentNo.ToLower().Contains(lower)
                || (x.s.TransportReference != null && x.s.TransportReference.ToLower().Contains(lower))
                || matchingLocations.Contains(x.s.SourceLocationId) || matchingLocations.Contains(x.c.DestinationLocationId)
                || x.c.Lines.Any(l => matchingProducts.Contains(l.ProductId)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.c.DispatchedAt).ThenByDescending(x => x.c.ConsignmentNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.c.Id, x.c.ConsignmentNo, x.c.Status, x.c.DestinationLocationId, x.c.DestinationBranchId, x.c.DispatchedAt,
                ShipmentId = x.s.Id, x.s.ShipmentNo, ShipmentStatus = x.s.Status, x.s.TransportMode, x.s.TransportReference, x.s.SourceLocationId,
            }).ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var lines = await db.ConsignmentLines.AsNoTracking().Where(l => ids.Contains(l.ConsignmentId) && l.Status == ConsignmentLineStatus.Active)
            .OrderBy(l => l.CreatedAt).ToListAsync(ct);
        var linesByConsignment = lines.ToLookup(l => l.ConsignmentId);
        var products = await ShipmentReads.ProductsAsync(db, lines.Select(l => l.ProductId), ct);
        var receipts = (await db.ConsignmentReceipts.AsNoTracking().Where(r => ids.Contains(r.ConsignmentId)).Select(r => new { r.ConsignmentId, r.Status }).ToListAsync(ct))
            .ToLookup(r => r.ConsignmentId);

        var items = rows.Select(r =>
        {
            var consignmentLines = linesByConsignment[r.Id].ToList();
            var outgoing = sources.Contains(r.SourceLocationId);
            var hasDiscrepancy = consignmentLines.Any(l => ShipmentReads.OpenDiscrepancy(l) > 0);
            var own = receipts[r.Id].ToList();
            return new TransitItemDto(
                r.Id, r.ConsignmentNo, r.Status, outgoing ? "outgoing" : "incoming", outgoing ? r.ShipmentId : null, r.ShipmentNo, r.ShipmentStatus,
                r.TransportMode, r.TransportReference, lookups.Location(r.SourceLocationId), lookups.Location(r.DestinationLocationId), lookups.Branch(r.DestinationBranchId),
                r.DispatchedAt, own.Count, own.Count(x => x.Status is ConsignmentReceiptStatus.Verified or ConsignmentReceiptStatus.PendingApproval), hasDiscrepancy,
                consignmentLines.Select(l =>
                {
                    products.TryGetValue(l.ProductId, out var p);
                    var accounted = l.AcceptedBaseQty + l.DamagedBaseQty + l.RejectedBaseQty + l.MissingBaseQty;
                    return new TransitLineDto(l.Id, l.ProductId, p?.Sku, p?.Name, p?.BaseUnitCode, l.UnitCode,
                        l.DispatchedBaseQty, l.AcceptedBaseQty, l.DamagedBaseQty, l.RejectedBaseQty, l.MissingBaseQty, l.PostedBaseQty,
                        l.DispatchedBaseQty - accounted,
                        l.DispatchedBaseQty - l.PostedBaseQty - l.DispositionedDamagedBaseQty - l.DispositionedRejectedBaseQty - l.DispositionedMissingBaseQty,
                        l.DispositionedDamagedBaseQty, l.DispositionedRejectedBaseQty, l.DispositionedMissingBaseQty, ShipmentReads.OpenDiscrepancy(l));
                }).ToList());
        }).ToList();
        return Ok(new PagedResult<TransitItemDto>(items, total, page, pageSize));
    }
}
