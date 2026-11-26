using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// The tax lookup (BR5, BR8, BR9, BR11): product, subcategory, category, company default for one country. Reads only, no locks.
/// </summary>
public class TaxResolver(SelloraDbContext db) : ITaxResolver
{
    private sealed record Facts(Guid? ProductId, string Name, Guid? CategoryId, Guid? SubcategoryId, string? Hsn, Guid? SalesOverride, Guid? PurchaseOverride, bool UseStoredAssignment);

    public async Task<TaxResolution> ResolveAsync(Guid productId, Guid countryId, string field = "taxProfile", CancellationToken ct = default) =>
        (await ResolveManyAsync([productId], countryId, field, ct))[productId];

    public async Task<IReadOnlyDictionary<Guid, TaxResolution>> ResolveManyAsync(
        IReadOnlyCollection<Guid> productIds, Guid countryId, string field = "taxProfile", CancellationToken ct = default)
    {
        var ids = productIds.Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new Facts(p.Id, p.Name, p.CategoryId, p.SubcategoryId, p.HsnCode, null, null, true)).ToListAsync(ct);
        if (products.Count != ids.Length) throw new DomainValidationException(field, "One of the products does not exist.");

        var resolved = await ResolveCoreAsync(products, countryId, ct);
        var result = new Dictionary<Guid, TaxResolution>();
        foreach (var (facts, resolution) in products.Zip(resolved))
        {
            if (resolution is null)
            {
                var country = await db.Countries.AsNoTracking().Where(c => c.Id == countryId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "the document's country";
                throw new DomainValidationException(field,
                    $"{facts.Name} has no tax profile for {country}. Assign a profile to the product, its subcategory or category, or set a company default for {country}.");
            }
            result[facts.ProductId!.Value] = resolution;
        }
        return result;
    }

    public async Task<TaxResolution?> PreviewAsync(TaxPreviewInput input, CancellationToken ct = default)
    {
        if (input.PurchaseOverrideProfileId is not null && input.SalesOverrideProfileId is null)
        {
            throw new DomainValidationException("salesTaxProfileId", "Choose a sales profile to override the purchase profile.");
        }
        var facts = new Facts(input.ProductId, string.IsNullOrWhiteSpace(input.ProductName) ? "This product" : input.ProductName.Trim(),
            input.CategoryId, input.SubcategoryId, TaxRules.NormaliseHsn(input.HsnCode), input.SalesOverrideProfileId, input.PurchaseOverrideProfileId, false);
        return (await ResolveCoreAsync([facts], input.CountryId, ct))[0];
    }

    public async Task<Guid> ResolveDocumentCountryAsync(Guid? branchId, string field = "taxProfile", CancellationToken ct = default)
    {
        if (branchId is { } id)
        {
            var branch = await db.Branches.AsNoTracking().Where(b => b.Id == id).Select(b => new { b.Name, b.CountryId }).SingleOrDefaultAsync(ct)
                ?? throw new DomainValidationException(field, "The branch does not exist.");
            if (branch.CountryId is { } branchCountry) return branchCountry;
        }
        var company = await db.BusinessSettings.AsNoTracking().Select(s => s.CountryId).FirstOrDefaultAsync(ct);
        return company ?? throw new DomainValidationException(field, "Set the country of the branch or of the company in the business settings before billing.");
    }

    public async Task<TaxVersionResolution> ResolveVersionAsync(Guid profileId, DateOnly documentDate, string field = "taxProfile", CancellationToken ct = default)
    {
        var profile = await db.TaxProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == profileId, ct)
            ?? throw new DomainValidationException(field, "The tax profile does not exist.");
        if (!profile.IsActive) throw new DomainValidationException(field, $"{profile.Name} is inactive and cannot be used on new documents.");

        var version = await db.TaxProfileVersions.AsNoTracking().Include(v => v.Components).ThenInclude(c => c.TaxComponent)
            .Where(v => v.TaxProfileId == profileId && v.EffectiveFrom <= documentDate && (v.EffectiveTo == null || v.EffectiveTo >= documentDate))
            .SingleOrDefaultAsync(ct)
            ?? throw new DomainValidationException(field, $"{profile.Name} has no version valid on {documentDate:yyyy-MM-dd}.");
        return new TaxVersionResolution(profile, version);
    }

    private async Task<List<TaxResolution?>> ResolveCoreAsync(IReadOnlyList<Facts> items, Guid countryId, CancellationToken ct)
    {
        var index = await TaxAssignmentIndex.LoadAsync(db, countryId, ct);

        var picked = items.Select(f => (Facts: f, Pick: Pick(index, f))).ToList();
        var profileIds = picked.Where(x => x.Pick is not null).SelectMany(x => new[] { x.Pick!.Value.Sales, x.Pick!.Value.Purchase }).Distinct().ToArray();
        var profiles = await db.TaxProfiles.AsNoTracking().Where(p => profileIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        var subIds = items.Where(i => i.SubcategoryId != null).Select(i => i.SubcategoryId!.Value).Distinct().ToArray();
        var subs = await db.ProductSubcategories.AsNoTracking().Where(s => subIds.Contains(s.Id)).Select(s => new { s.Id, s.Name, s.HsnCode }).ToDictionaryAsync(s => s.Id, ct);
        var catIds = items.Where(i => i.CategoryId != null).Select(i => i.CategoryId!.Value).Distinct().ToArray();
        var categories = await db.ProductCategories.AsNoTracking().Where(c => catIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var results = new List<TaxResolution?>();
        foreach (var (facts, pick) in picked)
        {
            if (pick is not { } p || !profiles.TryGetValue(p.Sales, out var sales) || !profiles.TryGetValue(p.Purchase, out var purchase))
            {
                results.Add(null);
                continue;
            }

            var sourceName = p.Scope switch
            {
                TaxScope.Product => facts.Name,
                TaxScope.Subcategory => facts.SubcategoryId is { } s && subs.TryGetValue(s, out var sub) ? sub.Name : "Subcategory",
                TaxScope.Category => facts.CategoryId is { } c && categories.TryGetValue(c, out var cat) ? cat : "Category",
                _ => "Company default",
            };

            var subHsn = facts.SubcategoryId is { } sid && subs.TryGetValue(sid, out var subRow) ? subRow.HsnCode : null;
            var (hsn, hsnSource) = !string.IsNullOrEmpty(facts.Hsn) ? (facts.Hsn, TaxHsnSource.Product)
                : !string.IsNullOrEmpty(subHsn) ? (subHsn, TaxHsnSource.Subcategory) : (null, TaxHsnSource.None);

            var needsHsn = IsGstTaxable(sales) || IsGstTaxable(purchase);
            var warn = needsHsn && hsn is null;

            results.Add(new TaxResolution(
                facts.ProductId, countryId, ToResolved(sales), ToResolved(purchase), p.Sales == p.Purchase,
                p.Scope, sourceName, p.AssignmentId, hsn, hsnSource, warn,
                warn ? $"{facts.Name} has no HSN/SAC code. GST invoices need it." : null));
        }
        return results;
    }

    private static bool IsGstTaxable(TaxProfile p) => p.Regime == TaxRegime.Gst && p.Treatment == TaxTreatment.Taxable;

    private static ResolvedTaxProfile ToResolved(TaxProfile p) => new(p.Id, p.Code, p.Name, p.Regime, p.Treatment, p.IsActive, p.CountryId, p.LegacyTaxId);

    private static (Guid Sales, Guid Purchase, TaxScope Scope, Guid? AssignmentId)? Pick(TaxAssignmentIndex index, Facts f)
    {
        if (f.SalesOverride is { } sales) return (sales, f.PurchaseOverride ?? sales, TaxScope.Product, null);
        var a = index.Find(f.UseStoredAssignment ? f.ProductId : null, f.CategoryId, f.SubcategoryId);
        return a is null ? null : (a.SalesTaxProfileId, TaxAssignmentIndex.EffectivePurchase(a), a.Scope, a.Id);
    }
}
