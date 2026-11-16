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
/// Phase 04 shipments (FI-D29..D33). No class-level policy: each action names its permission (Shipments.View / Plan / Load). Reads are scoped by the
/// "Branch" query filter on Shipments (source branch; company-level sources are visible to every member) and by <see cref="StockVisibility"/> on the
/// source location; a hidden shipment is a 404. Every write answers with the shared detail projection so a fresh post and an idempotent replay
/// return the same shape. Business rules live in <see cref="IShipmentService"/>.
/// </summary>
[ApiController]
[Route("api/v1/shipments")]
public class ShipmentsController(IShipmentService shipments, SelloraDbContext db, IEffectivePermissionService effectivePermissions) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Shipments.View")]
    [CompanyScopeRead]
    public async Task<ActionResult<PagedResult<ShipmentListItemDto>>> List(
        [FromQuery] ShipmentStatus? status, [FromQuery] TransportMode? mode, [FromQuery] Guid? sourceLocationId, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var visibility = await StockVisibility.LoadAsync(db, effectivePermissions, "Shipments.View", ct);
        if (visibility.DenyUnlessVisible(this, sourceLocationId) is { } denied) return denied;
        if (sourceLocationId is { } asked && !visibility.IsShipmentSourceVisible(asked)) return Ok(new PagedResult<ShipmentListItemDto>([], 0, page, pageSize));
        var sources = sourceLocationId is { } one ? [one] : visibility.ShipmentSourceIds();

        var query = db.Shipments.AsNoTracking().Where(s => sources.Contains(s.SourceLocationId));
        if (status is not null) query = query.Where(s => s.Status == status);
        if (mode is not null) query = query.Where(s => s.TransportMode == mode);

        var lookups = await ShipmentLookups.LoadAsync(db, ct);
        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            var lower = term.ToLower();
            var matching = lookups.LocationsMatching(term);
            query = query.Where(s => s.ShipmentNo.ToLower().Contains(lower)
                || (s.ProviderName != null && s.ProviderName.ToLower().Contains(lower))
                || (s.TransportReference != null && s.TransportReference.ToLower().Contains(lower))
                || matching.Contains(s.SourceLocationId)
                || db.Consignments.Any(c => c.ShipmentId == s.Id && c.Status != ConsignmentStatus.Cancelled
                    && (c.ConsignmentNo.ToLower().Contains(lower) || matching.Contains(c.DestinationLocationId))));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.ShipmentNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(s => new
            {
                s.Id, s.ShipmentNo, s.Status, s.TransportMode, s.TransportVia, s.ProviderName, s.TransportReference, s.PlannedDispatchDate,
                s.BranchId, s.SourceLocationId, s.CreatedAt,
                ConsignmentCount = s.Consignments.Count(c => c.Status != ConsignmentStatus.Cancelled),
                LineCount = s.Consignments.Where(c => c.Status != ConsignmentStatus.Cancelled).SelectMany(c => c.Lines).Count(l => l.Status == ConsignmentLineStatus.Active),
                VehicleCount = s.Vehicles.Count,
            }).ToListAsync(ct);

        var items = rows.Select(r => new ShipmentListItemDto(
            r.Id, r.ShipmentNo, r.Status, r.TransportMode, r.TransportVia, r.ProviderName, r.TransportReference, r.PlannedDispatchDate,
            lookups.Branch(r.BranchId), lookups.Location(r.SourceLocationId), r.ConsignmentCount, r.LineCount, r.VehicleCount, r.CreatedAt)).ToList();
        return Ok(new PagedResult<ShipmentListItemDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Shipments.View")]
    public async Task<ActionResult<ShipmentDto>> Get(Guid id, CancellationToken ct)
    {
        var visibility = await StockVisibility.LoadAsync(db, effectivePermissions, "Shipments.View", ct);
        var dto = await ShipmentReads.LoadShipmentAsync(db, id, ct, visibility);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpPost]
    [Authorize(Policy = "Shipments.Plan")]
    public async Task<ActionResult<ShipmentDto>> Create(CreateShipmentRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);

        var input = new CreateShipmentInput(request.SourceLocationId!.Value, request.TransportMode!.Value, request.TransportVia!.Value,
            request.ProviderName, request.ProviderContact, request.TransportReference, request.PlannedDispatchDate, request.Notes, key);

        return await RunAsync<ShipmentDto>(async () =>
        {
            var shipment = await shipments.CreateAsync(input, UserId(), HttpContext.TraceIdentifier, ct);
            var dto = await ShipmentReads.LoadShipmentAsync(db, shipment.Id, ct);
            return dto is null ? NotFound() : CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
        });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Shipments.Plan")]
    public async Task<ActionResult<ShipmentDto>> Update(Guid id, UpdateShipmentRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var input = new UpdateShipmentInput(request.TransportMode!.Value, request.TransportVia!.Value,
            request.ProviderName, request.ProviderContact, request.TransportReference, request.PlannedDispatchDate, request.Notes);
        return await RunAsync(async () => await RespondAsync(await shipments.UpdateAsync(id, input, UserId(), HttpContext.TraceIdentifier, ct), ct));
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = "Shipments.Plan")]
    public async Task<ActionResult<ShipmentDto>> Cancel(Guid id, ShipmentReasonRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);
        return await RunAsync<ShipmentDto>(async () =>
            await RespondAsync(await shipments.CancelAsync(id, request.Reason.Trim(), UserId(), key, HttpContext.TraceIdentifier, ct), ct));
    }

    [HttpPost("{id:guid}/vehicles")]
    [Authorize(Policy = "Shipments.Plan")]
    public async Task<ActionResult<ShipmentDto>> AddVehicle(Guid id, AddShipmentVehicleRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var input = new ShipmentVehicleInput(request.VehicleId, request.ExternalVehicleNumber, request.ExternalDescription, request.DriverName, request.DriverPhone);
        return await RunAsync(async () => await RespondAsync(await shipments.AddVehicleAsync(id, input, UserId(), HttpContext.TraceIdentifier, ct), ct));
    }

    [HttpDelete("{id:guid}/vehicles/{shipmentVehicleId:guid}")]
    [Authorize(Policy = "Shipments.Plan")]
    public Task<ActionResult<ShipmentDto>> RemoveVehicle(Guid id, Guid shipmentVehicleId, CancellationToken ct) =>
        RunAsync(async () => await RespondAsync(await shipments.RemoveVehicleAsync(id, shipmentVehicleId, UserId(), HttpContext.TraceIdentifier, ct), ct));

    [HttpPost("{id:guid}/allocations")]
    [Authorize(Policy = "Shipments.Plan")]
    public async Task<ActionResult<ShipmentDto>> Allocate(Guid id, AllocateShipmentRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);
        var input = new AllocateShipmentInput(request.StockTransferLineId!.Value, request.ProductUnitId!.Value, request.Quantity, key);
        return await RunAsync(async () => await RespondAsync(await shipments.AllocateAsync(id, input, UserId(), HttpContext.TraceIdentifier, ct), ct));
    }

    [HttpPut("{id:guid}/allocations/{consignmentLineId:guid}")]
    [Authorize(Policy = "Shipments.Plan")]
    public async Task<ActionResult<ShipmentDto>> ChangeAllocation(Guid id, Guid consignmentLineId, ChangeAllocationRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);
        return await RunAsync(async () => await RespondAsync(
            await shipments.ChangeAllocationAsync(id, consignmentLineId, new ChangeAllocationInput(request.Quantity, key), UserId(), HttpContext.TraceIdentifier, ct), ct));
    }

    [HttpDelete("{id:guid}/allocations/{consignmentLineId:guid}")]
    [Authorize(Policy = "Shipments.Plan")]
    public Task<ActionResult<ShipmentDto>> CancelAllocation(Guid id, Guid consignmentLineId, CancellationToken ct) =>
        RunAsync(async () => await RespondAsync(await shipments.CancelAllocationAsync(id, consignmentLineId, UserId(), HttpContext.TraceIdentifier, ct), ct));

    [HttpPut("{id:guid}/consignments/{consignmentId:guid}/vehicle")]
    [Authorize(Policy = "Shipments.Plan")]
    public async Task<ActionResult<ShipmentDto>> AssignVehicle(Guid id, Guid consignmentId, AssignConsignmentVehicleRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return await RunAsync(async () => await RespondAsync(
            await shipments.AssignVehicleAsync(id, consignmentId, request.ShipmentVehicleId, UserId(), HttpContext.TraceIdentifier, ct), ct));
    }

    [HttpPost("{id:guid}/consignments/{consignmentId:guid}/cancel")]
    [Authorize(Policy = "Shipments.Plan")]
    public async Task<ActionResult<ShipmentDto>> CancelConsignment(Guid id, Guid consignmentId, ShipmentReasonRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return await RunAsync(async () => await RespondAsync(
            await shipments.CancelConsignmentAsync(id, consignmentId, request.Reason.Trim(), UserId(), HttpContext.TraceIdentifier, ct), ct));
    }

    [HttpPost("{id:guid}/consignments/{consignmentId:guid}/load")]
    [Authorize(Policy = "Shipments.Load")]
    public async Task<ActionResult<ShipmentDto>> Load(Guid id, Guid consignmentId, LoadConsignmentRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);
        var input = new LoadConsignmentInput(request.Lines.Select(l => new LoadConsignmentLineInput(l.ConsignmentLineId!.Value, l.LoadedQuantity)).ToList(), key);
        return await RunAsync(async () => await RespondAsync(await shipments.LoadAsync(id, consignmentId, input, UserId(), HttpContext.TraceIdentifier, ct), ct));
    }

    // ---- helpers ----

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private async Task<ActionResult<ShipmentDto>> RespondAsync(Shipment? shipment, CancellationToken ct)
    {
        if (shipment is null) return NotFound();
        var dto = await ShipmentReads.LoadShipmentAsync(db, shipment.Id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>DomainValidationException → 400 ValidationProblemDetails keyed by the request field.</summary>
    private async Task<ActionResult<T>> RunAsync<T>(Func<Task<ActionResult<T>>> action)
    {
        try
        {
            return await action();
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }
}
