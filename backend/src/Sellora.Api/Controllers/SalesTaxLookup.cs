using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Api.Controllers;

/// <summary>
/// F7 (TP-5a): the tax label and rate screens show for a product come from its resolved sales profile (company country, version valid today), never from
/// the legacy <c>Product.Tax</c> mirror. One batched resolver call per request; a company without a country, or a product without a profile, gets an empty label and no rate.
/// </summary>
internal static class SalesTaxLookup
{
    public static async Task<IReadOnlyDictionary<Guid, CurrentSalesTax>> ForCompanyAsync(
        SelloraDbContext db, ITaxResolver resolver, IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        if (productIds.Count == 0) return new Dictionary<Guid, CurrentSalesTax>();
        var countryId = await db.BusinessSettings.AsNoTracking().Select(s => s.CountryId).FirstOrDefaultAsync(ct);
        return countryId is { } country
            ? await resolver.ResolveCurrentSalesProfilesAsync(country, DateOnly.FromDateTime(DateTime.UtcNow), productIds, ct)
            : new Dictionary<Guid, CurrentSalesTax>();
    }

    public static string Label(IReadOnlyDictionary<Guid, CurrentSalesTax> lookup, Guid productId) =>
        lookup.TryGetValue(productId, out var tax) ? tax.ProfileName : string.Empty;

    /// <summary>T11 (TP-5a): after an assignment or subcategory change, a warning (field <c>hsnCode</c>) with the number of active products that resolve to a GST-taxable profile and
    /// have no HSN/SAC. The change itself is always allowed. Empty when none (or when there is no country).</summary>
    public static async Task<IReadOnlyList<FieldErrorDto>> MissingHsnWarningsAsync(SelloraDbContext db, Guid? countryId, CancellationToken ct)
    {
        var country = countryId ?? await db.BusinessSettings.AsNoTracking().Select(s => s.CountryId).FirstOrDefaultAsync(ct);
        if (country is not { } id) return [];
        var count = await HsnRules.CountProductsWithoutHsnAsync(db, id, ct);
        return count == 0 ? [] : [new FieldErrorDto("hsnCode", $"{count} product{(count == 1 ? " has" : "s have")} no HSN/SAC code under a GST-taxable profile. Add the code to the product or its subcategory.")];
    }
}
