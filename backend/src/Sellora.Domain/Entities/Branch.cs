using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>A company-owned branch / outlet. Exactly one per company is the default ("Main"). <see cref="Code"/> is immutable: it is a numbering segment.</summary>
public class Branch : ICompanyOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>Upper-case, 1-6 chars of A-Z0-9; immutable after create.</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? AddressLine { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    /// <summary>Optional own GSTIN; null = company GSTIN applies.</summary>
    public string? Gstin { get; set; }
    /// <summary>Optional own invoice prefix; null = company prefix + branch code.</summary>
    public string? InvoicePrefix { get; set; }

    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    /// <summary>Optional country (BR8). StateId, when set, must belong to it (composite FK).</summary>
    public Guid? CountryId { get; set; }
    public Guid? StateId { get; set; }
}
