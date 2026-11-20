using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;

namespace Sellora.Api.Controllers;

/// <summary>Global state master (company view); same policies and guard as <see cref="CountriesController"/>.</summary>
[ApiController]
[Route("api/v1/states")]
public class StatesController(SelloraDbContext db, IStateService stateService, IOptions<PlatformOptions> platformOptions) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Masters.View")]
    public async Task<ActionResult<StateListDto>> List([FromQuery] Guid? countryId, [FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var query = db.States.AsNoTracking();
        if (countryId is { } cid) query = query.Where(s => s.CountryId == cid);
        if (!includeInactive) query = query.Where(s => s.IsActive);
        var rows = await query.Join(db.Countries, s => s.CountryId, c => c.Id, (s, c) => new { s, c })
            .OrderBy(x => x.c.Name).ThenBy(x => x.s.Name).ToListAsync(ct);
        return Ok(new StateListDto(platformOptions.Value.ReferenceDataEditableByCompanies, rows.Select(x => GeoMapping.ToDto(x.s, x.c)).ToList()));
    }

    [HttpGet("lookup")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<StateLookupDto>>> Lookup([FromQuery] Guid? countryId, CancellationToken ct)
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

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Masters.View")]
    public async Task<ActionResult<StateDto>> Get(Guid id, CancellationToken ct)
    {
        var state = await db.States.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
        if (state is null) return NotFound();
        return Ok(GeoMapping.ToDto(state, await db.Countries.AsNoTracking().SingleAsync(c => c.Id == state.CountryId, ct)));
    }

    [HttpPost]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<StateDto>> Create(CreateStateRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var state = await stateService.CreateAsync(new CreateStateInput(
                request.CountryId!.Value, request.Code, request.Name, request.Kind!.Value, request.GstStateCode, request.IsActive), Actor(), ct);
            return Ok(GeoMapping.ToDto(state, await db.Countries.AsNoTracking().SingleAsync(c => c.Id == state.CountryId, ct)));
        }
        catch (DomainValidationException ex)
        {
            foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<StateDto>> Update(Guid id, UpdateStateRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var state = await stateService.UpdateAsync(id, new UpdateStateInput(request.Code, request.Name, request.Kind!.Value, request.GstStateCode), Actor(), ct);
            if (state is null) return NotFound();
            return Ok(GeoMapping.ToDto(state, await db.Countries.AsNoTracking().SingleAsync(c => c.Id == state.CountryId, ct)));
        }
        catch (DomainValidationException ex)
        {
            foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        try
        {
            return await stateService.SetActiveAsync(id, request.IsActive, Actor(), ct) == GeoStatusOutcome.NotFound ? NotFound() : NoContent();
        }
        catch (DomainValidationException ex)
        {
            foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
            return ValidationProblem(ModelState);
        }
    }

    private ReferenceDataActor Actor() => ReferenceDataActor.ForCompany(
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!),
        db.CompanyContext.RequireCompanyId(), HttpContext.TraceIdentifier);
}
