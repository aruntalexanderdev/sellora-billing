using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/exchange-rates")]
public class ExchangeRatesController(SelloraDbContext db, IExchangeRateService exchangeRateService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Masters.View")]
    public async Task<ActionResult<IReadOnlyList<ExchangeRateDto>>> List([FromQuery] Guid? currencyId, CancellationToken ct)
    {
        var query = db.ExchangeRates.AsQueryable();
        if (currencyId is not null) query = query.Where(r => r.CurrencyId == currencyId);

        var rates = await query
            .OrderBy(r => r.Currency.Code).ThenByDescending(r => r.EffectiveDate)
            .Select(r => new ExchangeRateDto(r.Id, r.CurrencyId, r.Currency.Code, r.Currency.Symbol, r.RateToBase, r.EffectiveDate, r.CreatedAt))
            .ToListAsync(ct);
        return Ok(rates);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Masters.View")]
    public async Task<ActionResult<ExchangeRateDto>> Get(Guid id, CancellationToken ct)
    {
        var rate = await db.ExchangeRates.Include(r => r.Currency).SingleOrDefaultAsync(r => r.Id == id, ct);
        return rate is null ? NotFound() : Ok(ToDto(rate));
    }

    // Read-only lookup used by the purchase form's conversion preview, so it is open to any signed-in
    // user rather than requiring Masters.View. The authoritative rate is always re-resolved server-side
    // when a purchase is saved.
    [HttpGet("resolve")]
    [Authorize]
    public async Task<ActionResult<ResolvedExchangeRateDto>> Resolve([FromQuery] Guid currencyId, [FromQuery] DateOnly date, CancellationToken ct)
    {
        try
        {
            var rate = await exchangeRateService.ResolveRateToBaseAsync(currencyId, date, ct);
            var baseCurrency = await db.GetBaseCurrencyAsync(ct);
            return Ok(new ResolvedExchangeRateDto(currencyId, date, rate, baseCurrency.Code, baseCurrency.Symbol, baseCurrency.DecimalPrecision));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<ExchangeRateDto>> Create(UpsertExchangeRateRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!await ValidateAsync(request, null, ct)) return ValidationProblem(ModelState);

        var rate = new ExchangeRate
        {
            Id = Guid.NewGuid(),
            CurrencyId = request.CurrencyId!.Value,
            RateToBase = request.RateToBase,
            EffectiveDate = request.EffectiveDate,
            CreatedByUserId = CurrentUserId(),
        };
        db.ExchangeRates.Add(rate);
        await db.SaveChangesAsync(ct);

        await db.Entry(rate).Reference(r => r.Currency).LoadAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = rate.Id }, ToDto(rate));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<ExchangeRateDto>> Update(Guid id, UpsertExchangeRateRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var rate = await db.ExchangeRates.Include(r => r.Currency).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (rate is null) return NotFound();

        if (!await ValidateAsync(request, id, ct)) return ValidationProblem(ModelState);

        rate.CurrencyId = request.CurrencyId!.Value;
        rate.RateToBase = request.RateToBase;
        rate.EffectiveDate = request.EffectiveDate;
        await db.SaveChangesAsync(ct);

        await db.Entry(rate).Reference(r => r.Currency).LoadAsync(ct);
        return Ok(ToDto(rate));
    }

    private async Task<bool> ValidateAsync(UpsertExchangeRateRequest request, Guid? existingId, CancellationToken ct)
    {
        var currency = await db.Currencies.SingleOrDefaultAsync(c => c.Id == request.CurrencyId, ct);
        if (currency is null)
        {
            ModelState.AddModelError(nameof(request.CurrencyId), "Select a valid currency.");
            return false;
        }
        if (!currency.IsActive)
        {
            ModelState.AddModelError(nameof(request.CurrencyId), "This currency is inactive.");
            return false;
        }
        if ((await db.GetCurrentCompanySettingsAsync(ct)).BaseCurrencyId == currency.Id)
        {
            ModelState.AddModelError(nameof(request.CurrencyId), "The base currency is always 1:1 with itself and cannot have an exchange rate.");
            return false;
        }
        if (await db.ExchangeRates.AnyAsync(r => r.Id != existingId && r.CurrencyId == currency.Id && r.EffectiveDate == request.EffectiveDate, ct))
        {
            ModelState.AddModelError(nameof(request.EffectiveDate), "A rate for this currency is already effective from this date — edit that one instead.");
            return false;
        }
        return true;
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private static ExchangeRateDto ToDto(ExchangeRate r) =>
        new(r.Id, r.CurrencyId, r.Currency.Code, r.Currency.Symbol, r.RateToBase, r.EffectiveDate, r.CreatedAt);
}
