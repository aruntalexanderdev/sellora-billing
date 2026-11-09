using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/settings")]
[Authorize(Policy = "Masters.View")]
public class SettingsController(SelloraDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<BusinessSettingsDto>> Get(CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var settings = await db.BusinessSettings.Include(s => s.BaseCurrency).SingleAsync(s => s.CompanyId == companyId, ct);
        return Ok(ToDto(settings));
    }

    [HttpPut]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<BusinessSettingsDto>> Update(UpdateBusinessSettingsRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var companyId = db.CompanyContext.RequireCompanyId();
        var settings = await db.BusinessSettings.Include(s => s.BaseCurrency).SingleAsync(s => s.CompanyId == companyId, ct);

        var baseCurrency = await db.Currencies.SingleOrDefaultAsync(c => c.Id == request.BaseCurrencyId, ct);
        if (baseCurrency is null)
        {
            ModelState.AddModelError(nameof(request.BaseCurrencyId), "Select a valid base currency.");
            return ValidationProblem(ModelState);
        }
        if (!baseCurrency.IsActive)
        {
            ModelState.AddModelError(nameof(request.BaseCurrencyId), "The base currency must be active.");
            return ValidationProblem(ModelState);
        }

        settings.BusinessName = request.BusinessName.Trim();
        settings.AddressLine = request.AddressLine;
        settings.City = request.City;
        settings.State = request.State;
        settings.Gstin = request.Gstin;
        settings.Phone = request.Phone;
        settings.Email = request.Email;
        settings.InvoicePrefix = request.InvoicePrefix.Trim().ToUpperInvariant();
        settings.BaseCurrencyId = baseCurrency.Id;
        settings.BaseCurrency = baseCurrency;

        // The settings row is the company's profile (ADR-07); the company name follows it. This is the single edit point.
        var company = await db.Companies.SingleAsync(c => c.Id == companyId, ct);
        company.Name = settings.BusinessName;

        await db.SaveChangesAsync(ct);
        return Ok(ToDto(settings));
    }

    private static BusinessSettingsDto ToDto(Domain.Entities.BusinessSettings s) =>
        new(s.Id, s.BusinessName, s.AddressLine, s.City, s.State, s.Gstin, s.Phone, s.Email, s.InvoicePrefix, s.BaseCurrencyId, s.BaseCurrency.Code, s.BaseCurrency.Symbol, s.CompanyId);
}
