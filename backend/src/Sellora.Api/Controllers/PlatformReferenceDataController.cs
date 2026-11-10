using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>
/// Platform-managed shared reference data (Phase 7 D11/Q3): Units and Currencies are platform rows, so their writes belong to the platform
/// session (<c>Platform.Admin</c>, no company context) once <c>Platform:ReferenceDataEditableByCompanies</c> is false. Reuses the unit service
/// and the currency rules of the company endpoints. "Base" on a currency = it is the base currency of any company.
/// </summary>
[ApiController]
[Route("api/v1/platform")]
[PlatformEndpoint]
[Authorize(Policy = PlatformPolicies.Admin)]
public class PlatformReferenceDataController(SelloraDbContext db, IUnitService unitService, ICurrencyService currencyService) : ControllerBase
{
    private static readonly string[] CompanyFilter = [CompanyQueryFilter.Name];

    [HttpGet("units")]
    public async Task<ActionResult<IReadOnlyList<UnitDto>>> Units(CancellationToken ct)
    {
        var units = await db.Units.OrderBy(u => u.Name).ToListAsync(ct);
        var dtos = new List<UnitDto>();
        foreach (var u in units)
        {
            dtos.Add(ToDto(u, await unitService.IsInUseAsync(u.Id, ct), !await unitService.IsUsedByActiveProductAsync(u.Id, ct)));
        }
        return Ok(dtos);
    }

    [HttpPost("units")]
    public async Task<ActionResult<UnitDto>> CreateUnit(UpsertUnitRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var unit = await unitService.CreateUnitAsync(ToInput(request), ct, Actor());
            return Ok(ToDto(unit, isInUse: false, canDeactivate: true));
        }
        catch (DomainValidationException ex)
        {
            foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPut("units/{id:guid}")]
    public async Task<ActionResult<UnitDto>> UpdateUnit(Guid id, UpsertUnitRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var unit = await unitService.UpdateUnitAsync(id, ToInput(request), ct, Actor());
            if (unit is null) return NotFound();
            return Ok(ToDto(unit, await unitService.IsInUseAsync(unit.Id, ct), !await unitService.IsUsedByActiveProductAsync(unit.Id, ct)));
        }
        catch (DomainValidationException ex)
        {
            foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("units/{id:guid}/status")]
    public async Task<IActionResult> SetUnitActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        try
        {
            var unit = await unitService.SetActiveAsync(id, request.IsActive, ct, Actor());
            return unit is null ? NotFound() : NoContent();
        }
        catch (DomainValidationException ex)
        {
            foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpGet("currencies/all")]
    public async Task<ActionResult<IReadOnlyList<CurrencyDto>>> Currencies(CancellationToken ct)
    {
        // Documented Company-filter bypass: a platform session has no company; "base" = base currency of any company.
        var baseIds = await db.BusinessSettings.IgnoreQueryFilters(CompanyFilter).Select(s => s.BaseCurrencyId).Distinct().ToListAsync(ct);
        var rows = await db.Currencies.OrderBy(c => c.Code).ToListAsync(ct);
        return Ok(rows.Select(c => ToDto(c, baseIds.Contains(c.Id))).ToList());
    }

    [HttpPost("currencies")]
    public async Task<ActionResult<CurrencyDto>> CreateCurrency(UpsertCurrencyRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var currency = await currencyService.CreateAsync(ToInput(request), Actor(), ct);
            return Ok(ToDto(currency, false));
        }
        catch (DomainValidationException ex)
        {
            foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPut("currencies/{id:guid}")]
    public async Task<ActionResult<CurrencyDto>> UpdateCurrency(Guid id, UpsertCurrencyRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var currency = await currencyService.UpdateAsync(id, ToInput(request), Actor(), ct);
            if (currency is null) return NotFound();
            return Ok(ToDto(currency, await db.BusinessSettings.IgnoreQueryFilters(CompanyFilter).AnyAsync(s => s.BaseCurrencyId == id, ct)));
        }
        catch (DomainValidationException ex)
        {
            foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("currencies/{id:guid}/status")]
    public async Task<IActionResult> SetCurrencyActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        var outcome = await currencyService.SetActiveAsync(id, request.IsActive, Actor(), ct);
        return outcome switch
        {
            CurrencyStatusOutcome.NotFound => NotFound(),
            CurrencyStatusOutcome.IsBaseCurrency => Problem(statusCode: 400, title: "A currency that is a company's base currency cannot be deactivated."),
            _ => NoContent(),
        };
    }

    private ReferenceDataActor Actor() => ReferenceDataActor.Platform(
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!), HttpContext.TraceIdentifier);

    private static UpsertCurrencyInput ToInput(UpsertCurrencyRequest r) => new(r.Code, r.Name, r.Symbol, r.DecimalPrecision);

    private static UpsertUnitInput ToInput(UpsertUnitRequest request) => new(
        request.Name, request.ShortCode, request.MeasurementCategory!.Value, request.DecimalPlaces, request.StandardFactor);

    private static UnitDto ToDto(Unit u, bool isInUse, bool canDeactivate) => new(
        u.Id, u.Name, u.ShortCode, u.MeasurementCategory, u.DecimalPlaces, u.StandardFactor, u.IsActive, isInUse, canDeactivate);

    private static CurrencyDto ToDto(Currency c, bool isBase) => new(c.Id, c.Code, c.Name, c.Symbol, c.DecimalPrecision, c.IsActive, isBase);
}
