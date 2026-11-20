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

/// <summary>Global country master (company view). Reads need <c>Masters.View</c> (lookup: any signed-in user, D15); writes need
/// <c>Masters.Edit</c> and are refused with 403 <c>reference_data_platform_managed</c> while the platform manages reference data (D8).</summary>
[ApiController]
[Route("api/v1/countries")]
public class CountriesController(SelloraDbContext db, ICountryService countryService, IOptions<PlatformOptions> platformOptions) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Masters.View")]
    public async Task<ActionResult<CountryListDto>> List([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var query = db.Countries.AsNoTracking();
        if (!includeInactive) query = query.Where(c => c.IsActive);
        var items = await query.OrderBy(c => c.Name).ToListAsync(ct);
        return Ok(new CountryListDto(platformOptions.Value.ReferenceDataEditableByCompanies, items.Select(GeoMapping.ToDto).ToList()));
    }

    [HttpGet("lookup")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<CountryLookupDto>>> Lookup(CancellationToken ct) =>
        Ok(await db.Countries.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name)
            .Select(c => new CountryLookupDto(c.Id, c.Code, c.Name)).ToListAsync(ct));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Masters.View")]
    public async Task<ActionResult<CountryDto>> Get(Guid id, CancellationToken ct)
    {
        var country = await db.Countries.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        return country is null ? NotFound() : Ok(GeoMapping.ToDto(country));
    }

    [HttpPost]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<CountryDto>> Create(CreateCountryRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var country = await countryService.CreateAsync(new CreateCountryInput(request.Code, request.Iso3Code, request.Name, request.IsActive), Actor(), ct);
            return Ok(GeoMapping.ToDto(country));
        }
        catch (DomainValidationException ex)
        {
            foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<CountryDto>> Update(Guid id, UpdateCountryRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var country = await countryService.UpdateAsync(id, new UpdateCountryInput(request.Code, request.Iso3Code, request.Name), Actor(), ct);
            return country is null ? NotFound() : Ok(GeoMapping.ToDto(country));
        }
        catch (DomainValidationException ex)
        {
            foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken ct) =>
        await countryService.SetActiveAsync(id, request.IsActive, Actor(), ct) == GeoStatusOutcome.NotFound ? NotFound() : NoContent();

    private ReferenceDataActor Actor() => ReferenceDataActor.ForCompany(
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!),
        db.CompanyContext.RequireCompanyId(), HttpContext.TraceIdentifier);
}
