using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;

namespace Sellora.Domain.Abstractions;

/// <summary>
/// The tax lookup (BR5, BR8, BR9, BR11). Product, then subcategory, then category, then company default, for the country of the document.
/// A product with no result is a 400 that names the product and the country (AC14), keyed by <c>field</c> (default <c>taxProfile</c>; documents pass <c>Lines</c>).
/// </summary>
public interface ITaxResolver
{
    Task<TaxResolution> ResolveAsync(Guid productId, Guid countryId, string field = "taxProfile", CancellationToken ct = default);

    /// <summary>One load of the assignments for many products. Throws on the first product that does not resolve.</summary>
    Task<IReadOnlyDictionary<Guid, TaxResolution>> ResolveManyAsync(IReadOnlyCollection<Guid> productIds, Guid countryId, string field = "taxProfile", CancellationToken ct = default);

    /// <summary>A product as it would be saved (the product form). Null when no profile applies for the country. Reads only.</summary>
    Task<TaxResolution?> PreviewAsync(TaxPreviewInput input, CancellationToken ct = default);

    /// <summary>The country that decides a document's tax: the branch's country, else the company's (BusinessSettings), else 400 <c>taxProfile</c> (BR8, BR11).</summary>
    Task<Guid> ResolveDocumentCountryAsync(Guid? branchId, string field = "taxProfile", CancellationToken ct = default);

    /// <summary>The version valid on <paramref name="documentDate"/> with its components (BR5); 400 when the profile is inactive or has no version for that date.</summary>
    Task<TaxVersionResolution> ResolveVersionAsync(Guid profileId, DateOnly documentDate, string field = "taxProfile", CancellationToken ct = default);

    /// <summary>
    /// Place of supply of a document (BR11, BR12, T14). Regime comes from the document country (VAT is <see cref="PlaceOfSupply.NotApplicable"/>).
    /// GST: own state = the branch's, else the company's (400 <c>branchId</c> when neither is set); a party with no state (walk-in, <paramref name="partyId"/> null)
    /// is intra-state (a supplier without state also warns); equal states are intra-state, otherwise inter-state; a party in another country is inter-state.
    /// </summary>
    Task<PlaceOfSupplyResolution> ResolvePlaceOfSupplyAsync(TaxDirection direction, Guid? branchId, Guid? partyId, CancellationToken ct = default);

    /// <summary>
    /// The rate set of each product for the document (BR5, BR7): the sales or the purchase profile by <paramref name="direction"/>, and its version valid on
    /// <paramref name="date"/>. <paramref name="productIds"/> is one id per document line (duplicates allowed); every failing line is reported together as
    /// <c>{field}[i].productId</c> (default <c>lines[i].productId</c>). Keyed by product id.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ResolvedLineRates>> ResolveRateSetsAsync(
        TaxDirection direction, Guid countryId, DateOnly date, IReadOnlyList<Guid> productIds, string field = "lines", CancellationToken ct = default);
}
