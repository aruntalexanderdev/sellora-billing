using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Api.Controllers;

/// <summary>Branch management (Phase 2). Using a branch (X-Branch-Id header, list filters, <c>accessible</c>) needs no permission:
/// a branch is a data scope, not an action.</summary>
[ApiController]
[Route("api/v1/branches")]
public class BranchesController(SelloraDbContext db, IBranchService branchService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Company.Branches.View")]
    public async Task<ActionResult<IReadOnlyList<BranchDto>>> List([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var query = db.Branches.AsNoTracking().Where(b => b.CompanyId == companyId);
        if (!includeInactive) query = query.Where(b => b.IsActive);

        var branches = await query.OrderByDescending(b => b.IsDefault).ThenBy(b => b.Name).ToListAsync(ct);
        var companyPrefix = (await db.GetCurrentCompanySettingsAsync(ct)).InvoicePrefix;
        return Ok(branches.Select(b => ToDto(b, companyPrefix)).ToList());
    }

    [HttpGet("accessible")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<AccessibleBranchDto>>> Accessible(CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var branches = await db.Branches.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.IsActive)
            .OrderByDescending(b => b.IsDefault).ThenBy(b => b.Name)
            .ToListAsync(ct);
        return Ok(branches.Where(b => db.BranchContext.CanAccess(b.Id))
            .Select(b => new AccessibleBranchDto(b.Id, b.Code, b.Name, b.IsDefault)).ToList());
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Company.Branches.View")]
    public async Task<ActionResult<BranchDto>> Get(Guid id, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var branch = await db.Branches.AsNoTracking().SingleOrDefaultAsync(b => b.CompanyId == companyId && b.Id == id, ct);
        if (branch is null) return NotFound();

        return Ok(ToDto(branch, (await db.GetCurrentCompanySettingsAsync(ct)).InvoicePrefix));
    }

    [HttpPost]
    [Authorize(Policy = "Company.Branches.Create")]
    public async Task<ActionResult<BranchDto>> Create(CreateBranchRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var branch = await branchService.CreateAsync(new CreateBranchInput(
                request.Code, request.Name, request.AddressLine, request.City, request.State, request.Phone, request.Email,
                request.Gstin, request.InvoicePrefix), CurrentUserId(), ct);
            var dto = ToDto(branch, (await db.GetCurrentCompanySettingsAsync(ct)).InvoicePrefix);
            return CreatedAtAction(nameof(Get), new { id = branch.Id }, dto);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Company.Branches.Edit")]
    public async Task<ActionResult<BranchDto>> Update(Guid id, UpdateBranchRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var branch = await branchService.UpdateAsync(id, new UpdateBranchInput(
                request.Name, request.AddressLine, request.City, request.State, request.Phone, request.Email,
                request.Gstin, request.InvoicePrefix), CurrentUserId(), ct);
            if (branch is null) return NotFound();

            return Ok(ToDto(branch, (await db.GetCurrentCompanySettingsAsync(ct)).InvoicePrefix));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Company.Branches.Disable")]
    public async Task<ActionResult<BranchDto>> SetStatus(Guid id, SetStatusRequest request, CancellationToken ct)
    {
        try
        {
            var branch = await branchService.SetStatusAsync(id, request.IsActive, request.Reason, CurrentUserId(), ct);
            if (branch is null) return NotFound();

            return Ok(ToDto(branch, (await db.GetCurrentCompanySettingsAsync(ct)).InvoicePrefix));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private static BranchDto ToDto(Branch b, string companyPrefix) => new(
        b.Id, b.Code, b.Name, b.AddressLine, b.City, b.State, b.Phone, b.Email, b.Gstin, b.InvoicePrefix,
        DocumentNumberFormat.InvoicePrefix(b, companyPrefix), b.IsDefault, b.IsActive);
}
