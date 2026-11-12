using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>Stock locations (Phase 3), read-only for all types: BranchStore/Transit are written by BranchService/the seeder, warehouses and vehicles by their own services (Phase 4).</summary>
[ApiController]
[Route("api/v1/stock-locations")]
[Authorize(Policy = "StockLocations.View")]
public class StockLocationsController(SelloraDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<StockLocationDto>>> List(
        [FromQuery] string? type, [FromQuery] Guid? branchId, [FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        StockLocationType? typeFilter = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<StockLocationType>(type.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                ModelState.AddModelError("type", "Unknown location type.");
                return ValidationProblem(ModelState);
            }
            typeFilter = parsed;
        }

        var denied = await this.DenyUnlessAllowedAsync(db, branchId, ct);
        if (denied is not null) return denied;

        var companyId = db.CompanyContext.RequireCompanyId();
        var query = db.StockLocations.AsNoTracking().Where(l => l.CompanyId == companyId);
        if (!includeInactive) query = query.Where(l => l.IsActive);
        if (typeFilter is { } t) query = query.Where(l => l.LocationType == t);
        var rows = await query.ToListAsync(ct);

        // A vehicle's location row is company-level (BranchId NULL); its owner is the branch it is assigned to (Phase 8 D3,
        // as StockVisibility). Effective owner = location.BranchId ?? the vehicle's AssignedBranchId; central vehicles and
        // company warehouses have none.
        var vehicleOwner = await db.Vehicles.AsNoTracking()
            .Where(v => v.CompanyId == companyId && v.AssignedBranchId != null)
            .Select(v => new { v.StockLocationId, v.AssignedBranchId })
            .ToDictionaryAsync(v => v.StockLocationId, v => v.AssignedBranchId, ct);
        Guid? OwnerOf(StockLocation l) => l.BranchId ?? vehicleOwner.GetValueOrDefault(l.Id);

        // Branch-owned locations (incl. assigned vehicles) follow branch access (as documents do); company-level locations are
        // visible to every member. The branchId filter matches the effective owner, so a branch's vehicles are listed with it.
        var locations = rows
            .Where(l => branchId is not { } b || OwnerOf(l) == b)
            .Where(l => OwnerOf(l) is not { } owner || db.BranchContext.CanAccess(owner))
            .ToList();
        var labels = await BranchLabels.LoadAsync(db, ct);
        var defaultBranchId = await db.Branches.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.IsDefault).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);

        var ordered = locations
            .OrderByDescending(l => defaultBranchId is not null && l.BranchId == defaultBranchId && l.LocationType == StockLocationType.BranchStore)
            .ThenBy(l => (int)l.LocationType)
            .ThenBy(l => l.Code, StringComparer.Ordinal)
            .Select(l => ToDto(l, labels))
            .ToList();
        return Ok(ordered);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<StockLocationDto>> Get(Guid id, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var location = await db.StockLocations.AsNoTracking().SingleOrDefaultAsync(l => l.CompanyId == companyId && l.Id == id, ct);
        if (location is null) return NotFound();

        // Phase 01 (G5): same visibility as the list - the effective owner is the location's branch or, for a vehicle location, the vehicle's assigned branch.
        var vehicleOwner = await db.Vehicles.AsNoTracking()
            .Where(v => v.CompanyId == companyId && v.StockLocationId == id)
            .Select(v => v.AssignedBranchId).FirstOrDefaultAsync(ct);
        if ((location.BranchId ?? vehicleOwner) is { } owner && !db.BranchContext.CanAccess(owner)) return NotFound();

        return Ok(ToDto(location, await BranchLabels.LoadAsync(db, ct)));
    }

    private static StockLocationDto ToDto(StockLocation l, BranchLabels labels) => new(
        l.Id, l.Code, l.Name, l.LocationType.ToString(), l.BranchId, labels.Code(l.BranchId), labels.Name(l.BranchId), l.IsActive,
        StockLocationTypeRules.CanSell(l.LocationType),
        StockLocationTypeRules.CanReceivePurchase(l.LocationType),
        StockLocationTypeRules.IsSystemManaged(l.LocationType));
}
