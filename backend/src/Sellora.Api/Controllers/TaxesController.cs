using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/taxes")]
[Authorize(Policy = "Masters.View")]
public class TaxesController(SelloraDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TaxDto>>> List(CancellationToken ct)
    {
        var taxes = await db.Taxes.OrderBy(t => t.RatePercent)
            .Select(t => new TaxDto(t.Id, t.Name, t.RatePercent, t.IsActive))
            .ToListAsync(ct);
        return Ok(taxes);
    }

    [HttpPost]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<TaxDto>> Create(UpsertTaxRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        if (await db.Taxes.AnyAsync(t => t.Name.ToLower() == request.Name.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Name), "A tax with this name already exists.");
            return ValidationProblem(ModelState);
        }

        var tax = new Tax { Id = Guid.NewGuid(), Name = request.Name.Trim(), RatePercent = request.RatePercent };
        db.Taxes.Add(tax);
        await db.SaveChangesAsync(ct);

        return Ok(new TaxDto(tax.Id, tax.Name, tax.RatePercent, tax.IsActive));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<ActionResult<TaxDto>> Update(Guid id, UpsertTaxRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var tax = await db.Taxes.SingleOrDefaultAsync(t => t.Id == id, ct);
        if (tax is null) return NotFound();

        if (await db.Taxes.AnyAsync(t => t.Id != id && t.Name.ToLower() == request.Name.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Name), "A tax with this name already exists.");
            return ValidationProblem(ModelState);
        }

        tax.Name = request.Name.Trim();
        tax.RatePercent = request.RatePercent;
        await db.SaveChangesAsync(ct);

        return Ok(new TaxDto(tax.Id, tax.Name, tax.RatePercent, tax.IsActive));
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Masters.Edit")]
    public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        var tax = await db.Taxes.SingleOrDefaultAsync(t => t.Id == id, ct);
        if (tax is null) return NotFound();

        tax.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
