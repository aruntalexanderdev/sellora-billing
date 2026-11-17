using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Api.Validation;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>
/// FI-D32: consignments as their readers see them. Outgoing = consignments of shipments whose source the caller reads (Shipments.View, source branch
/// scope, ?scope=company for company-level holders). Incoming = consignments bound for a destination location the caller reads (Shipments.View,
/// StockTransfers.View or StockTransfers.Receive): only that consignment, with its own leg, never other destinations, other vehicles or costs. The
/// shipment's source-branch filter is bypassed for the incoming projection on purpose and only fixed shipment header fields are selected.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/consignments")]
public class ConsignmentsController(SelloraDbContext db, IEffectivePermissionService effectivePermissions, IConsignmentReceivingService receiving) : ControllerBase
{
    private static readonly string[] IncomingCodes = ["Shipments.View", "StockTransfers.Receive", "StockTransfers.View"];

    [HttpGet]
    [CompanyScopeRead(Permission = "Shipments.View")]
    public async Task<ActionResult<PagedResult<ConsignmentListItemDto>>> List(
        [FromQuery] string? direction, [FromQuery] ConsignmentStatus? status, [FromQuery] Guid? shipmentId,
        [FromQuery] Guid? sourceLocationId, [FromQuery] Guid? destinationLocationId, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var dir = direction?.Trim().ToLowerInvariant();
        if (dir is not ("incoming" or "outgoing"))
        {
            ModelState.AddModelError("direction", "Direction must be 'incoming' or 'outgoing'.");
            return ValidationProblem(ModelState);
        }
        var incoming = dir == "incoming";

        var visibility = await StockVisibility.LoadAnyAsync(db, effectivePermissions, incoming ? IncomingCodes : ["Shipments.View"], ct);
        if (visibility is null) return Forbid();
        if (visibility.DenyUnlessVisible(this, incoming ? destinationLocationId : sourceLocationId) is { } denied) return denied;

        var shipmentsQuery = incoming
            ? db.Shipments.AsNoTracking().IgnoreQueryFilters([BranchQueryFilter.Name])
            : db.Shipments.AsNoTracking();
        var scope = incoming ? visibility.Scope(destinationLocationId)
            : sourceLocationId is { } onlySource ? (visibility.IsShipmentSourceVisible(onlySource) ? [onlySource] : []) : visibility.ShipmentSourceIds();

        var query = from c in db.Consignments.AsNoTracking()
                    join s in shipmentsQuery on c.ShipmentId equals s.Id
                    where c.Status != ConsignmentStatus.Cancelled && s.Status != ShipmentStatus.Cancelled
                    select new { c, s };
        query = incoming
            ? query.Where(x => scope.Contains(x.c.DestinationLocationId))
            : query.Where(x => scope.Contains(x.s.SourceLocationId));

        if (status is not null) query = query.Where(x => x.c.Status == status);
        if (!incoming && shipmentId is not null) query = query.Where(x => x.s.Id == shipmentId);
        if (incoming && sourceLocationId is not null) query = query.Where(x => x.s.SourceLocationId == sourceLocationId);
        if (!incoming && destinationLocationId is not null) query = query.Where(x => x.c.DestinationLocationId == destinationLocationId);

        var lookups = await ShipmentLookups.LoadAsync(db, ct);
        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            var lower = term.ToLower();
            var matching = lookups.LocationsMatching(term);
            query = query.Where(x => x.c.ConsignmentNo.ToLower().Contains(lower) || x.s.ShipmentNo.ToLower().Contains(lower)
                || matching.Contains(x.s.SourceLocationId) || matching.Contains(x.c.DestinationLocationId));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.c.CreatedAt).ThenByDescending(x => x.c.ConsignmentNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.c.Id, x.c.ConsignmentNo, x.c.Status, x.c.DestinationLocationId, x.c.DestinationBranchId, x.c.LoadedAt, x.c.DispatchedAt,
                ShipmentId = x.s.Id, x.s.ShipmentNo, ShipmentStatus = x.s.Status, x.s.TransportMode, x.s.PlannedDispatchDate, x.s.SourceLocationId,
                LineCount = x.c.Lines.Count(l => l.Status == ConsignmentLineStatus.Active),
            }).ToListAsync(ct);

        var items = rows.Select(r => new ConsignmentListItemDto(
            r.Id, r.ConsignmentNo, r.Status, dir!, incoming ? null : r.ShipmentId, r.ShipmentNo, r.ShipmentStatus, r.TransportMode, r.PlannedDispatchDate,
            lookups.Location(r.SourceLocationId), lookups.Location(r.DestinationLocationId), lookups.Branch(r.DestinationBranchId),
            r.LineCount, r.LoadedAt, r.DispatchedAt)).ToList();
        return Ok(new PagedResult<ConsignmentListItemDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ConsignmentDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var raw = await (from c in db.Consignments.AsNoTracking()
                         join s in db.Shipments.AsNoTracking().IgnoreQueryFilters([BranchQueryFilter.Name]) on c.ShipmentId equals s.Id
                         where c.Id == id && c.Status != ConsignmentStatus.Cancelled && s.Status != ShipmentStatus.Cancelled
                         select new { c, s }).SingleOrDefaultAsync(ct);
        if (raw is null) return NotFound();

        var direction = await DirectionAsync(raw.c, raw.s, ct);
        if (direction is null) return NotFound(); // no existence leak

        var lookups = await ShipmentLookups.LoadAsync(db, ct);
        var receiptCount = await db.ConsignmentReceipts.AsNoTracking().CountAsync(r => r.ConsignmentId == id, ct);
        var lines = await db.ConsignmentLines.AsNoTracking().Where(l => l.ConsignmentId == id && l.Status == ConsignmentLineStatus.Active)
            .OrderBy(l => l.CreatedAt).ToListAsync(ct);
        var products = await ShipmentReads.ProductsAsync(db, lines.Select(l => l.ProductId), ct);
        var transferIds = lines.Select(l => l.StockTransferId).Distinct().ToList();
        var transferNos = await db.StockTransfers.AsNoTracking().IgnoreQueryFilters([BranchQueryFilter.Name])
            .Where(t => transferIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.TransferNo, ct);

        // Only this consignment's own leg.
        ConsignmentLegDto? leg = null;
        if (raw.c.ShipmentVehicleId is { } legId)
        {
            var sv = await db.ShipmentVehicles.AsNoTracking().SingleOrDefaultAsync(v => v.Id == legId, ct);
            if (sv is not null)
            {
                var master = sv.VehicleId is { } vid
                    ? await db.Vehicles.AsNoTracking().Where(v => v.Id == vid).Select(v => new { v.VehicleNumber, v.VehicleName }).SingleOrDefaultAsync(ct)
                    : null;
                leg = new ConsignmentLegDto(master?.VehicleNumber ?? sv.ExternalVehicleNumber, master?.VehicleName ?? sv.ExternalDescription, sv.DriverName, sv.DriverPhone);
            }
        }

        var detailLines = lines.Select(l =>
        {
            products.TryGetValue(l.ProductId, out var p);
            return new ConsignmentDetailLineDto(l.Id, transferNos.GetValueOrDefault(l.StockTransferId), l.ProductId, p?.Sku, p?.Name, p?.BaseUnitCode,
                l.ProductUnitId, l.UnitCode, l.EnteredQty, l.AllocatedBaseQty, l.LoadedBaseQty,
                l.ConversionFactorToBase > 0 ? l.LoadedBaseQty / l.ConversionFactorToBase : 0,
                l.DispatchedBaseQty, l.AcceptedBaseQty, l.DamagedBaseQty, l.RejectedBaseQty, l.MissingBaseQty, l.PostedBaseQty, l.ConversionFactorToBase);
        }).ToList();

        return Ok(new ConsignmentDetailDto(
            raw.c.Id, raw.c.ConsignmentNo, raw.c.Status, direction, direction == "outgoing" ? raw.s.Id : null, raw.s.ShipmentNo, raw.s.Status,
            raw.s.TransportMode, raw.s.TransportVia, raw.s.ProviderName, raw.s.TransportReference, raw.s.PlannedDispatchDate,
            lookups.Location(raw.s.SourceLocationId), lookups.Location(raw.c.DestinationLocationId), lookups.Branch(raw.c.DestinationBranchId),
            leg, raw.c.LoadedAt, detailLines, raw.c.DispatchedAt, receiptCount));
    }

    // ---- receipts (FI-D35) ----

    /// <summary>Records a Verified receipt (accepted / damaged / rejected / missing per line, in the line's unit). Moves no stock. Header key: Idempotency-Key.</summary>
    [HttpPost("{id:guid}/receipts")]
    [Authorize(Policy = "StockTransfers.Receive")]
    public async Task<ActionResult<ConsignmentReceiptDto>> RecordReceipt(Guid id, RecordReceiptRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);
        var input = new RecordReceiptInput(
            request.Lines.Select(l => new ReceiptLineInput(l.ConsignmentLineId!.Value, l.AcceptedQuantity, l.DamagedQuantity, l.RejectedQuantity, l.MissingQuantity)).ToList(),
            request.Notes, key);
        try
        {
            var receipt = await receiving.RecordAsync(id, input, UserId(), HttpContext.TraceIdentifier, ct);
            if (receipt is null) return NotFound();
            var dto = (await ShipmentReads.LoadReceiptsAsync(db, id, receipt.Id, ct)).SingleOrDefault();
            return dto is null ? NotFound() : CreatedAtAction(nameof(GetReceipt), new { id, receiptId = dto.Id }, dto);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    /// <summary>Posts a Verified receipt: its accepted quantity moves Transit to the destination, exactly once (a repeat is a no-op). Header key: Idempotency-Key.</summary>
    [HttpPost("{id:guid}/receipts/{receiptId:guid}/post")]
    [Authorize(Policy = "StockTransfers.Receive")]
    public async Task<ActionResult<ConsignmentReceiptDto>> PostReceipt(Guid id, Guid receiptId, CancellationToken ct)
    {
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);
        try
        {
            var receipt = await receiving.PostAsync(id, receiptId, new PostReceiptInput(key), UserId(), HttpContext.TraceIdentifier, ct);
            if (receipt is null) return NotFound();
            var dto = (await ShipmentReads.LoadReceiptsAsync(db, id, receipt.Id, ct)).SingleOrDefault();
            return dto is null ? NotFound() : Ok(dto);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    /// <summary>Receipts of a consignment, oldest first. Readable by whoever may read the consignment (source or destination side).</summary>
    [HttpGet("{id:guid}/receipts")]
    public async Task<ActionResult<IReadOnlyList<ConsignmentReceiptDto>>> ListReceipts(Guid id, CancellationToken ct)
    {
        if (!await CanReadAsync(id, ct)) return NotFound();
        return Ok(await ShipmentReads.LoadReceiptsAsync(db, id, null, ct));
    }

    [HttpGet("{id:guid}/receipts/{receiptId:guid}")]
    public async Task<ActionResult<ConsignmentReceiptDto>> GetReceipt(Guid id, Guid receiptId, CancellationToken ct)
    {
        if (!await CanReadAsync(id, ct)) return NotFound();
        var dto = (await ShipmentReads.LoadReceiptsAsync(db, id, receiptId, ct)).SingleOrDefault();
        return dto is null ? NotFound() : Ok(dto);
    }

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private async Task<bool> CanReadAsync(Guid consignmentId, CancellationToken ct)
    {
        var raw = await (from c in db.Consignments.AsNoTracking()
                         join s in db.Shipments.AsNoTracking().IgnoreQueryFilters([BranchQueryFilter.Name]) on c.ShipmentId equals s.Id
                         where c.Id == consignmentId && c.Status != ConsignmentStatus.Cancelled && s.Status != ShipmentStatus.Cancelled
                         select new { c, s }).SingleOrDefaultAsync(ct);
        return raw is not null && await DirectionAsync(raw.c, raw.s, ct) is not null;
    }

    // Outgoing first (the richer view): needs Shipments.View, the shipment inside the Branch filter and its source visible; else incoming (destination visible).
    private async Task<string?> DirectionAsync(Consignment c, Shipment s, CancellationToken ct)
    {
        if (await PickCodeAsync(["Shipments.View"], ct) is { } outCode)
        {
            var visible = await db.Shipments.AsNoTracking().AnyAsync(x => x.Id == s.Id, ct)
                && (await StockVisibility.LoadAsync(db, effectivePermissions, outCode, ct)).IsShipmentSourceVisible(s.SourceLocationId);
            if (visible) return "outgoing";
        }
        if (await StockVisibility.LoadAnyAsync(db, effectivePermissions, IncomingCodes, ct) is { } inVisibility && inVisibility.IsVisible(c.DestinationLocationId))
        {
            return "incoming";
        }
        return null;
    }

    // The first of the codes the caller holds at some branch (company-level or branch role), else null.
    private async Task<string?> PickCodeAsync(IEnumerable<string> codes, CancellationToken ct)
    {
        var set = await effectivePermissions.GetCurrentAsync(ct);
        foreach (var code in codes)
        {
            var permitted = set.PermittedBranches(code);
            if (permitted is null || permitted.Count > 0) return code;
        }
        return null;
    }
}
