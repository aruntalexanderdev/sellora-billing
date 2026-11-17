using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

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

        // The company prefix is a numbering segment shared with the branches: the check and the save run under the same
        // company row lock the branch service uses, so a concurrent branch edit cannot slip a colliding prefix in.
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
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
        settings.State = request.State;
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
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Ok(ToDto(settings));
    }

    private static BusinessSettingsDto ToDto(Domain.Entities.BusinessSettings s) =>
        new(s.Id, s.BusinessName, s.AddressLine, s.City, s.State, s.Gstin, s.Phone, s.Email, s.InvoicePrefix, s.BaseCurrencyId, s.BaseCurrency.Code, s.BaseCurrency.Symbol, s.CompanyId, s.ReceiptPostingMode, s.ReceiptSeparationOfDuties);
}
