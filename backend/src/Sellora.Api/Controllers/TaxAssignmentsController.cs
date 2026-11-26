using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>Assignment of tax profiles to the company, categories, subcategories and products (BR7–BR10). Reads need <c>Tax.View</c>, writes <c>Tax.Manage</c>.</summary>
[ApiController]
[Route("api/v1/tax-assignments")]
[Authorize(Policy = "Tax.View")]
public class TaxAssignmentsController(SelloraDbContext db, ITaxAssignmentService service, ITaxResolver resolver) : ControllerBase
{
    /// <summary>The mapping tree of one country (default: the company's): company default, then category, subcategory and product overrides, each with the profile it
    /// resolves to and where that comes from. <c>includeOverrides=false</c> returns counts only (a migrated company has one override per product).</summary>
    [HttpGet]
    public async Task<ActionResult<TaxAssignmentTreeDto>> Tree([FromQuery] Guid? countryId, [FromQuery] bool includeOverrides = true, CancellationToken ct = default)
    {
        var country = await ResolveCountryAsync(countryId, ct);
        if (country is null) return CountryProblem(countryId);

        var assignments = await db.TaxAssignments.AsNoTracking().Where(a => a.CountryId == country.Id).ToListAsync(ct);
        var profiles = (await db.TaxProfiles.AsNoTracking().Where(p => p.CountryId == country.Id).ToListAsync(ct)).ToDictionary(p => p.Id);

        var categories = await db.ProductCategories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var subcategories = await db.ProductSubcategories.AsNoTracking().OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync(ct);
        var categoryCounts = await db.Products.AsNoTracking().Where(p => p.CategoryId != null).GroupBy(p => p.CategoryId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var subCounts = await db.Products.AsNoTracking().Where(p => p.SubcategoryId != null).GroupBy(p => p.SubcategoryId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

        var productIds = assignments.Where(a => a.Scope == TaxScope.Product && a.ProductId != null).Select(a => a.ProductId!.Value).ToList();
        var products = new Dictionary<Guid, (string Sku, string Name, Guid? CategoryId, Guid? SubcategoryId)>();
        foreach (var chunk in productIds.Chunk(1000))
        {
            foreach (var p in await db.Products.AsNoTracking().Where(x => chunk.Contains(x.Id)).Select(x => new { x.Id, x.Sku, x.Name, x.CategoryId, x.SubcategoryId }).ToListAsync(ct))
            {
                products[p.Id] = (p.Sku, p.Name, p.CategoryId, p.SubcategoryId);
            }
        }

        var company = assignments.FirstOrDefault(a => a.Scope == TaxScope.Company);
        var byCategory = assignments.Where(a => a.Scope == TaxScope.Category && a.CategoryId != null).ToDictionary(a => a.CategoryId!.Value);
        var bySub = assignments.Where(a => a.Scope == TaxScope.Subcategory && a.SubcategoryId != null).ToDictionary(a => a.SubcategoryId!.Value);

        string Name(TaxAssignment a) => a.Scope switch
        {
            TaxScope.Company => "Company default",
            TaxScope.Category => categories.FirstOrDefault(c => c.Id == a.CategoryId)?.Name ?? "Category",
            TaxScope.Subcategory => subcategories.FirstOrDefault(s => s.Id == a.SubcategoryId)?.Name ?? "Subcategory",
            _ => a.ProductId is { } id && products.TryGetValue(id, out var p) ? p.Name : "Product",
        };
        TaxAssignmentDto? Dto(TaxAssignment? a) => a is null || !profiles.ContainsKey(a.SalesTaxProfileId) ? null : ToDto(a, Name(a), profiles);
        TaxEffectiveDto? Effective(TaxAssignment? a, string sourceName) => a is null || !profiles.ContainsKey(a.SalesTaxProfileId) ? null
            : new TaxEffectiveDto(Ref(profiles[a.SalesTaxProfileId]), Ref(profiles[TaxPurchaseId(a)]), a.Scope, a.Scope == TaxScope.Company ? "Company default" : sourceName);

        var overrides = assignments.Where(a => a.Scope == TaxScope.Product && a.ProductId is { } id && products.ContainsKey(id) && profiles.ContainsKey(a.SalesTaxProfileId))
            .Select(a => (Assignment: a, Product: products[a.ProductId!.Value]))
            .OrderBy(x => x.Product.Name, StringComparer.OrdinalIgnoreCase).ToList();
        TaxTreeProductDto ToTree((TaxAssignment Assignment, (string Sku, string Name, Guid? CategoryId, Guid? SubcategoryId) Product) x) =>
            new(x.Assignment.ProductId!.Value, x.Product.Sku, x.Product.Name, ToDto(x.Assignment, x.Product.Name, profiles));

        var knownSubs = subcategories.Select(s => s.Id).ToHashSet();
        var knownCategories = categories.Select(c => c.Id).ToHashSet();
        var tree = categories.Select(c =>
        {
            byCategory.TryGetValue(c.Id, out var own);
            var catEffective = own ?? company;
            var subDtos = subcategories.Where(s => s.CategoryId == c.Id).Select(s =>
            {
                bySub.TryGetValue(s.Id, out var subOwn);
                var subOverrides = overrides.Where(x => x.Product.SubcategoryId == s.Id).ToList();
                var effective = subOwn ?? own ?? company;
                return new TaxTreeSubcategoryDto(s.Id, s.Code, s.Name, s.HsnCode, s.IsActive, subCounts.GetValueOrDefault(s.Id), subOverrides.Count,
                    Dto(subOwn), Effective(effective, effective is null ? "" : effective.Scope == TaxScope.Subcategory ? s.Name : effective.Scope == TaxScope.Category ? c.Name : ""),
                    includeOverrides ? subOverrides.Select(ToTree).ToList() : []);
            }).ToList();
            var catOverrides = overrides.Where(x => x.Product.CategoryId == c.Id && (x.Product.SubcategoryId is null || !knownSubs.Contains(x.Product.SubcategoryId.Value))).ToList();
            return new TaxTreeCategoryDto(c.Id, c.Code, c.Name, c.IsActive, categoryCounts.GetValueOrDefault(c.Id),
                overrides.Count(x => x.Product.CategoryId == c.Id), Dto(own), Effective(catEffective, c.Name), subDtos,
                includeOverrides ? catOverrides.Select(ToTree).ToList() : []);
        }).ToList();

        var uncategorised = overrides.Where(x => x.Product.CategoryId is null || !knownCategories.Contains(x.Product.CategoryId.Value)).ToList();
        return Ok(new TaxAssignmentTreeDto(country.Id, country.Code, country.Name, Dto(company), tree, uncategorised.Count,
            includeOverrides ? uncategorised.Select(ToTree).ToList() : []));
    }

    /// <summary>What a product resolves to for a country (BR8, BR9): the stored product, or a product as it would be saved on the form (category, subcategory, HSN and optional override profiles).
    /// 200 with <c>resolved: false</c> when no profile applies.</summary>
    [HttpGet("resolve")]
    public async Task<ActionResult<TaxResolutionDto>> Resolve(
        [FromQuery] Guid? countryId, [FromQuery] Guid? productId, [FromQuery] Guid? categoryId, [FromQuery] Guid? subcategoryId, [FromQuery] string? hsnCode,
        [FromQuery] Guid? salesProfileId, [FromQuery] Guid? purchaseProfileId, CancellationToken ct)
    {
        var country = await ResolveCountryAsync(countryId, ct);
        if (country is null) return CountryProblem(countryId);

        try
        {
            string? name = null;
            if (productId is { } pid)
            {
                var stored = await db.Products.AsNoTracking().Where(p => p.Id == pid).Select(p => new { p.Name, p.CategoryId, p.SubcategoryId, p.HsnCode }).SingleOrDefaultAsync(ct);
                if (stored is null) return NotFound();
                name = stored.Name;
                // Query values override the stored product, so the form can preview unsaved edits.
                categoryId ??= stored.CategoryId;
                subcategoryId ??= stored.SubcategoryId;
                hsnCode ??= stored.HsnCode;
                if (salesProfileId is null)
                {
                    var own = await db.TaxAssignments.AsNoTracking().Where(a => a.Scope == TaxScope.Product && a.ProductId == pid && a.CountryId == country.Id)
                        .Select(a => new { a.SalesTaxProfileId, a.PurchaseTaxProfileId }).SingleOrDefaultAsync(ct);
                    salesProfileId = own?.SalesTaxProfileId;
                    purchaseProfileId ??= own?.PurchaseTaxProfileId;
                }
            }

            var r = await resolver.PreviewAsync(new TaxPreviewInput(country.Id, productId, name, categoryId, subcategoryId, hsnCode, salesProfileId, purchaseProfileId), ct);
            if (r is null) return Ok(new TaxResolutionDto(false, country.Id, null, null, true, null, null, null, TaxHsnSource.None.ToString(), false, null));
            return Ok(new TaxResolutionDto(true, country.Id, ToRef(r.SalesProfile), ToRef(r.PurchaseProfile), r.PurchaseSameAsSales, r.Source, r.SourceName,
                r.HsnCode, r.HsnSource.ToString(), r.HsnWarning, r.HsnWarningMessage));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    [HttpPut]
    [Authorize(Policy = "Tax.Manage")]
    public async Task<ActionResult<TaxAssignmentDto>> Upsert(UpsertTaxAssignmentRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var a = await service.UpsertAsync(new UpsertTaxAssignmentInput(request.ScopeType!.Value, request.ScopeId, request.CountryId!.Value, request.SalesProfileId!.Value, request.PurchaseProfileId), ct);
            return Ok(await LoadDtoAsync(a, ct));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    /// <summary>The company default cannot be deleted (400 <c>id</c>).</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "Tax.Manage")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try
        {
            return await service.DeleteAsync(id, ct) ? NoContent() : NotFound();
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    /// <summary>T20: removes the product overrides of a country that equal what the product inherits. Returns how many were cleared.</summary>
    [HttpPost("clear-redundant-overrides")]
    [Authorize(Policy = "Tax.Manage")]
    public async Task<ActionResult<ClearRedundantOverridesResultDto>> ClearRedundantOverrides(ClearRedundantOverridesRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            return Ok(new ClearRedundantOverridesResultDto(await service.ClearRedundantOverridesAsync(request.CountryId!.Value, ct)));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Country?> ResolveCountryAsync(Guid? countryId, CancellationToken ct)
    {
        var id = countryId ?? await db.BusinessSettings.AsNoTracking().Select(s => s.CountryId).FirstOrDefaultAsync(ct);
        return id is null ? null : await db.Countries.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
    }

    private ActionResult CountryProblem(Guid? countryId)
    {
        ModelState.AddModelError("countryId", countryId is null ? "Choose a country or set the company's country in the business settings." : "Select a valid country.");
        return ValidationProblem(ModelState);
    }

    private static Guid TaxPurchaseId(TaxAssignment a) => a.PurchaseTaxProfileId ?? a.SalesTaxProfileId;

    private static TaxProfileRefDto Ref(TaxProfile p) => new(p.Id, p.Code, p.Name, p.Regime, p.Treatment, p.IsActive);

    private static TaxProfileRefDto ToRef(ResolvedTaxProfile p) => new(p.Id, p.Code, p.Name, p.Regime, p.Treatment, p.IsActive);

    private static TaxAssignmentDto ToDto(TaxAssignment a, string scopeName, IReadOnlyDictionary<Guid, TaxProfile> profiles) =>
        new(a.Id, a.Scope, a.Scope switch { TaxScope.Category => a.CategoryId, TaxScope.Subcategory => a.SubcategoryId, TaxScope.Product => a.ProductId, _ => null },
            scopeName, a.CountryId, Ref(profiles[a.SalesTaxProfileId]), a.PurchaseTaxProfileId is { } p ? Ref(profiles[p]) : null);

    private async Task<TaxAssignmentDto> LoadDtoAsync(TaxAssignment a, CancellationToken ct)
    {
        var ids = new[] { a.SalesTaxProfileId, a.PurchaseTaxProfileId ?? a.SalesTaxProfileId };
        var profiles = await db.TaxProfiles.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var name = a.Scope switch
        {
            TaxScope.Company => "Company default",
            TaxScope.Category => await db.ProductCategories.AsNoTracking().Where(c => c.Id == a.CategoryId).Select(c => c.Name).SingleOrDefaultAsync(ct),
            TaxScope.Subcategory => await db.ProductSubcategories.AsNoTracking().Where(s => s.Id == a.SubcategoryId).Select(s => s.Name).SingleOrDefaultAsync(ct),
            _ => await db.Products.AsNoTracking().Where(p => p.Id == a.ProductId).Select(p => p.Name).SingleOrDefaultAsync(ct),
        };
        return ToDto(a, name ?? a.Scope.ToString(), profiles);
    }

    private ActionResult ToValidationProblem(DomainValidationException ex)
    {
        foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
        return ValidationProblem(ModelState);
    }
}
