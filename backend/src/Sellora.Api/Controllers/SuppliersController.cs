using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/suppliers")]
public class SuppliersController(SelloraDbContext db) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Suppliers.View")]
    public async Task<ActionResult<PagedResult<SupplierDto>>> List(
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Suppliers.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(s) || (x.City != null && x.City.ToLower().Contains(s)));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.Name).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new SupplierDto(x.Id, x.Name, x.City, x.Gstin, x.PaymentTerms, x.IsActive))
            .ToListAsync(ct);

        return Ok(new PagedResult<SupplierDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Suppliers.View")]
    public async Task<ActionResult<SupplierDto>> Get(Guid id, CancellationToken ct)
    {
        var supplier = await db.Suppliers.SingleOrDefaultAsync(x => x.Id == id, ct);
        return supplier is null ? NotFound() : Ok(SupplierMapping.ToDto(supplier));
    }

    [HttpPost]
    [Authorize(Policy = "Suppliers.Create")]
    public async Task<ActionResult<SupplierDto>> Create(UpsertSupplierRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        if (await db.Suppliers.AnyAsync(x => x.Name.ToLower() == request.Name.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Name), "A supplier with this name already exists.");
            return ValidationProblem(ModelState);
        }

        var supplier = new Supplier { Id = Guid.NewGuid(), Name = request.Name.Trim(), City = request.City, Gstin = request.Gstin, PaymentTerms = request.PaymentTerms };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = supplier.Id }, SupplierMapping.ToDto(supplier));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Suppliers.Edit")]
    public async Task<ActionResult<SupplierDto>> Update(Guid id, UpsertSupplierRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var supplier = await db.Suppliers.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (supplier is null) return NotFound();

        if (await db.Suppliers.AnyAsync(x => x.Id != id && x.Name.ToLower() == request.Name.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Name), "A supplier with this name already exists.");
            return ValidationProblem(ModelState);
        }

        supplier.Name = request.Name.Trim();
        supplier.City = request.City;
        supplier.Gstin = request.Gstin;
        supplier.PaymentTerms = request.PaymentTerms;
        await db.SaveChangesAsync(ct);

        return Ok(SupplierMapping.ToDto(supplier));
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Suppliers.Disable")]
    public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        var supplier = await db.Suppliers.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (supplier is null) return NotFound();

        supplier.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
