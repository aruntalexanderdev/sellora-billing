using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// FI-D35: one receiving event of a consignment. Created Verified (counts only, no stock moved); Posted moves the accepted quantity
/// Transit to destination exactly once. Sequence is 1-based per consignment. Posted iff PostedAt + PostedByUserId (DB CHECK).
/// </summary>
public class ConsignmentReceipt : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid ConsignmentId { get; set; }
    public int Sequence { get; set; }
    public ConsignmentReceiptStatus Status { get; set; } = ConsignmentReceiptStatus.Verified;

    public DateTimeOffset VerifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid VerifiedByUserId { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public Guid? PostedByUserId { get; set; }
    public string? Notes { get; set; }

    public List<ConsignmentReceiptLine> Lines { get; set; } = [];
}
