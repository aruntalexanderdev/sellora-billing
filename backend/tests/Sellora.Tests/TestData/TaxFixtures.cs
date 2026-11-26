using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Tests.TestData;

/// <summary>
/// Tax setup for fixtures that save products through <c>ProductService</c> (TP-1: a product needs a company country and a resolvable tax profile).
/// <see cref="UseAsCompanyDefaultAsync"/> turns a fixture's <see cref="Tax"/> into the mirror of an Indian GST profile that is the company default, so
/// products created by the service get <c>TaxId = tax.Id</c> and every rate assertion of the fixture stays valid.
/// </summary>
public static class TaxFixtures
{
    public static async Task<Country> EnsureCountryAsync(SelloraDbContext db, string code = "IN", string name = "India")
    {
        var country = await db.Countries.FirstOrDefaultAsync(c => c.Code == code) ?? db.Countries.Local.FirstOrDefault(c => c.Code == code);
        if (country is null)
        {
            country = new Country { Id = Guid.NewGuid(), Code = code, Name = name, IsSystem = true };
            db.Countries.Add(country);
            await db.SaveChangesAsync();
        }
        return country;
    }

    /// <summary>Sets the company country (creating the business settings when the fixture has none).</summary>
    public static async Task<BusinessSettings> EnsureSettingsCountryAsync(SelloraDbContext db, Country country)
    {
        var settings = await db.BusinessSettings.FirstOrDefaultAsync();
        if (settings is null)
        {
            var currency = await db.Currencies.FirstOrDefaultAsync() ?? new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
            settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = currency.Id };
            if (db.Entry(currency).State == EntityState.Detached) db.Currencies.Add(currency);
            db.BusinessSettings.Add(settings);
        }
        settings.CountryId = country.Id;
        await db.SaveChangesAsync();
        return settings;
    }

    public static async Task<TaxComponent> EnsureComponentAsync(SelloraDbContext db, Guid countryId, string code, TaxComponentKind kind, TaxCalculationMethod method = TaxCalculationMethod.Percent)
    {
        var component = await db.TaxComponents.FirstOrDefaultAsync(c => c.CountryId == countryId && c.Code == code);
        if (component is null)
        {
            component = new TaxComponent { Id = Guid.NewGuid(), CountryId = countryId, Code = code, Name = code, Kind = kind, Method = method, CreatedAt = DateTime.UtcNow };
            db.TaxComponents.Add(component);
            await db.SaveChangesAsync();
        }
        return component;
    }

    /// <summary>A GST profile whose mirror tax is <paramref name="tax"/>: version from 2000-01-01 (CGST/SGST halves, IGST), optional company default.</summary>
    public static async Task<TaxProfile> UseAsProfileAsync(SelloraDbContext db, Tax tax, bool companyDefault, string? code = null)
    {
        var country = await EnsureCountryAsync(db);
        await EnsureSettingsCountryAsync(db, country);
        if (await db.Taxes.FindAsync(tax.Id) is null) { db.Taxes.Add(tax); await db.SaveChangesAsync(); }

        var cgst = await EnsureComponentAsync(db, country.Id, "CGST", TaxComponentKind.Cgst);
        var sgst = await EnsureComponentAsync(db, country.Id, "SGST", TaxComponentKind.Sgst);
        var igst = await EnsureComponentAsync(db, country.Id, "IGST", TaxComponentKind.Igst);

        var taxable = tax.RatePercent > 0;
        var profile = new TaxProfile
        {
            Id = Guid.NewGuid(), CountryId = country.Id, Code = code ?? "T-" + tax.Id.ToString("N")[..8].ToUpperInvariant(), Name = tax.Name,
            Regime = TaxRegime.Gst, Treatment = taxable ? TaxTreatment.Taxable : TaxTreatment.NilRated, LegacyTaxId = tax.Id, CreatedAt = DateTime.UtcNow,
        };
        var version = new TaxProfileVersion
        {
            Id = Guid.NewGuid(), TaxProfileId = profile.Id, EffectiveFrom = new DateOnly(2000, 1, 1), TotalPercent = tax.RatePercent, CreatedAt = DateTime.UtcNow,
        };
        if (taxable)
        {
            var half = tax.RatePercent / 2;
            version.Components.Add(Line(version, cgst, TaxApplicability.IntraState, half, 1));
            version.Components.Add(Line(version, sgst, TaxApplicability.IntraState, half, 2));
            version.Components.Add(Line(version, igst, TaxApplicability.InterState, tax.RatePercent, 3));
        }
        profile.Versions.Add(version);
        db.TaxProfiles.Add(profile);
        if (companyDefault)
        {
            db.TaxAssignments.Add(new TaxAssignment
            {
                Id = Guid.NewGuid(), Scope = TaxScope.Company, CountryId = country.Id, SalesTaxProfileId = profile.Id, CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return profile;
    }

    /// <summary>
    /// Makes the products of a fixture purchasable once purchases run on the tax calculator (TP-3: a purchase needs a company country, an own state and a
    /// purchase profile per product). Idempotent. The company gets India and a state (Karnataka, GST code 29) when it has none, and every product without a
    /// product-level assignment gets a GST profile whose mirror is its own <c>Tax</c> (version from 2000-01-01, CGST/SGST halves, IGST) so the old rate
    /// assertions of the fixture stay valid. Call it after the fixture's products are saved (and again after a test adds more).
    /// </summary>
    public static async Task EnablePurchasesAsync(SelloraDbContext db)
    {
        var country = await EnsureCountryAsync(db);
        var settings = await EnsureSettingsCountryAsync(db, country);
        if (settings.StateId is null)
        {
            var state = await db.States.FirstOrDefaultAsync(x => x.CountryId == country.Id && x.Code == "KA");
            if (state is null)
            {
                state = new State { Id = Guid.NewGuid(), CountryId = country.Id, Code = "KA", Name = "Karnataka", Kind = StateKind.State, GstStateCode = "29", IsSystem = true };
                db.States.Add(state);
            }
            settings.StateId = state.Id;
            await db.SaveChangesAsync();
        }

        var assigned = (await db.TaxAssignments.Where(a => a.Scope == TaxScope.Product).Select(a => a.ProductId).ToListAsync()).ToHashSet();
        foreach (var product in await db.Products.Include(p => p.Tax).ToListAsync())
        {
            if (assigned.Contains(product.Id)) continue;
            var profile = await db.TaxProfiles.FirstOrDefaultAsync(p => p.LegacyTaxId == product.TaxId && p.CountryId == country.Id)
                ?? await UseAsProfileAsync(db, product.Tax, companyDefault: false);
            db.TaxAssignments.Add(new TaxAssignment
            {
                Id = Guid.NewGuid(), Scope = TaxScope.Product, ProductId = product.Id, CountryId = country.Id, SalesTaxProfileId = profile.Id, CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Rewrites a purchase into the shape history has (written before TP-3): no profile/version/place, no document-currency twins, no header taxable total,
    /// one legacy tax row per line (the id and values of <c>AddTaxProfiles.legacy-rows.sql</c>). Amounts of the lines are untouched, so a return against
    /// it exercises the pre-TP-3 proportional rule.
    /// </summary>
    public static async Task MakeLegacyPurchaseAsync(SelloraDbContext db, Guid purchaseId)
    {
        var purchase = await db.PurchaseOrders.Include(p => p.Lines).ThenInclude(l => l.Taxes).Include(p => p.Lines).ThenInclude(l => l.Tax).SingleAsync(p => p.Id == purchaseId);
        purchase.TaxableTotal = null;
        purchase.PlaceOfSupply = null;
        purchase.PlaceOfSupplyStateId = null;
        purchase.SupplierStateId = null;
        foreach (var line in purchase.Lines)
        {
            db.PurchaseOrderLineTaxes.RemoveRange(line.Taxes);
            (line.TaxProfileId, line.TaxProfileVersionId, line.PlaceOfSupply, line.HsnCode) = (null, null, null, null);
            (line.OriginalRate, line.OriginalDiscount, line.OriginalOtherCharges, line.OriginalBillDiscountShare, line.OriginalBillChargeShare) = (null, null, null, null, null);
            (line.OriginalTaxableValue, line.OriginalTaxAmount, line.OriginalLineTotal) = (null, null, null);
            db.PurchaseOrderLineTaxes.Add(new PurchaseOrderLineTax
            {
                Id = Sellora.Infrastructure.Data.TaxSeedData.LegacyLineTaxId(line.Id), PurchaseOrderLineId = line.Id, TaxComponentId = null,
                ComponentCode = line.Tax.Name, Kind = TaxComponentKind.Other, Method = TaxCalculationMethod.Percent, RatePercent = line.TaxRatePercent,
                BaseAmount = line.TaxableValue, TaxAmount = line.TaxAmount,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>The usual fixture call: <paramref name="tax"/> becomes the company default.</summary>
    public static Task<TaxProfile> UseAsCompanyDefaultAsync(SelloraDbContext db, Tax tax) => UseAsProfileAsync(db, tax, companyDefault: true);

    private static TaxProfileVersionComponent Line(TaxProfileVersion v, TaxComponent c, TaxApplicability applicability, decimal rate, int sort) => new()
    {
        Id = Guid.NewGuid(), TaxProfileVersionId = v.Id, TaxComponentId = c.Id, Method = c.Method, Applicability = applicability, RatePercent = rate, SortOrder = sort,
    };
}
