using Sellora.Domain.Entities;

namespace Sellora.Domain.Abstractions;

/// <summary>
/// The signed-in manager of access, re-read by the caller after the access-management advisory lock (Phase H).
/// <see cref="CanManageBranchAccess"/> = holds <c>Company.Users.ManageBranchAccess</c>; the service additionally requires a
/// company-wide membership. <see cref="CorrelationId"/> is stamped on the audit rows.
/// </summary>
public sealed record AccessActor(Guid UserId, bool IsAdministrator, bool CanManageBranchAccess, string? CorrelationId = null);

/// <summary>Branch selection for an AssignedBranches membership: the allocated branches and exactly one primary among them.</summary>
public sealed record AccessSelection(CompanyAccessScope? AccessScope, IReadOnlyList<Guid>? BranchIds, Guid? PrimaryBranchId);

public sealed record SetAccessScopeInput(CompanyAccessScope AccessScope, IReadOnlyList<Guid>? BranchIds, Guid? PrimaryBranchId, string? Reason);

public sealed record SetMembershipStatusInput(
    MembershipStatus Status, string? Reason, CompanyAccessScope? AccessScope, IReadOnlyList<Guid>? BranchIds, Guid? PrimaryBranchId);

public sealed record ReassignInput(IReadOnlyList<Guid> FromAssignmentIds, Guid ToBranchId, bool MakePrimary, string? Reason);

/// <summary>Stable error codes carried in the ProblemDetails <c>code</c> extension.</summary>
public static class AccessErrorCodes
{
    public const string AccessManagementForbidden = "access_management_forbidden";
    public const string SelfAccessChangeForbidden = "self_access_change_forbidden";
}

/// <summary>The actor may not manage access (missing permission, not company-wide, self change, Administrator target). Maps to 403.</summary>
public sealed class AccessManagementForbiddenException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The user (no membership in the current company) or assignment does not exist. Maps to 404.</summary>
public sealed class AccessTargetNotFoundException : Exception;

/// <summary>
/// Membership and branch-allocation writes (Phase 5). Callers wrap each call in an access-management scope (transaction + advisory
/// lock, actor reloaded after the lock) and commit it; the service saves inside that transaction. Every change bumps
/// <see cref="UserCompanyMembership.AccessVersion"/> and appends an <see cref="AuditEvent"/> in the same transaction.
/// Business-rule violations throw <see cref="DomainValidationException"/> (camelCase request field).
/// </summary>
public interface IUserAccessService
{
    /// <summary>Adds an allocation; <paramref name="isPrimary"/> closes the old primary and reissues it as a plain allocation.</summary>
    Task<UserBranchAssignment> AddAssignmentAsync(AccessActor actor, Guid userId, Guid branchId, bool isPrimary, string? reason, CancellationToken ct = default);

    /// <summary>Ends a non-primary allocation (reason required). Ending the primary or the last allocation is refused.</summary>
    Task EndAssignmentAsync(AccessActor actor, Guid userId, Guid assignmentId, string? reason, CancellationToken ct = default);

    Task SetPrimaryAsync(AccessActor actor, Guid userId, Guid branchId, string? reason, CancellationToken ct = default);

    Task SetScopeAsync(AccessActor actor, Guid userId, SetAccessScopeInput input, CancellationToken ct = default);

    Task SetStatusAsync(AccessActor actor, Guid userId, SetMembershipStatusInput input, CancellationToken ct = default);

    Task ReassignAsync(AccessActor actor, Guid userId, ReassignInput input, CancellationToken ct = default);

    /// <summary>
    /// Sets the scope and stages the initial allocations of a membership that the caller has just added to the context (no save).
    /// No selection: Administrator role -> CompanyWide, otherwise AssignedBranches with one primary at the creator's working branch.
    /// An explicit selection needs <c>ManageBranchAccess</c> and a company-wide actor.
    /// </summary>
    Task InitializeNewMembershipAsync(AccessActor actor, UserCompanyMembership membership, bool isAdministratorRole, AccessSelection? selection, CancellationToken ct = default);

    /// <summary>Promotion to Administrator forces CompanyWide in the same transaction (no-op when already company-wide). Stages changes and saves.</summary>
    Task EnsureCompanyWideForAdministratorAsync(AccessActor actor, Guid userId, CancellationToken ct = default);
}
