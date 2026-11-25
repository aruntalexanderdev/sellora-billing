using Sellora.Domain.Taxes;

namespace Sellora.Domain.Abstractions;

/// <summary>The tax of a document before it is saved (TP-2). Read-only; uses the same working branch, price basis, units and rate sets as the save,
/// so the preview matches it. Validation problems are <see cref="DomainValidationException"/>s keyed <c>lines[i].field</c>, <c>partyId</c>, <c>billDiscount</c>, ...
/// Permissions (<c>Sales.BillDiscount</c>, BR19a) are checked by the caller; the cap on the bill discount (T15) is checked here.</summary>
public interface ITaxDocumentPreviewService
{
    /// <summary>The working branch the preview (and the save) uses; the caller checks permissions at it. 400 <c>branchId</c> when none can be used.</summary>
    Task<Guid> ResolveWorkingBranchIdAsync(CancellationToken ct = default);

    Task<TaxDocumentPreview> PreviewAsync(TaxDocumentPreviewInput input, CancellationToken ct = default);
}
