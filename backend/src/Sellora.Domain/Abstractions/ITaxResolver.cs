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
}
