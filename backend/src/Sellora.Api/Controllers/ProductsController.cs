using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/products")]
public class ProductsController(SelloraDbContext db) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Inventory.View")]
    public async Task<ActionResult<PagedResult<ProductListItemDto>>> List(
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Products.Include(p => p.BaseUnit).Include(p => p.Tax).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(s) || p.Sku.ToLower().Contains(s) || (p.Category != null && p.Category.ToLower().Contains(s)));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(p => p.Name).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new ProductListItemDto(p.Id, p.Sku, p.Name, p.Category, p.BaseUnit.ShortCode, p.Tax.Name, p.ReorderLevel, p.IsActive))
            .ToListAsync(ct);

        return Ok(new PagedResult<ProductListItemDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Inventory.View")]
    public async Task<ActionResult<ProductDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var product = await db.Products.Include(p => p.BaseUnit).Include(p => p.Tax).Include(p => p.SellingModes)
            .SingleOrDefaultAsync(p => p.Id == id, ct);
        return product is null ? NotFound() : Ok(ToDetail(product));
    }

    [HttpPost]
    [Authorize(Policy = "Inventory.Create")]
    public async Task<ActionResult<ProductDetailDto>> Create(UpsertProductRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!await ValidateReferencesAsync(request, ct)) return ValidationProblem(ModelState);

        if (await db.Products.AnyAsync(p => p.Sku.ToLower() == request.Sku.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Sku), "A product with this SKU already exists.");
            return ValidationProblem(ModelState);
        }

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Sku = request.Sku.Trim(),
            Name = request.Name.Trim(),
            Category = request.Category,
            BaseUnitId = request.BaseUnitId!.Value,
            TaxId = request.TaxId!.Value,
            ReorderLevel = request.ReorderLevel,
        };
        AttachSellingModes(product, request.SellingModes);

        db.Products.Add(product);
        await db.SaveChangesAsync(ct);

        await db.Entry(product).Reference(p => p.BaseUnit).LoadAsync(ct);
        await db.Entry(product).Reference(p => p.Tax).LoadAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = product.Id }, ToDetail(product));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Inventory.Edit")]
    public async Task<ActionResult<ProductDetailDto>> Update(Guid id, UpsertProductRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!await ValidateReferencesAsync(request, ct)) return ValidationProblem(ModelState);

        var product = await db.Products.Include(p => p.SellingModes).Include(p => p.BaseUnit).Include(p => p.Tax)
            .SingleOrDefaultAsync(p => p.Id == id, ct);
        if (product is null) return NotFound();

        if (await db.Products.AnyAsync(p => p.Id != id && p.Sku.ToLower() == request.Sku.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Sku), "A product with this SKU already exists.");
            return ValidationProblem(ModelState);
        }

        product.Sku = request.Sku.Trim();
        product.Name = request.Name.Trim();
        product.Category = request.Category;
        product.BaseUnitId = request.BaseUnitId!.Value;
        product.TaxId = request.TaxId!.Value;
        product.ReorderLevel = request.ReorderLevel;

        product.SellingModes.Clear();
        AttachSellingModes(product, request.SellingModes);

        await db.SaveChangesAsync(ct);

        if (product.BaseUnitId != product.BaseUnit.Id) await db.Entry(product).Reference(p => p.BaseUnit).LoadAsync(ct);
        if (product.TaxId != product.Tax.Id) await db.Entry(product).Reference(p => p.Tax).LoadAsync(ct);
        return Ok(ToDetail(product));
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Inventory.Edit")]
    public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (product is null) return NotFound();

        product.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<bool> ValidateReferencesAsync(UpsertProductRequest request, CancellationToken ct)
    {
        if (!await db.Units.AnyAsync(u => u.Id == request.BaseUnitId, ct))
        {
            ModelState.AddModelError(nameof(request.BaseUnitId), "Select a valid base unit.");
        }
        if (!await db.Taxes.AnyAsync(t => t.Id == request.TaxId, ct))
        {
            ModelState.AddModelError(nameof(request.TaxId), "Select a valid tax.");
        }
        return ModelState.IsValid;
    }

    private static void AttachSellingModes(Product product, IEnumerable<SellingModeInput> modes)
    {
        foreach (var mode in modes)
        {
            product.SellingModes.Add(new ProductSellingMode
            {
                Id = Guid.NewGuid(),
                ProductId = product.Id,
                Label = mode.Label.Trim(),
                ConversionFactorToBase = mode.ConversionFactorToBase,
            });
        }
    }

    private static ProductDetailDto ToDetail(Product p) => new(
        p.Id, p.Sku, p.Name, p.Category, p.BaseUnitId, p.BaseUnit.ShortCode, p.TaxId, p.Tax.Name,
        p.ReorderLevel, p.IsActive, p.SellingModes.Select(m => new SellingModeDto(m.Id, m.Label, m.ConversionFactorToBase, m.IsActive)).ToList());
}
