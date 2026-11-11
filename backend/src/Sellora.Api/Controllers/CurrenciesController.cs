using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/currencies")]
public class CurrenciesController(SelloraDbContext db, ICurrencyService currencyService) : ControllerBase
{
    // Read-only reference data that the supplier, settings and exchange-rate forms all need, so any
    // signed-in user can list it; creating, editing and (de)activating stay behind Masters.Edit.
    [HttpGet]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<CurrencyDto>>> List(CancellationToken ct)
    {
        var baseCurrencyId = await BaseCurrencyIdAsync(ct);
        var currencies = await db.Currencies.OrderBy(c => c.Code)
            .Select(c => new CurrencyDto(c.Id, c.Code, c.Name, c.Symbol, c.DecimalPrecision, c.IsActive, c.Id == baseCurrencyId))
            .ToListAsync(ct);
        return Ok(currencies);
    }

    [HttpPost]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<CurrencyDto>> Create(UpsertCurrencyRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var currency = await currencyService.CreateAsync(ToInput(request), ReferenceDataActor.Company, ct);
            return Ok(ToDto(currency, isBase: false));
        }
        catch (DomainValidationException ex)
        {
            foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<CurrencyDto>> Update(Guid id, UpsertCurrencyRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var currency = await currencyService.UpdateAsync(id, ToInput(request), ReferenceDataActor.Company, ct);
            if (currency is null) return NotFound();
            return Ok(ToDto(currency, currency.Id == await BaseCurrencyIdAsync(ct)));
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
        var outcome = await currencyService.SetActiveAsync(id, request.IsActive, ReferenceDataActor.Company, ct);
        return outcome switch
        {
            CurrencyStatusOutcome.NotFound => NotFound(),
            CurrencyStatusOutcome.IsBaseCurrency => Problem(statusCode: 400, title: "The business base currency cannot be deactivated. Choose a different base currency in Business Settings first."),
            _ => NoContent(),
        };
    }

    private async Task<Guid> BaseCurrencyIdAsync(CancellationToken ct) => (await db.GetCurrentCompanySettingsAsync(ct)).BaseCurrencyId;

    private static UpsertCurrencyInput ToInput(UpsertCurrencyRequest r) => new(r.Code, r.Name, r.Symbol, r.DecimalPrecision);

    private static CurrencyDto ToDto(Currency c, bool isBase) => new(c.Id, c.Code, c.Name, c.Symbol, c.DecimalPrecision, c.IsActive, isBase);
}
