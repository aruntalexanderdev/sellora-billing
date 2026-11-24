using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>A tax that prints on the invoice (CGST, SGST, IGST, Cess, VAT). One set per company and country (BR1). Deactivated, never deleted (BR6).</summary>
public class TaxComponent : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }

    /// <summary>The jurisdiction (global country master). A company with branches in several GCC countries has one VAT component per country.</summary>
    public Guid CountryId { get; set; }

    /// <summary>Upper-case code, unique per company and country, at most 20 characters.</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    public TaxComponentKind Kind { get; set; }
    public TaxCalculationMethod Method { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
