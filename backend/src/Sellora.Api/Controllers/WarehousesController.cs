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

/// <summary>Warehouses (Phase 4). Company warehouses are visible to every member with View; branch warehouses follow branch access
/// (inaccessible ones answer 404). DTOs join Warehouses with their StockLocation explicitly (no navigations).</summary>
[ApiController]
[Route("api/v1/warehouses")]
public class WarehousesController(SelloraDbContext db, IWarehouseService warehouseService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Company.Warehouses.View")]
    public async Task<ActionResult<IReadOnlyList<WarehouseDto>>> List(
        [FromQuery] Guid? branchId, [FromQuery] string? ownership, [FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        StockLocationType? typeFilter = null;
        if (!string.IsNullOrWhiteSpace(ownership))
        {
            typeFilter = ownership.Trim().ToLowerInvariant() switch
            {
                "company" => StockLocationType.CompanyWarehouse,
                "branch" => StockLocationType.BranchWarehouse,
                _ => null,
            };
            if (typeFilter is null)
            {
                ModelState.AddModelError("ownership", "Ownership must be Company or Branch.");
                return ValidationProblem(ModelState);
            }
        }
        var denied = await this.DenyUnlessAllowedAsync(db, branchId, ct);
        if (denied is not null) return denied;

        var companyId = db.CompanyContext.RequireCompanyId();
        var query = Joined(companyId, includeInactive: includeInactive, type: typeFilter, branchId: branchId);

        var rows = (await query.ToListAsync(ct)).Where(x => Visible(x.Location)).ToList();
        var labels = await BranchLabels.LoadAsync(db, ct);
        return Ok(rows
            .Select(x => ToDto(x.Warehouse, x.Location, labels))
            .OrderBy(d => d.BranchId is null ? 0 : 1)
            .ThenBy(d => d.BranchCode, StringComparer.Ordinal)
            .ThenBy(d => d.Code, StringComparer.Ordinal)
            .ToList());
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Company.Warehouses.View")]
    public async Task<ActionResult<WarehouseDto>> Get(Guid id, CancellationToken ct) =>
        await LoadDtoAsync(id, ct) is { } dto ? Ok(dto) : NotFound();

    [HttpPost]
    [Authorize(Policy = "Company.Warehouses.Create")]
    public async Task<ActionResult<WarehouseDto>> Create(CreateWarehouseRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var denied = this.DenyIfInaccessible(db, request.BranchId);
        if (denied is not null) return denied;

        try
        {
            var warehouse = await warehouseService.CreateAsync(new CreateWarehouseInput(request.Code, request.Name, request.BranchId), CurrentUserId(), ct);
            var dto = await LoadDtoAsync(warehouse.Id, ct);
            return CreatedAtAction(nameof(Get), new { id = warehouse.Id }, dto);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Company.Warehouses.Edit")]
    public async Task<ActionResult<WarehouseDto>> Update(Guid id, UpdateWarehouseRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!await IsVisibleAsync(id, ct)) return NotFound();

        try
        {
            var warehouse = await warehouseService.UpdateAsync(id, new UpdateWarehouseInput(request.Name), CurrentUserId(), ct);
            return warehouse is null ? NotFound() : Ok(await LoadDtoAsync(id, ct));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Company.Warehouses.Disable")]
    public async Task<ActionResult<WarehouseDto>> SetStatus(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        if (!await IsVisibleAsync(id, ct)) return NotFound();

        try
        {
            var warehouse = await warehouseService.SetStatusAsync(id, request.IsActive, CurrentUserId(), ct);
            return warehouse is null ? NotFound() : Ok(await LoadDtoAsync(id, ct));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    private record Row(Warehouse Warehouse, StockLocation Location);

    /// <summary>
    /// Warehouse + its location. Filters are applied to the entity roots BEFORE the projection: EF cannot translate a
    /// Where over the projected <see cref="Row"/> record (every filter must go through these parameters).
    /// </summary>
    private IQueryable<Row> Joined(Guid companyId, Guid? id = null, bool includeInactive = true,
        StockLocationType? type = null, Guid? branchId = null)
    {
        var warehouses = db.Warehouses.AsNoTracking().Where(w => w.CompanyId == companyId);
        var locations = db.StockLocations.AsNoTracking().Where(l => l.CompanyId == companyId);
        if (id is { } wid) warehouses = warehouses.Where(w => w.Id == wid);
        if (type is { } t) warehouses = warehouses.Where(w => w.LocationType == t);
        if (!includeInactive) locations = locations.Where(l => l.IsActive);
        if (branchId is { } b) locations = locations.Where(l => l.BranchId == b);

        return from w in warehouses
               join l in locations on w.StockLocationId equals l.Id
               select new Row(w, l);
    }

    private bool Visible(StockLocation l) => l.BranchId is not { } owner || db.BranchContext.CanAccess(owner);

    private async Task<bool> IsVisibleAsync(Guid id, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var row = await Joined(companyId, id: id).SingleOrDefaultAsync(ct);
        return row is not null && Visible(row.Location);
    }

    private async Task<WarehouseDto?> LoadDtoAsync(Guid id, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var row = await Joined(companyId, id: id).SingleOrDefaultAsync(ct);
        if (row is null || !Visible(row.Location)) return null;

        return ToDto(row.Warehouse, row.Location, await BranchLabels.LoadAsync(db, ct));
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private static WarehouseDto ToDto(Warehouse w, StockLocation l, BranchLabels labels) => new(
        w.Id, l.Id, l.Code, l.Name, w.LocationType == StockLocationType.CompanyWarehouse ? "Company" : "Branch",
        l.BranchId, labels.Code(l.BranchId), labels.Name(l.BranchId), l.IsActive, w.CreatedAt);
}
