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
        if (branchId is { } b) query = query.Where(l => l.BranchId == b);

        // Branch-owned locations follow branch access (as documents do); company-level locations are visible to every member.
        var locations = (await query.ToListAsync(ct))
            .Where(l => l.BranchId is not { } owner || db.BranchContext.CanAccess(owner))
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
        if (location is null || (location.BranchId is { } owner && !db.BranchContext.CanAccess(owner))) return NotFound();

        return Ok(ToDto(location, await BranchLabels.LoadAsync(db, ct)));
    }

    private static StockLocationDto ToDto(StockLocation l, BranchLabels labels) => new(
        l.Id, l.Code, l.Name, l.LocationType.ToString(), l.BranchId, labels.Code(l.BranchId), labels.Name(l.BranchId), l.IsActive,
        StockLocationTypeRules.CanSell(l.LocationType),
        StockLocationTypeRules.CanReceivePurchase(l.LocationType),
        StockLocationTypeRules.IsSystemManaged(l.LocationType));
}
