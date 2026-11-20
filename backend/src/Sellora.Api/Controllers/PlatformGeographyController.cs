using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>Platform-console Countries/States (country-state-masters D8/D10): the same services as the company endpoints with a platform actor (guard never
/// applies, audited with CompanyId NULL), plus the active lookups the company-creation form needs (a platform session has no company context).
/// Lists are plain arrays: the platform console is always writable.</summary>
[ApiController]
[Route("api/v1/platform")]
[PlatformEndpoint]
[Authorize(Policy = PlatformPolicies.Admin)]
public class PlatformGeographyController(SelloraDbContext db, ICountryService countryService, IStateService stateService) : ControllerBase
{
    [HttpGet("countries/all")]
    public async Task<ActionResult<IReadOnlyList<CountryDto>>> Countries(CancellationToken ct) =>
        Ok((await db.Countries.AsNoTracking().OrderBy(c => c.Code).ToListAsync(ct)).Select(GeoMapping.ToDto).ToList());

    [HttpGet("countries")]
    public async Task<ActionResult<IReadOnlyList<CountryLookupDto>>> CountryLookup(CancellationToken ct) =>
        Ok(await db.Countries.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).Select(c => new CountryLookupDto(c.Id, c.Code, c.Name)).ToListAsync(ct));

    [HttpPost("countries")]
    public async Task<ActionResult<CountryDto>> CreateCountry(CreateCountryRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var country = await countryService.CreateAsync(new CreateCountryInput(request.Code, request.Iso3Code, request.Name, request.IsActive), Actor(), ct);
            return Ok(GeoMapping.ToDto(country));
        }
        catch (DomainValidationException ex) { return Invalid(ex); }
    }

    [HttpPut("countries/{id:guid}")]
    public async Task<ActionResult<CountryDto>> UpdateCountry(Guid id, UpdateCountryRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var country = await countryService.UpdateAsync(id, new UpdateCountryInput(request.Code, request.Iso3Code, request.Name), Actor(), ct);
            return country is null ? NotFound() : Ok(GeoMapping.ToDto(country));
        }
        catch (DomainValidationException ex) { return Invalid(ex); }
    }

    [HttpPost("countries/{id:guid}/status")]
    public async Task<IActionResult> SetCountryActive(Guid id, SetActiveRequest request, CancellationToken ct) =>
        await countryService.SetActiveAsync(id, request.IsActive, Actor(), ct) == GeoStatusOutcome.NotFound ? NotFound() : NoContent();

    [HttpGet("states/all")]
    public async Task<ActionResult<IReadOnlyList<StateDto>>> States([FromQuery] Guid? countryId, CancellationToken ct)
    {
        var query = db.States.AsNoTracking();
        if (countryId is { } cid) query = query.Where(s => s.CountryId == cid);
        var rows = await query.Join(db.Countries, s => s.CountryId, c => c.Id, (s, c) => new { s, c })
            .OrderBy(x => x.c.Code).ThenBy(x => x.s.Name).ToListAsync(ct);
        return Ok(rows.Select(x => GeoMapping.ToDto(x.s, x.c)).ToList());
    }

    [HttpGet("states")]
    public async Task<ActionResult<IReadOnlyList<StateLookupDto>>> StateLookup([FromQuery] Guid? countryId, CancellationToken ct)
    {
        if (countryId is null)
        {
            ModelState.AddModelError("countryId", "countryId is required.");
            return ValidationProblem(ModelState);
        }
        return Ok(await db.States.AsNoTracking()
            .Where(s => s.CountryId == countryId && s.IsActive && db.Countries.Any(c => c.Id == s.CountryId && c.IsActive))
            .OrderBy(s => s.Name).Select(s => new StateLookupDto(s.Id, s.CountryId, s.Code, s.Name, s.GstStateCode)).ToListAsync(ct));
    }

    [HttpPost("states")]
    public async Task<ActionResult<StateDto>> CreateState(CreateStateRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var state = await stateService.CreateAsync(new CreateStateInput(
                request.CountryId!.Value, request.Code, request.Name, request.Kind!.Value, request.GstStateCode, request.IsActive), Actor(), ct);
            return Ok(GeoMapping.ToDto(state, await db.Countries.AsNoTracking().SingleAsync(c => c.Id == state.CountryId, ct)));
        }
        catch (DomainValidationException ex) { return Invalid(ex); }
    }

    [HttpPut("states/{id:guid}")]
    public async Task<ActionResult<StateDto>> UpdateState(Guid id, UpdateStateRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var state = await stateService.UpdateAsync(id, new UpdateStateInput(request.Code, request.Name, request.Kind!.Value, request.GstStateCode), Actor(), ct);
            if (state is null) return NotFound();
            return Ok(GeoMapping.ToDto(state, await db.Countries.AsNoTracking().SingleAsync(c => c.Id == state.CountryId, ct)));
        }
        catch (DomainValidationException ex) { return Invalid(ex); }
    }

    [HttpPost("states/{id:guid}/status")]
    public async Task<IActionResult> SetStateActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        try
        {
            return await stateService.SetActiveAsync(id, request.IsActive, Actor(), ct) == GeoStatusOutcome.NotFound ? NotFound() : NoContent();
        }
        catch (DomainValidationException ex) { return Invalid(ex); }
    }

    private ActionResult Invalid(DomainValidationException ex)
    {
        foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
        return ValidationProblem(ModelState);
    }

    private ReferenceDataActor Actor() => ReferenceDataActor.Platform(
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!), HttpContext.TraceIdentifier);
}
