using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>Phase 08 (FI-D16): product category and subcategory masters. Reads are open to any authenticated company member
/// (pickers on product, stock and POS screens); writes need <c>Inventory.Categories</c>. Deactivate only, never delete.</summary>
[ApiController]
[Route("api/v1/product-categories")]
[Authorize]
public class ProductCategoriesController(SelloraDbContext db, IProductCategoryService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProductCategoryDto>>> List([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var categories = await db.ProductCategories.AsNoTracking()
            .Where(c => includeInactive || c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new { c.Id, c.Code, c.Name, c.SortOrder, c.IsActive, ProductCount = db.Products.Count(p => p.CategoryId == c.Id) })
            .ToListAsync(ct);

        var subs = await db.ProductSubcategories.AsNoTracking()
            .Where(s => includeInactive || s.IsActive)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new ProductSubcategoryDto(s.Id, s.CategoryId, s.Code, s.Name, s.SortOrder, s.IsActive,
                db.Products.Count(p => p.SubcategoryId == s.Id)))
            .ToListAsync(ct);
        var subsByCategory = subs.ToLookup(s => s.CategoryId);

        return Ok(categories.Select(c => new ProductCategoryDto(
            c.Id, c.Code, c.Name, c.SortOrder, c.IsActive, c.ProductCount, subsByCategory[c.Id].ToList())).ToList());
    }

    [HttpPost]
    [Authorize(Policy = "Inventory.Categories")]
    public async Task<ActionResult<ProductCategoryDto>> Create(UpsertProductClassificationRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var category = await service.CreateCategoryAsync(ToInput(request), ct);
            var dto = (await LoadCategoryAsync(category.Id, ct))!;
            return Created($"api/v1/product-categories/{category.Id}", dto);
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Inventory.Categories")]
    public async Task<ActionResult<ProductCategoryDto>> Update(Guid id, UpsertProductClassificationRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var category = await service.UpdateCategoryAsync(id, ToInput(request), ct);
            return category is null ? NotFound() : Ok(await LoadCategoryAsync(id, ct));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Inventory.Categories")]
    public async Task<ActionResult<ProductCategoryDto>> SetActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        var category = await service.SetCategoryActiveAsync(id, request.IsActive, ct);
        return category is null ? NotFound() : Ok(await LoadCategoryAsync(id, ct));
    }

    [HttpPost("{id:guid}/subcategories")]
    [Authorize(Policy = "Inventory.Categories")]
    public async Task<ActionResult<ProductSubcategoryDto>> CreateSubcategory(Guid id, UpsertProductClassificationRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var sub = await service.CreateSubcategoryAsync(id, ToInput(request), ct);
            if (sub is null) return NotFound();
            return Created($"api/v1/product-categories/{id}/subcategories/{sub.Id}", await LoadSubcategoryAsync(id, sub.Id, ct));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    [HttpPut("{id:guid}/subcategories/{subId:guid}")]
    [Authorize(Policy = "Inventory.Categories")]
    public async Task<ActionResult<ProductSubcategoryDto>> UpdateSubcategory(Guid id, Guid subId, UpsertProductClassificationRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var sub = await service.UpdateSubcategoryAsync(id, subId, ToInput(request), ct);
            return sub is null ? NotFound() : Ok(await LoadSubcategoryAsync(id, subId, ct));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    [HttpPost("{id:guid}/subcategories/{subId:guid}/status")]
    [Authorize(Policy = "Inventory.Categories")]
    public async Task<ActionResult<ProductSubcategoryDto>> SetSubcategoryActive(Guid id, Guid subId, SetActiveRequest request, CancellationToken ct)
    {
        try
        {
            var sub = await service.SetSubcategoryActiveAsync(id, subId, request.IsActive, ct);
            return sub is null ? NotFound() : Ok(await LoadSubcategoryAsync(id, subId, ct));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    private ActionResult ToValidationProblem(DomainValidationException ex)
    {
        foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
        return ValidationProblem(ModelState);
    }

    private static UpsertProductClassificationInput ToInput(UpsertProductClassificationRequest r) =>
        new(r.Code ?? string.Empty, r.Name ?? string.Empty, r.SortOrder);

    private Task<ProductSubcategoryDto?> LoadSubcategoryAsync(Guid categoryId, Guid subId, CancellationToken ct) =>
        db.ProductSubcategories.AsNoTracking().Where(s => s.Id == subId && s.CategoryId == categoryId)
            .Select(s => new ProductSubcategoryDto(s.Id, s.CategoryId, s.Code, s.Name, s.SortOrder, s.IsActive,
                db.Products.Count(p => p.SubcategoryId == s.Id)))
            .SingleOrDefaultAsync(ct);

    private async Task<ProductCategoryDto?> LoadCategoryAsync(Guid id, CancellationToken ct)
    {
        var c = await db.ProductCategories.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Code, x.Name, x.SortOrder, x.IsActive, ProductCount = db.Products.Count(p => p.CategoryId == x.Id) })
            .SingleOrDefaultAsync(ct);
        if (c is null) return null;

        var subs = await db.ProductSubcategories.AsNoTracking().Where(s => s.CategoryId == id)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new ProductSubcategoryDto(s.Id, s.CategoryId, s.Code, s.Name, s.SortOrder, s.IsActive,
                db.Products.Count(p => p.SubcategoryId == s.Id)))
            .ToListAsync(ct);
        return new ProductCategoryDto(c.Id, c.Code, c.Name, c.SortOrder, c.IsActive, c.ProductCount, subs);
    }
}
