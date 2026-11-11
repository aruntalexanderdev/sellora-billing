using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Api.Validation;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Platform;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>Platform company management (Phase 6 D4-D6). Platform session + Platform.Admin only; never returns business data.</summary>
[ApiController]
[Route("api/v1/platform")]
[PlatformEndpoint]
[Authorize(Policy = PlatformPolicies.Admin)]
public class PlatformCompaniesController(IPlatformService platform, SelloraDbContext db) : ControllerBase
{
    [HttpGet("companies")]
    public async Task<ActionResult<IReadOnlyList<PlatformCompanyDto>>> List(CancellationToken ct)
    {
        var rows = await platform.ListCompaniesAsync(ct);
        return Ok(rows.Select(c => new PlatformCompanyDto(c.Id, c.Code, c.Name, c.IsActive, c.CreatedAt, c.MemberCount, c.OwnerCount)).ToList());
    }

    /// <summary>Active currencies, for the base-currency choice of a new company (currencies are global).</summary>
    [HttpGet("currencies")]
    public async Task<ActionResult<IReadOnlyList<PlatformCurrencyDto>>> Currencies(CancellationToken ct)
    {
        var rows = await db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code)
            .Select(c => new PlatformCurrencyDto(c.Id, c.Code, c.Name, c.Symbol)).ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost("companies")]
    public async Task<IActionResult> Create(CreatePlatformCompanyRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var n = request.Owner.NewUser;
        if (n is not null)
        {
            if (n.Password != n.ConfirmPassword) ModelState.AddModelError("owner.newUser.confirmPassword", "Passwords do not match.");
            if (!PasswordRules.IsValid(n.Password)) ModelState.AddModelError("owner.newUser.password", PasswordRules.RequirementText);
            if (!ModelState.IsValid) return ValidationProblem(ModelState);
        }

        var input = new CreateCompanyInput(
            request.Code, request.Name, request.BusinessName, request.InvoicePrefix, request.BaseCurrencyId!.Value,
            request.Gstin, request.AddressLine, request.City, request.State, request.Phone, request.Email,
            new PlatformOwnerInput(request.Owner.UserId,
                n is null ? null : new PlatformNewOwner(n.FirstName, n.LastName, n.Username, n.Email, n.PhoneNumber, n.Password)),
            request.Reason);

        try
        {
            var created = await platform.CreateCompanyAsync(input, ActorId(), HttpContext.TraceIdentifier, ct);
            return Created($"/api/v1/platform/companies/{created.Id}",
                new CreatedPlatformCompanyDto(created.Id, created.Code, created.MainBranchId, created.OwnerUserId));
        }
        catch (CompanyCreationDisabledException ex)
        {
            return Problem(statusCode: 403, title: ex.Code == "company_creation_disabled" ? "Company creation is disabled on this platform." : ex.Message,
                extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
    }

    [HttpPost("companies/{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, SetPlatformCompanyStatusRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var outcome = await platform.SetCompanyStatusAsync(id, request.IsActive, request.Reason, ActorId(), HttpContext.TraceIdentifier, ct);
        return outcome == PlatformCompanyStatusOutcome.NotFound ? NotFound() : NoContent();
    }

    private Guid ActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
}
