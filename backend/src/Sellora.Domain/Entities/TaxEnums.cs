namespace Sellora.Domain.Entities;

/// <summary>What a tax component is called on the invoice (BR1). Persisted as a string.</summary>
public enum TaxComponentKind
{
    Cgst,
    Sgst,
    Utgst,
    Igst,
    Cess,
    Vat,
    Other,
}

/// <summary>How a component amount is calculated (BR1): a percentage of the taxable value, or an amount per base unit. Persisted as a string.</summary>
public enum TaxCalculationMethod
{
    Percent,
    AmountPerBaseUnit,
}

/// <summary>The tax system of a profile's country (BR2). Persisted as a string.</summary>
public enum TaxRegime
{
    Gst,
    Vat,
}

/// <summary>Tax treatment of a profile (BR2). Only <see cref="Taxable"/> profiles carry components. Persisted as a string.</summary>
public enum TaxTreatment
{
    Taxable,
    NilRated,
    Exempt,
    ZeroRated,
    NonTaxable,
}

/// <summary>When a component of a version applies (BR3): GST intra-state, inter-state, or always (VAT, cess). Persisted as a string.</summary>
public enum TaxApplicability
{
    IntraState,
    InterState,
    Always,
}

/// <summary>The level a <see cref="TaxAssignment"/> is set at (BR7). Persisted as a string.</summary>
public enum TaxScope
{
    Company,
    Category,
    Subcategory,
    Product,
}

/// <summary>Where a GST supply happens relative to the seller (BR12): decides which component set applies. VAT documents are <see cref="NotApplicable"/>.</summary>
public enum PlaceOfSupply
{
    IntraState,
    InterState,
    NotApplicable,
}

/// <summary>The tax status of the other party (BR13). Only <see cref="Unspecified"/> exists until the party-status phase (T4).</summary>
public enum PartyTaxStatus
{
    Unspecified,
}

/// <summary>Which side of the business a tax document belongs to: sales use the sales profile, purchases the purchase profile (BR7).</summary>
public enum TaxDirection
{
    Sale,
    Purchase,
}
