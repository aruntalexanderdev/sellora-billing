namespace Sellora.Domain.Entities;

/// <summary>
/// A unit the product can be sold in, e.g. "10 kg bag" or "Loose kg" (docx §9.3). Pricing is deliberately
/// not modeled here — Phase 5 adds effective-dated SellingPrice rows so historical sales never shift
/// when a price changes.
/// </summary>
public class ProductSellingMode
{
    public Guid Id { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public required string Label { get; set; }

    /// <summary>How many base units one of this selling mode represents, e.g. 10 for a "10 kg bag" when the base unit is kg.</summary>
    public decimal ConversionFactorToBase { get; set; }

    public bool IsActive { get; set; } = true;
}
