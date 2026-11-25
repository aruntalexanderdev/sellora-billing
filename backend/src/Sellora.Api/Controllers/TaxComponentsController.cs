using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>Tax components (BR1): CGST, SGST, IGST, Cess, VAT. Reads need <c>Tax.View</c>, writes <c>Tax.Manage</c>.</summary>
[ApiController]
[Route("api/v1/tax-components")]
[Authorize(Policy = "Tax.View")]
public class TaxComponentsController(SelloraDbContext db, ITaxComponentService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TaxComponentDto>>> List([FromQuery] Guid? countryId, [FromQuery] bool? active, CancellationToken ct)
    {
        var query = db.TaxComponents.AsNoTracking().AsQueryable();
        if (countryId is not null) query = query.Where(c => c.CountryId == countryId);
        if (active is not null) query = query.Where(c => c.IsActive == active);

        var rows = await query.OrderBy(c => c.CountryId).ThenBy(c => c.Code).ToListAsync(ct);
        var countries = await db.Countries.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var used = (await db.TaxProfileVersionComponents.AsNoTracking().Select(c => c.TaxComponentId).Distinct().ToListAsync(ct)).ToHashSet();
        return Ok(rows.OrderBy(c => countries.GetValueOrDefault(c.CountryId)).ThenBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => ToDto(c, countries.GetValueOrDefault(c.CountryId, string.Empty), used.Contains(c.Id))).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TaxComponentDto>> Get(Guid id, CancellationToken ct)
    {
        var component = await db.TaxComponents.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        return component is null ? NotFound() : Ok(await ToDtoAsync(component, ct));
    }

    [HttpPost]
    [Authorize(Policy = "Tax.Manage")]
    public async Task<ActionResult<TaxComponentDto>> Create(CreateTaxComponentRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var component = await service.CreateAsync(new UpsertTaxComponentInput(request.CountryId!.Value, request.Code, request.Name, request.Kind!.Value, request.Method!.Value), ct);
            return CreatedAtAction(nameof(Get), new { id = component.Id }, await ToDtoAsync(component, ct));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Tax.Manage")]
    public async Task<ActionResult<TaxComponentDto>> Update(Guid id, UpdateTaxComponentRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var component = await service.UpdateAsync(id, new UpdateTaxComponentInput(request.Code, request.Name, request.Kind!.Value, request.Method!.Value), ct);
            return component is null ? NotFound() : Ok(await ToDtoAsync(component, ct));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Tax.Manage")]
    public async Task<ActionResult<TaxComponentDto>> SetActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        var component = await service.SetActiveAsync(id, request.IsActive, ct);
        return component is null ? NotFound() : Ok(await ToDtoAsync(component, ct));
    }

    private async Task<TaxComponentDto> ToDtoAsync(TaxComponent c, CancellationToken ct)
    {
        var code = await db.Countries.AsNoTracking().Where(x => x.Id == c.CountryId).Select(x => x.Code).SingleOrDefaultAsync(ct) ?? string.Empty;
        return ToDto(c, code, await db.TaxProfileVersionComponents.AnyAsync(v => v.TaxComponentId == c.Id, ct));
    }

    private static TaxComponentDto ToDto(TaxComponent c, string countryCode, bool used) =>
        new(c.Id, c.CountryId, countryCode, c.Code, c.Name, c.Kind, c.Method, c.IsActive, used);

    private ActionResult ToValidationProblem(DomainValidationException ex)
    {
        foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
        return ValidationProblem(ModelState);
    }
}
