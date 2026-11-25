using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;

namespace Sellora.Domain.Abstractions;

/// <summary>
/// Assignments of tax profiles to the company, a category, a subcategory or a product (BR7, BR10). Every change re-syncs the mirror
/// <c>Products.TaxId</c> of the affected products (D3) under the per-company advisory lock <c>sellora:tax-assign:&lt;company&gt;</c>.
/// Violations throw <see cref="DomainValidationException"/> keyed <c>countryId</c>, <c>scopeId</c>, <c>salesProfileId</c>, <c>purchaseProfileId</c> or <c>id</c>.
/// </summary>
public interface ITaxAssignmentService
{
    /// <summary>Creates or updates the single row of (scope target, country). Both profiles must be active and belong to the country.</summary>
    Task<TaxAssignment> UpsertAsync(UpsertTaxAssignmentInput input, CancellationToken ct = default);

    /// <summary>The company default cannot be deleted (400 <c>id</c>). Returns false when the assignment does not exist.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>T20: removes the product overrides of the country whose sales and purchase profiles equal what the product would inherit without the override. Returns how many were removed.</summary>
    Task<int> ClearRedundantOverridesAsync(Guid countryId, CancellationToken ct = default);
}
