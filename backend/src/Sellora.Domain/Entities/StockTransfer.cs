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

    /// <summary>FI-D49: estimated value at submit = sum of requested base qty x the source location's average cost (base currency, 2 dp).</summary>
    public decimal? EstimatedValue { get; set; }

    /// <summary>FI-D48: snapshot at submit — the estimated value reached the company's requester approval threshold.</summary>
    public bool RequiresRequesterApproval { get; set; }

    public DateTimeOffset? RequesterApprovedAt { get; set; }
    public Guid? RequesterApprovedByUserId { get; set; }

    /// <summary>Snapshot of BusinessSettings.TransferApprovalSeparationOfDuties taken at submit; later setting changes never alter it.</summary>
    public bool RequireApprovalSeparation { get; set; }

    /// <summary>Reason the approver gave for approving less than requested on one or more lines (null when approved as requested).</summary>
    public string? ApprovalAdjustmentReason { get; set; }

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

    /// <summary>FI-D26: set when the remainder of an approved request is closed (Status Closed). Reason is mandatory.</summary>
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public string? ClosureReason { get; set; }

    public List<StockTransferLine> Lines { get; set; } = [];
    public List<StockTransferReceipt> Receipts { get; set; } = [];
}
