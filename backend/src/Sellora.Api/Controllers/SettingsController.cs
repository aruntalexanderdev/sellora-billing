using System.Security.Claims;
using System.Text.Json;
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
[Route("api/v1/settings")]
[Authorize(Policy = "Masters.View")]
public class SettingsController(SelloraDbContext db, IBranchService branchService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<BusinessSettingsDto>> Get(CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var settings = await db.BusinessSettings.Include(s => s.BaseCurrency).SingleAsync(s => s.CompanyId == companyId, ct);
        return Ok(await ToDtoAsync(settings, ct));
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

        try
        {
            await GeoLinkValidator.ValidateAsync(db, request.CountryId, request.StateId, settings.CountryId, settings.StateId, ct);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }

        // The company prefix is a numbering segment shared with the branches: the check and the save run under the same
        // company row lock the branch service uses, so a concurrent branch edit cannot slip a colliding prefix in.
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        // T24: the country can change only while no product or document exists; the new country's standard tax profiles are seeded below.
        // While products or documents exist the country is locked: a different country is refused, and an omitted one (which would clear it) leaves it as it is.
        var countryId = request.CountryId;
        if (countryId is null && settings.CountryId is not null && await TaxCountryChange.IsLockedAsync(db, ct)) countryId = settings.CountryId;
        var countryChanging = countryId != settings.CountryId;
        if (countryChanging)
        {
            try
            {
                await TaxCountryChange.EnsureAllowedAsync(db, ct);
            }
            catch (DomainValidationException ex)
            {
                ModelState.AddModelError(ex.Field, ex.Message);
                return ValidationProblem(ModelState);
            }
        }
        var invoicePrefix = request.InvoicePrefix.Trim().ToUpperInvariant();
        if (!string.Equals(invoicePrefix, settings.InvoicePrefix, StringComparison.Ordinal))
        {
            try
            {
                await branchService.EnsureCompanyInvoicePrefixAllowedAsync(invoicePrefix, ct);
            }
            catch (DomainValidationException ex)
            {
                ModelState.AddModelError(ex.Field, ex.Message);
                return ValidationProblem(ModelState);
            }
        }

        settings.BusinessName = request.BusinessName.Trim();
        settings.AddressLine = request.AddressLine;
        settings.City = request.City;
        // D17: null clears (unless the country is locked by products or documents, T24: then country and state stay); legacy State text is frozen (request.State ignored)
        if (request.CountryId is not null || countryId is null)
        {
            settings.CountryId = countryId;
            settings.StateId = request.StateId;
        }
        settings.Gstin = request.Gstin;
        settings.Phone = request.Phone;
        settings.Email = request.Email;
        settings.InvoicePrefix = invoicePrefix;
        settings.BaseCurrencyId = baseCurrency.Id;
        settings.BaseCurrency = baseCurrency;

        // Phase 06 (FI-D39): the receipt policy. Omitted = unchanged. Existing receipts keep the policy snapshotted at their creation.
        var beforePolicy = new { mode = settings.ReceiptPostingMode.ToString(), separation = settings.ReceiptSeparationOfDuties };
        if (request.ReceiptPostingMode is { } postingMode)
        {
            if (!Enum.IsDefined(postingMode))
            {
                ModelState.AddModelError(nameof(request.ReceiptPostingMode), "Choose Direct or SupervisorApproval.");
                return ValidationProblem(ModelState);
            }
            settings.ReceiptPostingMode = postingMode;
        }
        if (request.ReceiptSeparationOfDuties is { } separation) settings.ReceiptSeparationOfDuties = separation;

        // Tax profiles (TP-1): the price basis of stored selling prices (omitted = unchanged) and the whole-bill discount cap (null = no limit).
        if (request.SellingPricesIncludeTax is { } includeTax) settings.SellingPricesIncludeTax = includeTax;
        settings.MaxBillDiscountPercent = request.MaxBillDiscountPercent is { } cap ? Math.Round(cap, 2, MidpointRounding.AwayFromZero) : null;
        var afterPolicy = new { mode = settings.ReceiptPostingMode.ToString(), separation = settings.ReceiptSeparationOfDuties };
        if (!beforePolicy.Equals(afterPolicy))
        {
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                OccurredAt = DateTimeOffset.UtcNow,
                ActorUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!),
                Action = AuditActions.ReceiptPolicyChanged,
                TargetType = AuditTargets.BusinessSettings,
                TargetId = settings.Id,
                Before = JsonSerializer.Serialize(beforePolicy, json),
                After = JsonSerializer.Serialize(afterPolicy, json),
                CorrelationId = HttpContext.TraceIdentifier.Length > 64 ? HttpContext.TraceIdentifier[..64] : HttpContext.TraceIdentifier,
            });
        }

        // The settings row is the company's profile (ADR-07); the company name follows it. This is the single edit point.
        var company = await db.Companies.SingleAsync(c => c.Id == companyId, ct);
        company.Name = settings.BusinessName;

        await db.SaveChangesAsync(ct);
        if (countryChanging && countryId is { } newCountry) await TaxCountryChange.SeedNewCountryAsync(db, companyId, newCountry, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Ok(await ToDtoAsync(settings, ct));
    }

    private async Task<BusinessSettingsDto> ToDtoAsync(Domain.Entities.BusinessSettings s, CancellationToken ct)
    {
        var geo = await GeoNames.LoadAsync(db, [s.CountryId], [s.StateId], ct);
        return ToDto(s, geo);
    }

    private static BusinessSettingsDto ToDto(Domain.Entities.BusinessSettings s, GeoNames geo) =>
        new(s.Id, s.BusinessName, s.AddressLine, s.City, s.State, s.Gstin, s.Phone, s.Email, s.InvoicePrefix, s.BaseCurrencyId, s.BaseCurrency.Code, s.BaseCurrency.Symbol, s.CompanyId, s.ReceiptPostingMode, s.ReceiptSeparationOfDuties,
            s.CountryId, geo.Country(s.CountryId), s.StateId, geo.State(s.StateId),
            s.SellingPricesIncludeTax, s.MaxBillDiscountPercent);
}
