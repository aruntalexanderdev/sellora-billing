using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// Append-only audit record (Phase 5, arch §16). Written in the same transaction as the change it describes; never updated or
/// deleted (SelloraDbContext.SaveChanges rejects Modified/Deleted entries). Payloads must not contain secrets.
/// </summary>
public class AuditEvent : ICompanyOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ActorUserId { get; set; }

    /// <summary>See <see cref="AuditActions"/>.</summary>
    public required string Action { get; set; }
    public required string TargetType { get; set; }
    public Guid TargetId { get; set; }

    /// <summary>The user whose access/membership the event concerns, when applicable.</summary>
    public Guid? SubjectUserId { get; set; }
    public string? Reason { get; set; }

    /// <summary>JSON documents (jsonb) of the state before/after the change.</summary>
    public string? Before { get; set; }
    public string? After { get; set; }

    /// <summary>HttpContext.TraceIdentifier of the originating request, when any.</summary>
    public string? CorrelationId { get; set; }
}

/// <summary>Action codes written to <see cref="AuditEvent.Action"/> (Phase 5; later phases add their own).</summary>
public static class AuditActions
{
    public const string MembershipCreated = "membership.created";
    public const string MembershipScopeChanged = "membership.scope_changed";
    public const string MembershipStatusChanged = "membership.status_changed";
    public const string MembershipAccessMapped = "membership.access_mapped";
    public const string BranchAssignmentCreated = "branch_assignment.created";
    public const string BranchAssignmentEnded = "branch_assignment.ended";
    public const string BranchAssignmentPrimaryChanged = "branch_assignment.primary_changed";
    public const string AccessReassigned = "access.reassigned";

    // Phase 6 governance
    public const string GovernanceMemberAdded = "governance.member_added";
    public const string GovernanceMemberEnded = "governance.member_ended";
    public const string GovernanceMemberTitleChanged = "governance.member_title_changed";
    public const string GovernanceMemberPermissionsChanged = "governance.member_permissions_changed";
    public const string GovernanceOwnerMapped = "governance.owner_mapped";
    public const string GovernanceAdministratorGranted = "governance.administrator_granted";
    public const string GovernanceAdministratorRemoved = "governance.administrator_removed";

    // Phase 7: scoped roles.
    public const string RoleAssignmentMigrated = "role_assignment.migrated";
    public const string RoleAssignmentCreated = "role_assignment.created";
    public const string RoleAssignmentEnded = "role_assignment.ended";

    // Phase 9: stock transfers.
    public const string StockTransferCreated = "stock_transfer.created";
    public const string StockTransferUpdated = "stock_transfer.updated";
    public const string StockTransferSubmitted = "stock_transfer.submitted";
    public const string StockTransferApproved = "stock_transfer.approved";
    public const string StockTransferRejected = "stock_transfer.rejected";
    public const string StockTransferCancelled = "stock_transfer.cancelled";
    public const string StockTransferDispatched = "stock_transfer.dispatched";
    public const string StockTransferReceived = "stock_transfer.received";
    public const string StockTransferResolved = "stock_transfer.resolved";

    // Phase 11a: location status changes (reason required).
    public const string BranchStatusChanged = "branch.status_changed";
    public const string WarehouseStatusChanged = "warehouse.status_changed";
    public const string VehicleStatusChanged = "vehicle.status_changed";
}

/// <summary>Values of <see cref="AuditEvent.TargetType"/>.</summary>
public static class AuditTargets
{
    public const string Membership = "UserCompanyMembership";
    public const string BranchAssignment = "UserBranchAssignment";
    public const string GovernanceMember = "CompanyGovernanceMember";
    public const string RoleAssignment = "UserRoleAssignment";
    public const string StockTransfer = "StockTransfer";
    public const string Branch = "Branch";
    public const string Warehouse = "Warehouse";
    public const string Vehicle = "Vehicle";
}
