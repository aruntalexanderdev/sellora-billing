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

    public async Task<PlaceOfSupplyResolution> ResolvePlaceOfSupplyAsync(TaxDirection direction, Guid? branchId, Guid? partyId, CancellationToken ct = default)
    {
        var countryId = await ResolveDocumentCountryAsync(branchId, "branchId", ct);
        var countryCode = await db.Countries.AsNoTracking().Where(c => c.Id == countryId).Select(c => c.Code).SingleAsync(ct);
        var regime = TaxRules.RegimeFor(countryCode);
        if (regime == TaxRegime.Vat) return new PlaceOfSupplyResolution(PlaceOfSupply.NotApplicable, regime, countryId, countryCode, []);

        // Own state: the branch's, else the company's (T14).
        Guid? ownState = null;
        if (branchId is { } id) ownState = await db.Branches.AsNoTracking().Where(b => b.Id == id).Select(b => b.StateId).SingleOrDefaultAsync(ct);
        ownState ??= await db.BusinessSettings.AsNoTracking().Select(s => s.StateId).FirstOrDefaultAsync(ct);
        if (ownState is null)
        {
            throw new DomainValidationException("branchId", "Set the state of the branch (or of the company in the business settings) before billing GST documents.");
        }

        var warnings = new List<string>();
        (Guid? Country, Guid? State)? party = null;
        if (partyId is { } pid)
        {
            var found = direction == TaxDirection.Sale
                ? await db.Customers.AsNoTracking().Where(c => c.Id == pid).Select(c => new { c.CountryId, c.StateId, c.IsWalkIn }).SingleOrDefaultAsync(ct)
                : await db.Suppliers.AsNoTracking().Where(x => x.Id == pid).Select(x => new { x.CountryId, x.StateId, IsWalkIn = false }).SingleOrDefaultAsync(ct);
            // TP-4 (TP4-Q3): a walk-in customer is always intra-state, whatever state is stored on it (so the preview and the save agree).
            party = found is null ? null : (found.CountryId, found.StateId);
            if (party is null) throw new DomainValidationException("partyId", direction == TaxDirection.Sale ? "Select a valid customer." : "Select a valid supplier.");
            if (found!.IsWalkIn) party = null;
        }

        PlaceOfSupply place;
        if (party is not { } p) place = PlaceOfSupply.IntraState; // walk-in
        else if (p.Country is { } partyCountry && partyCountry != countryId) place = PlaceOfSupply.InterState; // DQ-TP2-7
        else if (p.State is null)
        {
            place = PlaceOfSupply.IntraState;
            if (direction == TaxDirection.Purchase) warnings.Add("The supplier has no state, so the purchase is treated as intra-state. Set the supplier's state.");
        }
        else place = p.State == ownState ? PlaceOfSupply.IntraState : PlaceOfSupply.InterState;
        return new PlaceOfSupplyResolution(place, regime, countryId, countryCode, warnings, ownState, party?.State);
    }

    public async Task<IReadOnlyDictionary<Guid, ResolvedLineRates>> ResolveRateSetsAsync(
        TaxDirection direction, Guid countryId, DateOnly date, IReadOnlyList<Guid> productIds, string field = "lines", CancellationToken ct = default)
    {
        var errors = new List<FieldError>();
        string Key(int i) => $"{field}[{i}].productId";
        var ids = productIds.Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new Facts(p.Id, p.Name, p.CategoryId, p.SubcategoryId, p.HsnCode, null, null, true)).ToListAsync(ct);
        var known = products.Select(p => p.ProductId!.Value).ToHashSet();
        for (var i = 0; i < productIds.Count; i++)
        {
            if (!known.Contains(productIds[i])) errors.Add(new FieldError(Key(i), "Select a valid product."));
        }

        var resolved = await ResolveCoreAsync(products, countryId, ct);
        var country = errors.Count == 0 && resolved.Any(r => r is null)
            ? await db.Countries.AsNoTracking().Where(c => c.Id == countryId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "the document's country"
            : "the document's country";

        var picked = new Dictionary<Guid, (TaxResolution Resolution, ResolvedTaxProfile Profile)>();
        foreach (var (facts, resolution) in products.Zip(resolved))
        {
            var productId = facts.ProductId!.Value;
            if (resolution is null)
            {
                for (var i = 0; i < productIds.Count; i++)
                {
                    if (productIds[i] == productId) errors.Add(new FieldError(Key(i), $"{facts.Name} has no tax profile for {country}. Assign a profile to the product, its subcategory or category, or set a company default for {country}."));
                }
                continue;
            }
            picked[productId] = (resolution, direction == TaxDirection.Sale ? resolution.SalesProfile : resolution.PurchaseProfile);
        }

        var profileIds = picked.Values.Select(v => v.Profile.Id).Distinct().ToArray();
        var versions = await db.TaxProfileVersions.AsNoTracking().Include(v => v.Components).ThenInclude(c => c.TaxComponent)
            .Where(v => profileIds.Contains(v.TaxProfileId) && v.EffectiveFrom <= date && (v.EffectiveTo == null || v.EffectiveTo >= date))
            .ToListAsync(ct);
        var versionByProfile = versions.GroupBy(v => v.TaxProfileId).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.EffectiveFrom).First());

        var result = new Dictionary<Guid, ResolvedLineRates>();
        foreach (var (productId, (resolution, profile)) in picked)
        {
            string? problem = null;
            if (!profile.IsActive) problem = $"{profile.Name} is inactive and cannot be used on new documents.";
            else if (!versionByProfile.TryGetValue(profile.Id, out _)) problem = $"{profile.Name} has no version valid on {date:yyyy-MM-dd}.";
            if (problem is not null)
            {
                for (var i = 0; i < productIds.Count; i++)
                {
                    if (productIds[i] == productId) errors.Add(new FieldError(Key(i), problem));
                }
                continue;
            }

            var version = versionByProfile[profile.Id];
            var rates = new TaxRateSet(
                profile.Id, version.Id, profile.Regime, profile.Treatment,
                version.Components.OrderBy(c => c.SortOrder)
                    .Select(c => new TaxRateComponent(c.TaxComponentId, c.TaxComponent.Code, c.TaxComponent.Kind, c.Method, c.Applicability, c.RatePercent, c.AmountPerBaseUnit, c.SortOrder))
                    .ToList());
            result[productId] = new ResolvedLineRates(
                productId, rates, profile.Code, profile.Name, version.EffectiveFrom, resolution.Source, resolution.SourceName,
                resolution.HsnCode, resolution.HsnSource, resolution.HsnWarning, resolution.HsnWarningMessage, profile.MirrorTaxId);
        }

        if (errors.Count > 0) throw new DomainValidationException(errors.OrderBy(e => LineIndex(e.Field)).ToList());
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> ResolveCurrentSalesPercentsAsync(
        Guid countryId, DateOnly date, IReadOnlyCollection<Guid> productIds, CancellationToken ct = default) =>
        (await ResolveCurrentSalesProfilesAsync(countryId, date, productIds, ct))
            .Where(kv => kv.Value.Percent is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Percent!.Value);

    public async Task<IReadOnlyDictionary<Guid, CurrentSalesTax>> ResolveCurrentSalesProfilesAsync(
        Guid countryId, DateOnly date, IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        var ids = productIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, CurrentSalesTax>();
        var index = await TaxAssignmentIndex.LoadAsync(db, countryId, ct);

        var products = new List<(Guid Id, Guid? CategoryId, Guid? SubcategoryId)>();
        foreach (var chunk in ids.Chunk(2000))
        {
            products.AddRange((await db.Products.AsNoTracking().Where(p => chunk.Contains(p.Id)).Select(p => new { p.Id, p.CategoryId, p.SubcategoryId }).ToListAsync(ct))
                .Select(p => (p.Id, p.CategoryId, p.SubcategoryId)));
        }
        var profileByProduct = new Dictionary<Guid, Guid>();
        foreach (var p in products)
        {
            if (index.Find(p.Id, p.CategoryId, p.SubcategoryId) is { } a) profileByProduct[p.Id] = a.SalesTaxProfileId;
        }

        var profileIds = profileByProduct.Values.Distinct().ToArray();
        var names = await db.TaxProfiles.AsNoTracking().Where(p => profileIds.Contains(p.Id)).Select(p => new { p.Id, p.Name }).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var versions = await db.TaxProfileVersions.AsNoTracking()
            .Where(v => profileIds.Contains(v.TaxProfileId) && v.EffectiveFrom <= date && (v.EffectiveTo == null || v.EffectiveTo >= date))
            .Select(v => new { v.TaxProfileId, v.EffectiveFrom, v.TotalPercent }).ToListAsync(ct);
        var percentByProfile = versions.GroupBy(v => v.TaxProfileId).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.EffectiveFrom).First().TotalPercent);

        return profileByProduct.Where(kv => names.ContainsKey(kv.Value)).ToDictionary(
            kv => kv.Key,
            kv => new CurrentSalesTax(kv.Value, names[kv.Value], percentByProfile.TryGetValue(kv.Value, out var percent) ? percent : null));
    }

    private static int LineIndex(string key)
    {
        var open = key.IndexOf('[');
        var close = key.IndexOf(']');
        return open >= 0 && close > open && int.TryParse(key.AsSpan(open + 1, close - open - 1), out var i) ? i : int.MaxValue;
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
