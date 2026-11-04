using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/units")]
[Authorize(Policy = "Masters.View")]
public class UnitsController(SelloraDbContext db, IUnitService unitService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UnitDto>>> List(CancellationToken ct)
    {
        var units = await db.Units.OrderBy(u => u.Name)
            .Select(u => new UnitDto(u.Id, u.Name, u.ShortCode, u.MeasurementCategory, u.DecimalPlaces, u.StandardFactor,
                u.IsActive, db.Products.Any(p => p.BaseUnitId == u.Id)))
            .ToListAsync(ct);
        return Ok(units);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UnitDto>> Get(Guid id, CancellationToken ct)
    {
        var unit = await db.Units.Where(u => u.Id == id)
            .Select(u => new UnitDto(u.Id, u.Name, u.ShortCode, u.MeasurementCategory, u.DecimalPlaces, u.StandardFactor,
                u.IsActive, db.Products.Any(p => p.BaseUnitId == u.Id)))
            .SingleOrDefaultAsync(ct);
        return unit is null ? NotFound() : Ok(unit);
    }

    [HttpPost]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<UnitDto>> Create(UpsertUnitRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var unit = await unitService.CreateUnitAsync(ToInput(request), ct);
            return Ok(ToDto(unit, isInUse: false));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<UnitDto>> Update(Guid id, UpsertUnitRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var unit = await unitService.UpdateUnitAsync(id, ToInput(request), ct);
            if (unit is null) return NotFound();
            return Ok(ToDto(unit, await unitService.IsInUseAsync(unit.Id, ct)));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        try
        {
            var unit = await unitService.SetActiveAsync(id, request.IsActive, ct);
            return unit is null ? NotFound() : NoContent();
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    private static UpsertUnitInput ToInput(UpsertUnitRequest request) => new(
        request.Name, request.ShortCode, request.MeasurementCategory!.Value, request.DecimalPlaces, request.StandardFactor);

    private static UnitDto ToDto(Unit u, bool isInUse) => new(
        u.Id, u.Name, u.ShortCode, u.MeasurementCategory, u.DecimalPlaces, u.StandardFactor, u.IsActive, isInUse);
}
