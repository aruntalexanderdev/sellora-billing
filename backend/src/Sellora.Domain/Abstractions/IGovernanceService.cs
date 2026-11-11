using Sellora.Domain.Entities;

namespace Sellora.Domain.Abstractions;

/// <summary>The signed-in governance actor. Governance codes are re-read from the database inside the lock, never taken from claims.</summary>
public sealed record GovernanceActor(Guid UserId, string? CorrelationId = null);

public sealed record AddGovernanceMemberInput(Guid UserId, GovernanceTitle Title, IReadOnlyList<string>? PermissionCodes, string? Reason);

/// <summary>Stable error codes carried in the ProblemDetails <c>code</c> extension (Phase 6 D10).</summary>
public static class GovernanceErrorCodes
{
    public const string SelfGovernanceChangeForbidden = "self_governance_change_forbidden";
    public const string DelegationExceeded = "governance_delegation_exceeded";
    public const string MemberProtected = "governance_member_protected";
    public const string PlatformAccountProtected = "platform_account_protected";
    public const string PermissionRequired = "governance_permission_required";
    public const string LastOwnerProtected = "last_owner_protected";
    public const string SharedAccountProtected = "shared_account_protected";
}

/// <summary>A governance invariant would be broken (last root owner, last administrator). Maps to 400 with <see cref="Code"/>.</summary>
public sealed class GovernanceRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Company governance writes (Phase 6 D7-D11). Callers wrap each call in an AccessManagementScope (transaction + advisory lock) and
/// commit it. 403 rule breaches throw <see cref="AccessManagementForbiddenException"/> (code), missing targets
/// <see cref="AccessTargetNotFoundException"/>, field problems <see cref="DomainValidationException"/>, invariants <see cref="GovernanceRuleException"/>.
/// Every write bumps the subject membership's AccessVersion and appends an audit event. A reason is required on every write.
/// </summary>
public interface IGovernanceService
{
    Task<Guid> AddMemberAsync(GovernanceActor actor, AddGovernanceMemberInput input, CancellationToken ct = default);

    Task ChangeTitleAsync(GovernanceActor actor, Guid memberId, GovernanceTitle title, string? reason, CancellationToken ct = default);

    /// <summary>Replaces the member's grants with <paramref name="permissionCodes"/>.</summary>
    Task SetPermissionsAsync(GovernanceActor actor, Guid memberId, IReadOnlyList<string>? permissionCodes, string? reason, CancellationToken ct = default);

    Task EndMemberAsync(GovernanceActor actor, Guid memberId, string? reason, CancellationToken ct = default);

    /// <summary>Gives the system Administrator role (company-wide) to an active member of this company (D11).</summary>
    Task GrantAdministratorAsync(GovernanceActor actor, Guid userId, string? reason, CancellationToken ct = default);

    Task RemoveAdministratorAsync(GovernanceActor actor, Guid userId, Guid replacementRoleId, string? reason, CancellationToken ct = default);
}
