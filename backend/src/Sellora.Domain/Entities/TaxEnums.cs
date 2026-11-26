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
