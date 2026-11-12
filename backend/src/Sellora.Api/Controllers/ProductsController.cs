using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/products")]
public class ProductsController(SelloraDbContext db, IProductService productService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Inventory.View")]
    public async Task<ActionResult<PagedResult<ProductListItemDto>>> List(
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default,
        [FromQuery] Guid? categoryId = null, [FromQuery] Guid? subcategoryId = null, [FromQuery] string? category = null)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Products.Include(p => p.BaseUnit).Include(p => p.Tax).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(s) || p.Sku.ToLower().Contains(s) || (p.Category != null && p.Category.ToLower().Contains(s)));
        }

        if (categoryId is not null) query = query.Where(p => p.CategoryId == categoryId);
        if (subcategoryId is not null) query = query.Where(p => p.SubcategoryId == subcategoryId);
        // Legacy string filter, kept for one release (FI-D17): matches the category name.
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(p => p.Category == category);

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(p => p.Name).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new ProductListItemDto(p.Id, p.Sku, p.Name, p.Category,
                p.CategoryId, p.Category, p.SubcategoryId,
                db.ProductSubcategories.Where(s => s.Id == p.SubcategoryId).Select(s => s.Name).FirstOrDefault(),
                p.BaseUnit.ShortCode, p.Tax.Name, p.ReorderLevel, p.IsActive))
            .ToListAsync(ct);

        return Ok(new PagedResult<ProductListItemDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Inventory.View")]
    public async Task<ActionResult<ProductDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var detail = await LoadDetailAsync(id, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpPost]
    [Authorize(Policy = "Inventory.Create")]
    public async Task<ActionResult<ProductDetailDto>> Create(UpsertProductRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var product = await productService.CreateProductAsync(ToInput(request), ct);
            return CreatedAtAction(nameof(Get), new { id = product.Id }, await LoadDetailAsync(product.Id, ct));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
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
            return product is null ? NotFound() : Ok(await LoadDetailAsync(product.Id, ct));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    /// <summary>Validates a unit hierarchy exactly as save would, without saving. Stateless (no locks, nothing
    /// persisted), so it only needs view access. 200 even when the configuration is invalid; 400 only for a malformed
    /// request; 404 when <c>ProductId</c> is given but unknown.</summary>
    [HttpPost("unit-preview")]
    [Authorize(Policy = "Inventory.View")]
    public async Task<ActionResult<UnitPreviewResponse>> PreviewUnits(UnitPreviewRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var preview = await productService.PreviewUnitsAsync(request.ProductId, request.BaseUnitId!.Value, ToUnitInputs(request.Units), ct);
        if (preview is null) return NotFound();

        return Ok(new UnitPreviewResponse(
            preview.IsValid,
            preview.Rows.Select(r => new UnitPreviewRowDto(r.Index, r.UnitId, r.UnitCode, r.DisplayName, r.ConversionFactorToBase,
                r.Depth, r.SortOrder, r.Chain, r.IsLocked)).ToList(),
            preview.Errors.Select(e => new FieldErrorDto(e.Field, e.Message)).ToList(),
            preview.Warnings.Select(e => new FieldErrorDto(e.Field, e.Message)).ToList()));
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

    private ActionResult ToValidationProblem(DomainValidationException ex)
    {
        foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
        return ValidationProblem(ModelState);
    }

    private static UpsertProductInput ToInput(UpsertProductRequest request) => new(
        request.Sku, request.Name, request.CategoryId, request.SubcategoryId, request.BaseUnitId!.Value, request.TaxId!.Value, request.ReorderLevel,
        ToUnitInputs(request.Units));

    private static List<ProductUnitInputItem> ToUnitInputs(IEnumerable<UpsertProductUnitRequest> units) => units
        .Select(u => new ProductUnitInputItem(u.Id, u.UnitId!.Value, u.InnerUnitId, u.InnerQuantity, u.Label, u.Barcode,
            u.IsPurchasable, u.IsSellable, u.IsDefaultPurchaseUnit, u.IsDefaultSalesUnit, u.IsActive))
        .ToList();

    private async Task<ProductDetailDto?> LoadDetailAsync(Guid id, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking()
            .Include(p => p.BaseUnit).Include(p => p.Tax).Include(p => p.Units).ThenInclude(u => u.Unit)
            .SingleOrDefaultAsync(p => p.Id == id, ct);
        if (product is null) return null;

        var units = product.Units.ToList();
        var locks = await ProductUnitUsage.GetLockReasonsAsync(db, units, ct);
        var isBaseUnitLocked = await ProductUnitUsage.IsBaseUnitLockedAsync(db, product.Id, locks, ct);
        var graph = ProductUnitGraph.Compute(units.Select(u => new ProductUnitGraph.Node(u.Id, u.IsBaseUnit ? null : u.InnerProductUnitId, u.InnerQuantity)));
        var byId = units.ToDictionary(u => u.Id);

        var dtos = units
            .OrderBy(u => u.SortOrder).ThenByDescending(u => u.IsBaseUnit).ThenBy(u => u.ConversionFactorToBase)
            .Select(u => new ProductUnitDto(
                u.Id, u.UnitId, u.Unit.ShortCode, u.Unit.Name, u.Unit.MeasurementCategory, u.Unit.DecimalPlaces,
                u.InnerProductUnitId, u.InnerProductUnitId is { } inner && byId.TryGetValue(inner, out var innerRow) ? innerRow.UnitId : null,
                u.InnerQuantity, u.ConversionFactorToBase, u.IsBaseUnit,
                u.Label, u.Label ?? u.Unit.Name, u.Barcode, u.IsPurchasable, u.IsSellable,
                u.IsDefaultPurchaseUnit, u.IsDefaultSalesUnit, u.SortOrder, graph.TryGetValue(u.Id, out var r) ? r.Depth ?? 0 : 0,
                u.IsActive, locks.ContainsKey(u.Id), locks.GetValueOrDefault(u.Id)))
            .ToList();

        var subcategoryName = product.SubcategoryId is { } subId
            ? await db.ProductSubcategories.Where(s => s.Id == subId).Select(s => s.Name).FirstOrDefaultAsync(ct)
            : null;

        return new ProductDetailDto(
            product.Id, product.Sku, product.Name, product.Category,
            product.CategoryId, product.Category, product.SubcategoryId, subcategoryName, product.BaseUnitId, product.BaseUnit.ShortCode,
            product.TaxId, product.Tax.Name, product.Tax.RatePercent, product.ReorderLevel, product.IsActive,
            isBaseUnitLocked, dtos);
    }
}
