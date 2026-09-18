using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/products")]
public class ProductsController(SelloraDbContext db, IProductService productService) : ControllerBase
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

        try
        {
            var product = await productService.CreateProductAsync(ToInput(request), ct);
            return CreatedAtAction(nameof(Get), new { id = product.Id }, ToDetail(product));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Inventory.Edit")]
    public async Task<ActionResult<ProductDetailDto>> Update(Guid id, UpsertProductRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var product = await productService.UpdateProductAsync(id, ToInput(request), ct);
            return product is null ? NotFound() : Ok(ToDetail(product));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
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

    private static UpsertProductInput ToInput(UpsertProductRequest request) => new(
        request.Sku, request.Name, request.Category, request.BaseUnitId!.Value, request.TaxId!.Value, request.ReorderLevel,
        request.SellingModes.Select(m => new SellingModeInputItem(m.Id, m.Label, m.ConversionFactorToBase)).ToList());

    private static ProductDetailDto ToDetail(Product p) => new(
        p.Id, p.Sku, p.Name, p.Category, p.BaseUnitId, p.BaseUnit.ShortCode, p.TaxId, p.Tax.Name, p.Tax.RatePercent,
        p.ReorderLevel, p.IsActive, p.SellingModes.Select(m => new SellingModeDto(m.Id, m.Label, m.ConversionFactorToBase, m.IsActive)).ToList());
}
