using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/currencies")]
public class CurrenciesController(SelloraDbContext db) : ControllerBase
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

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Currencies.AnyAsync(c => c.Code == code, ct))
        {
            ModelState.AddModelError(nameof(request.Code), "A currency with this code already exists.");
            return ValidationProblem(ModelState);
        }

        var currency = new Currency
        {
            Id = Guid.NewGuid(), Code = code, Name = request.Name.Trim(),
            Symbol = request.Symbol.Trim(), DecimalPrecision = request.DecimalPrecision,
        };
        db.Currencies.Add(currency);
        await db.SaveChangesAsync(ct);

        return Ok(ToDto(currency, isBase: false));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<CurrencyDto>> Update(Guid id, UpsertCurrencyRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var currency = await db.Currencies.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (currency is null) return NotFound();

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Currencies.AnyAsync(c => c.Id != id && c.Code == code, ct))
        {
            ModelState.AddModelError(nameof(request.Code), "A currency with this code already exists.");
            return ValidationProblem(ModelState);
        }

        currency.Code = code;
        currency.Name = request.Name.Trim();
        currency.Symbol = request.Symbol.Trim();
        currency.DecimalPrecision = request.DecimalPrecision;
        await db.SaveChangesAsync(ct);

        return Ok(ToDto(currency, currency.Id == await BaseCurrencyIdAsync(ct)));
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        var currency = await db.Currencies.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (currency is null) return NotFound();

        // Platform check, documented IgnoreQueryFilters bypass: a currency that is ANY company's base currency cannot be
        // deactivated, so this must see every company's settings, not just the current one.
        if (!request.IsActive && await db.BusinessSettings.IgnoreQueryFilters([CompanyQueryFilter.Name]).AnyAsync(s => s.BaseCurrencyId == id, ct))
        {
            return Problem(statusCode: 400, title: "The business base currency cannot be deactivated. Choose a different base currency in Business Settings first.");
        }

        currency.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<Guid> BaseCurrencyIdAsync(CancellationToken ct) => (await db.GetCurrentCompanySettingsAsync(ct)).BaseCurrencyId;

    private static CurrencyDto ToDto(Currency c, bool isBase) => new(c.Id, c.Code, c.Name, c.Symbol, c.DecimalPrecision, c.IsActive, isBase);
}
