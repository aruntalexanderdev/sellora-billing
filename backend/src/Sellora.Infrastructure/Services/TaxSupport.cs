using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>Formats and small rules shared by the tax services (docs/features/tax-profiles.md).</summary>
internal static partial class TaxRules
{
    public const int CodeMaxLength = 20;
    public const int NameMaxLength = 60;

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,19}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[0-9]{4}([0-9]{2}){0,2}$")]
    private static partial Regex HsnPattern();

    /// <summary>BR11: India is GST, every other country VAT.</summary>
    public static TaxRegime RegimeFor(string countryCode) => string.Equals(countryCode, "IN", StringComparison.OrdinalIgnoreCase) ? TaxRegime.Gst : TaxRegime.Vat;

    public static string NormaliseCode(string? code, string field = "code")
    {
        var value = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(value))
        {
            throw new DomainValidationException(field, $"Code must be 1 to {CodeMaxLength} letters, digits or hyphens, starting with a letter or digit.");
        }
        return value;
    }

    public static string NormaliseName(string? name, string field = "name")
    {
        var value = (name ?? string.Empty).Trim();
        if (value.Length == 0) throw new DomainValidationException(field, "Name is required.");
        if (value.Length > NameMaxLength) throw new DomainValidationException(field, $"Name cannot exceed {NameMaxLength} characters.");
        return value;
    }

    /// <summary>HSN/SAC code: empty = none; otherwise 4, 6 or 8 digits (the database check, BR9). Field <c>hsnCode</c>.</summary>
    public static string? NormaliseHsn(string? hsn, string field = "hsnCode")
    {
        var value = (hsn ?? string.Empty).Trim();
        if (value.Length == 0) return null;
        if (!HsnPattern().IsMatch(value)) throw new DomainValidationException(field, "The HSN/SAC code must be 4, 6 or 8 digits.");
        return value;
    }

    public static string Plain(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>Decimal places of a value without its trailing zeros (5.250 has 2).</summary>
    public static int DecimalPlaces(decimal value) => (decimal.GetBits(value / 1.0000000000000000000000000000m)[3] >> 16) & 0xFF;

    public static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    /// <summary>The mirror tax name: the profile name, or with the country code when another tax of the company has it (Taxes.Name is unique per company, profile names per country).</summary>
    public static string MirrorName(string profileName, string countryCode, ISet<string> takenNames)
    {
        var name = profileName;
        if (takenNames.Contains(name)) name = $"{profileName[..Math.Min(profileName.Length, NameMaxLength - countryCode.Length - 3)]} ({countryCode})";
        for (var n = 2; takenNames.Contains(name); n++)
        {
            var suffix = $" ({countryCode}) {n}";
            name = profileName[..Math.Min(profileName.Length, NameMaxLength - suffix.Length)] + suffix;
        }
        takenNames.Add(name);
        return name;
    }
}

/// <summary>Per-company advisory lock of the assignment/mirror writers (TP-1 design: <c>sellora:tax-assign:&lt;company&gt;</c>). Callers must be in a transaction. No-op on InMemory.</summary>
internal static class TaxLocks
{
    public static async Task LockAssignmentsAsync(SelloraDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return;
        var key = "sellora:tax-assign:" + db.CompanyContext.RequireCompanyId().ToString("D", CultureInfo.InvariantCulture);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({key}))", ct);
    }

    /// <summary>FOR UPDATE on one profile row: serialises version writes (D5: there is no exclusion constraint).</summary>
    public static async Task LockProfileAsync(SelloraDbContext db, Guid profileId, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return;
        var companyId = db.CompanyContext.RequireCompanyId();
        await db.Database
            .SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"TaxProfiles\" WHERE \"CompanyId\" = {companyId} AND \"Id\" = {profileId} FOR UPDATE")
            .ToListAsync(ct);
    }
}

/// <summary>The assignments of one country, loaded once, with the BR8 lookup: product, then subcategory, then category, then company default.</summary>
internal sealed class TaxAssignmentIndex
{
    private TaxAssignmentIndex(Guid countryId, List<TaxAssignment> rows)
    {
        CountryId = countryId;
        Rows = rows;
        Company = rows.FirstOrDefault(a => a.Scope == TaxScope.Company);
        ByCategory = rows.Where(a => a.Scope == TaxScope.Category && a.CategoryId != null).ToDictionary(a => a.CategoryId!.Value);
        BySubcategory = rows.Where(a => a.Scope == TaxScope.Subcategory && a.SubcategoryId != null).ToDictionary(a => a.SubcategoryId!.Value);
        ByProduct = rows.Where(a => a.Scope == TaxScope.Product && a.ProductId != null).ToDictionary(a => a.ProductId!.Value);
    }

    public Guid CountryId { get; }
    public List<TaxAssignment> Rows { get; }
    public TaxAssignment? Company { get; }
    public Dictionary<Guid, TaxAssignment> ByCategory { get; }
    public Dictionary<Guid, TaxAssignment> BySubcategory { get; }
    public Dictionary<Guid, TaxAssignment> ByProduct { get; }

    public static async Task<TaxAssignmentIndex> LoadAsync(SelloraDbContext db, Guid countryId, CancellationToken ct) =>
        new(countryId, await db.TaxAssignments.AsNoTracking().Where(a => a.CountryId == countryId).ToListAsync(ct));

    /// <summary>The assignment that applies, without the product's own row when <paramref name="includeProduct"/> is false (what it would inherit).</summary>
    public TaxAssignment? Find(Guid? productId, Guid? categoryId, Guid? subcategoryId, bool includeProduct = true)
    {
        if (includeProduct && productId is { } p && ByProduct.TryGetValue(p, out var product)) return product;
        if (subcategoryId is { } s && BySubcategory.TryGetValue(s, out var sub)) return sub;
        if (categoryId is { } c && ByCategory.TryGetValue(c, out var cat)) return cat;
        return Company;
    }

    public static Guid EffectivePurchase(TaxAssignment a) => a.PurchaseTaxProfileId ?? a.SalesTaxProfileId;
}

/// <summary>Keeps the mirror <c>Products.TaxId</c> equal to the mirror tax of the product's resolved sales profile for the company country (D3).</summary>
internal static class TaxMirror
{
    /// <summary>The mirror tax a product with these facts resolves to, or null when nothing applies.</summary>
    public static Guid? MirrorTaxIdFor(TaxAssignmentIndex index, IReadOnlyDictionary<Guid, Guid> mirrorByProfile, Guid? productId, Guid? categoryId, Guid? subcategoryId)
    {
        var assignment = index.Find(productId, categoryId, subcategoryId);
        return assignment is not null && mirrorByProfile.TryGetValue(assignment.SalesTaxProfileId, out var tax) ? tax : null;
    }

    public static async Task<Dictionary<Guid, Guid>> MirrorByProfileAsync(SelloraDbContext db, Guid countryId, CancellationToken ct) =>
        await db.TaxProfiles.AsNoTracking().Where(p => p.CountryId == countryId).ToDictionaryAsync(p => p.Id, p => p.LegacyTaxId, ct);

    /// <summary>Re-syncs the mirror of the products matching <paramref name="scope"/> (the assignments must already be saved). Returns the number changed; the caller saves.</summary>
    public static async Task<int> SyncAsync(SelloraDbContext db, System.Linq.Expressions.Expression<Func<Product, bool>>? scope, CancellationToken ct)
    {
        var companyCountry = await db.BusinessSettings.AsNoTracking().Select(s => s.CountryId).FirstOrDefaultAsync(ct);
        if (companyCountry is not { } countryId) return 0;

        var index = await TaxAssignmentIndex.LoadAsync(db, countryId, ct);
        var mirrors = await MirrorByProfileAsync(db, countryId, ct);
        var query = db.Products.AsNoTracking();
        if (scope is not null) query = query.Where(scope);
        var facts = await query.Select(p => new { p.Id, p.CategoryId, p.SubcategoryId, p.TaxId }).ToListAsync(ct);

        var changed = facts
            .Select(p => (p.Id, Target: MirrorTaxIdFor(index, mirrors, p.Id, p.CategoryId, p.SubcategoryId), p.TaxId))
            .Where(x => x.Target is { } t && t != x.TaxId)
            .ToDictionary(x => x.Id, x => x.Target!.Value);
        if (changed.Count == 0) return 0;

        foreach (var chunk in changed.Keys.Chunk(1000))
        {
            foreach (var product in await db.Products.Where(p => chunk.Contains(p.Id)).ToListAsync(ct))
            {
                product.TaxId = changed[product.Id];
            }
        }
        return changed.Count;
    }
}

/// <summary>
/// "Used" (BR4): a sales or purchase line with the profile's mirror tax dated inside the version's range. Returns reverse their original line and add no information.
/// The branch filter is ignored on purpose: a version used at another branch is still used. The company filter stays.
/// </summary>
public static class TaxVersionUsage
{
    public static async Task<bool> IsUsedAsync(SelloraDbContext db, Guid mirrorTaxId, DateOnly effectiveFrom, DateOnly? effectiveTo, CancellationToken ct) =>
        await db.SalesInvoiceLines.IgnoreQueryFilters([BranchQueryFilter.Name])
            .AnyAsync(l => l.TaxId == mirrorTaxId && l.SalesInvoice.InvoiceDate >= effectiveFrom && (effectiveTo == null || l.SalesInvoice.InvoiceDate <= effectiveTo), ct)
        || await db.PurchaseOrderLines.IgnoreQueryFilters([BranchQueryFilter.Name])
            .AnyAsync(l => l.TaxId == mirrorTaxId && l.PurchaseOrder.PurchaseDate >= effectiveFrom && (effectiveTo == null || l.PurchaseOrder.PurchaseDate <= effectiveTo), ct);

    /// <summary>The latest document date (sales invoice or purchase) of any line carrying the profile's mirror tax, or null when none (T25, BR4).</summary>
    public static async Task<DateOnly?> LatestDocumentDateAsync(SelloraDbContext db, Guid mirrorTaxId, CancellationToken ct)
    {
        var sales = await db.SalesInvoiceLines.IgnoreQueryFilters([BranchQueryFilter.Name]).Where(l => l.TaxId == mirrorTaxId)
            .Select(l => (DateOnly?)l.SalesInvoice.InvoiceDate).MaxAsync(ct);
        var purchases = await db.PurchaseOrderLines.IgnoreQueryFilters([BranchQueryFilter.Name]).Where(l => l.TaxId == mirrorTaxId)
            .Select(l => (DateOnly?)l.PurchaseOrder.PurchaseDate).MaxAsync(ct);
        return sales is null ? purchases : purchases is null ? sales : (sales > purchases ? sales : purchases);
    }
}

/// <summary>
/// T24: changing the company country. Allowed only while the company has no products and no sales or purchase documents (otherwise product mirrors and
/// history would sit on the old country's taxes). The new country's standard profiles and company default are then seeded; the old country's profiles stay, unused.
/// Both calls run inside the caller's transaction.
/// </summary>
public static class TaxCountryChange
{
    /// <summary>True when the company has any product or any sales or purchase document: its country is then locked.</summary>
    public static async Task<bool> IsLockedAsync(SelloraDbContext db, CancellationToken ct) =>
        await db.Products.AnyAsync(ct)
        || await db.SalesInvoices.IgnoreQueryFilters([BranchQueryFilter.Name]).AnyAsync(ct)
        || await db.PurchaseOrders.IgnoreQueryFilters([BranchQueryFilter.Name]).AnyAsync(ct);

    /// <summary>Takes the assignment advisory lock, then refuses (400 <c>countryId</c>) a change to another country while the country is locked.</summary>
    public static async Task EnsureAllowedAsync(SelloraDbContext db, CancellationToken ct)
    {
        await TaxLocks.LockAssignmentsAsync(db, ct);
        if (await IsLockedAsync(db, ct))
        {
            throw new DomainValidationException("countryId", "The country cannot change once the company has products or sales or purchase documents: their taxes belong to the current country.");
        }
    }

    /// <summary>After the new country is saved on the settings: seeds its standard profiles and default, saves, and re-syncs the product mirrors.</summary>
    public static async Task SeedNewCountryAsync(SelloraDbContext db, Guid companyId, Guid countryId, CancellationToken ct)
    {
        await TaxSeeder.SeedCompanyAsync(db, companyId, countryId, null, ct);
        await db.SaveChangesAsync(ct);
        if (await TaxMirror.SyncAsync(db, null, ct) > 0) await db.SaveChangesAsync(ct);
    }
}
