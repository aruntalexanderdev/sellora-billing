using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>Vehicles (Phase 4). Vehicles are company-level: visible to every member with View, except vehicles assigned to a branch the caller cannot access (Phase 01). Assigning to a branch needs access to
/// that branch (403 otherwise); the vehicle's stock location never gets a branch.</summary>
[ApiController]
[Route("api/v1/vehicles")]
public class VehiclesController(SelloraDbContext db, IVehicleService vehicleService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Company.Vehicles.View")]
    public async Task<ActionResult<IReadOnlyList<VehicleDto>>> List(
        [FromQuery] Guid? assignedBranchId, [FromQuery] bool unassigned = false, [FromQuery] string? vehicleType = null,
        [FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        VehicleType? typeFilter = null;
        if (!string.IsNullOrWhiteSpace(vehicleType))
        {
            if (!Enum.TryParse<VehicleType>(vehicleType.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                ModelState.AddModelError("vehicleType", "Unknown vehicle type.");
                return ValidationProblem(ModelState);
            }
            typeFilter = parsed;
        }
        var denied = await this.DenyUnlessAllowedAsync(db, assignedBranchId, ct);
        if (denied is not null) return denied;

        var companyId = db.CompanyContext.RequireCompanyId();
        var query = db.Vehicles.AsNoTracking().Where(v => v.CompanyId == companyId);
        if (!includeInactive) query = query.Where(v => v.IsActive);
        if (typeFilter is { } t) query = query.Where(v => v.VehicleType == t);
        if (unassigned) query = query.Where(v => v.AssignedBranchId == null);
        else if (assignedBranchId is { } b) query = query.Where(v => v.AssignedBranchId == b);

        // Phase 01 (G5): vehicles assigned to a branch outside the caller's branches are hidden; central (unassigned) ones stay visible.
        var vehicles = (await query.OrderBy(v => v.VehicleNumber).ToListAsync(ct))
            .Where(v => v.AssignedBranchId is not { } owner || db.BranchContext.CanAccess(owner)).ToList();
        var labels = await BranchLabels.LoadAsync(db, ct);
        return Ok(vehicles.Select(v => ToDto(v, labels)).ToList());
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Company.Vehicles.View")]
    public async Task<ActionResult<VehicleDto>> Get(Guid id, CancellationToken ct) =>
        await LoadDtoAsync(id, ct) is { } dto ? Ok(dto) : NotFound();

    [HttpPost]
    [Authorize(Policy = "Company.Vehicles.Create")]
    public async Task<ActionResult<VehicleDto>> Create(CreateVehicleRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!TryParseType(request.VehicleType, out var type)) return ValidationProblem(ModelState);
        var denied = this.DenyIfInaccessible(db, request.AssignedBranchId);
        if (denied is not null) return denied;

        try
        {
            var vehicle = await vehicleService.CreateAsync(
                new CreateVehicleInput(request.VehicleNumber, request.VehicleName, type, request.AssignedBranchId), CurrentUserId(), ct);
            return CreatedAtAction(nameof(Get), new { id = vehicle.Id }, await LoadDtoAsync(vehicle.Id, ct));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Company.Vehicles.Edit")]
    public async Task<ActionResult<VehicleDto>> Update(Guid id, UpdateVehicleRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!TryParseType(request.VehicleType, out var type)) return ValidationProblem(ModelState);

        var companyId = db.CompanyContext.RequireCompanyId();
        var current = await db.Vehicles.AsNoTracking().Where(v => v.CompanyId == companyId && v.Id == id)
            .Select(v => new { v.AssignedBranchId }).SingleOrDefaultAsync(ct);
        if (current is null) return NotFound();
        if (current.AssignedBranchId != request.AssignedBranchId)
        {
            // Reassignment needs access to both the current and the new branch (moving a vehicle away from a branch
            // the caller cannot access is refused too). Renaming without reassignment stays company-level.
            var denied = this.DenyIfInaccessible(db, current.AssignedBranchId) ?? this.DenyIfInaccessible(db, request.AssignedBranchId);
            if (denied is not null) return denied;
        }

        try
        {
            var vehicle = await vehicleService.UpdateAsync(id, new UpdateVehicleInput(request.VehicleName, type, request.AssignedBranchId), CurrentUserId(), ct);
            return vehicle is null ? NotFound() : Ok(await LoadDtoAsync(id, ct));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Company.Vehicles.Disable")]
    public async Task<ActionResult<VehicleDto>> SetStatus(Guid id, SetStatusRequest request, CancellationToken ct)
    {
        try
        {
            var vehicle = await vehicleService.SetStatusAsync(id, request.IsActive, request.Reason, CurrentUserId(), ct);
            return vehicle is null ? NotFound() : Ok(await LoadDtoAsync(id, ct));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    private bool TryParseType(string value, out VehicleType type)
    {
        if (Enum.TryParse(value.Trim(), ignoreCase: true, out type) && Enum.IsDefined(type)) return true;
        ModelState.AddModelError("VehicleType", "Unknown vehicle type.");
        return false;
    }

    private async Task<VehicleDto?> LoadDtoAsync(Guid id, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(v => v.CompanyId == companyId && v.Id == id, ct);
        return vehicle is null || (vehicle.AssignedBranchId is { } owner && !db.BranchContext.CanAccess(owner)) ? null : ToDto(vehicle, await BranchLabels.LoadAsync(db, ct));
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private static VehicleDto ToDto(Vehicle v, BranchLabels labels) => new(
        v.Id, v.StockLocationId, v.VehicleNumber, v.VehicleName, v.VehicleType.ToString(),
        v.AssignedBranchId, labels.Code(v.AssignedBranchId), labels.Name(v.AssignedBranchId), v.IsActive, v.CreatedAt);
}
