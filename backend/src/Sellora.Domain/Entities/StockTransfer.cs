using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// A transfer of stock between two locations of one company through the company's Transit location (phase-9 D1, ADR-02).
/// Dispatch moves source -> Transit, receipts move Transit -> destination, resolutions close the remainder. The location ids are
/// same-company composite FKs, navigation-less. The three branch ids are what the "Branch" query filter reads (phase-9 D7):
/// <see cref="BranchId"/> is the creating working branch (number-series owner), Source/DestinationBranchId are the effective branch
/// of each endpoint (location.BranchId, or the vehicle's AssignedBranchId), snapshotted at creation (stable while open, D9).
/// </summary>
public class StockTransfer : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }

    /// <summary>TRF number, unique per company. Allocated on the creating branch's series.</summary>
    public required string TransferNo { get; set; }

    public Guid BranchId { get; set; }

    public Guid SourceLocationId { get; set; }
    public Guid DestinationLocationId { get; set; }

    /// <summary>Snapshot of the company's Transit location at creation.</summary>
    public Guid TransitLocationId { get; set; }

    public Guid? SourceBranchId { get; set; }
    public Guid? DestinationBranchId { get; set; }

    public StockTransferStatus Status { get; set; } = StockTransferStatus.Draft;
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }
    public Guid? SubmittedByUserId { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }

    public DateTimeOffset? RejectedAt { get; set; }
    public Guid? RejectedByUserId { get; set; }
    public string? RejectionReason { get; set; }

    public DateTimeOffset? DispatchedAt { get; set; }
    public Guid? DispatchedByUserId { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CompletedByUserId { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public string? CancellationReason { get; set; }

    public List<StockTransferLine> Lines { get; set; } = [];
    public List<StockTransferReceipt> Receipts { get; set; } = [];
}
