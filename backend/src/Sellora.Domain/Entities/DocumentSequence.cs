using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>Per-company (and, from Phase 2, per-branch) gap-free counter for a document series (PUR, PRT, INV, SRT, ADJ, EXP). ADR-08.</summary>
public class DocumentSequence : ICompanyOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    /// <summary>Null = company-level series. FK to Branches is added in Phase 2.</summary>
    public Guid? BranchId { get; set; }
    public required string Series { get; set; }
    /// <summary>The next number to hand out (the numeric suffix of the next document).</summary>
    public long NextValue { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
