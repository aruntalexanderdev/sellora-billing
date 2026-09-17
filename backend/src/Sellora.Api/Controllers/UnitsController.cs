using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/units")]
[Authorize(Policy = "Masters.View")]
public class UnitsController(SelloraDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UnitDto>>> List(CancellationToken ct)
    {
        var units = await db.Units.OrderBy(u => u.Name)
            .Select(u => new UnitDto(u.Id, u.Name, u.ShortCode, u.Type, u.IsActive))
            .ToListAsync(ct);
        return Ok(units);
    }

    [HttpPost]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<UnitDto>> Create(UpsertUnitRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        if (await db.Units.AnyAsync(u => u.Name.ToLower() == request.Name.Trim().ToLower() || u.ShortCode.ToLower() == request.ShortCode.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.ShortCode), "A unit with this name or short code already exists.");
            return ValidationProblem(ModelState);
        }

        var unit = new Unit { Id = Guid.NewGuid(), Name = request.Name.Trim(), ShortCode = request.ShortCode.Trim(), Type = request.Type };
        db.Units.Add(unit);
        await db.SaveChangesAsync(ct);

        return Ok(new UnitDto(unit.Id, unit.Name, unit.ShortCode, unit.Type, unit.IsActive));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<UnitDto>> Update(Guid id, UpsertUnitRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var unit = await db.Units.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (unit is null) return NotFound();

        if (await db.Units.AnyAsync(u => u.Id != id && (u.Name.ToLower() == request.Name.Trim().ToLower() || u.ShortCode.ToLower() == request.ShortCode.Trim().ToLower()), ct))
        {
            ModelState.AddModelError(nameof(request.ShortCode), "A unit with this name or short code already exists.");
            return ValidationProblem(ModelState);
        }

        unit.Name = request.Name.Trim();
        unit.ShortCode = request.ShortCode.Trim();
        unit.Type = request.Type;
        await db.SaveChangesAsync(ct);

        return Ok(new UnitDto(unit.Id, unit.Name, unit.ShortCode, unit.Type, unit.IsActive));
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        var unit = await db.Units.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (unit is null) return NotFound();

        unit.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
