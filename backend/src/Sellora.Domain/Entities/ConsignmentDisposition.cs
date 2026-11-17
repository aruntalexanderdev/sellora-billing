using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// FI-D40: one disposition event resolving damaged / rejected / missing quantities of a consignment. Append-only (no update/delete in the service layer).
/// Sequence is 1-based per consignment (unique); Reason is mandatory.
/// </summary>
public class ConsignmentDisposition : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid ConsignmentId { get; set; }
    public int Sequence { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public Guid ByUserId { get; set; }

    public List<ConsignmentDispositionLine> Lines { get; set; } = [];
}
