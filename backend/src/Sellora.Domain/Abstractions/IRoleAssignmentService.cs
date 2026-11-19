using Sellora.Domain.Entities;
using Sellora.Domain.Roles;

namespace Sellora.Domain.Abstractions;

/// <summary>
/// Role assignment writes (Phase 7, D3/D9/D10; rule R4). History is close-only: a row is written once and closed once. Every change bumps
/// the membership AccessVersion and appends a RoleAssignmentCreated/Ended audit event in the same transaction. Callers run inside an
/// access-management scope. Exceptions as <see cref="IRoleService"/>.
/// </summary>
public interface IRoleAssignmentService
{
    Task<UserRoleAssignment> AssignAsync(RoleActor actor, Guid userId, Guid roleId, string? reason, CancellationToken ct = default);

    Task EndAsync(RoleActor actor, Guid userId, Guid assignmentId, string? reason, CancellationToken ct = default);

    /// <summary>
    /// Compat for POST users (RoleId): stages (no save) the initial company-role assignment of a membership the caller has just added to the
    /// context. Same R4 checks as <see cref="AssignAsync"/> except the target lookups.
    /// </summary>
    Task StageInitialCompanyRoleAsync(RoleActor actor, UserCompanyMembership membership, Guid roleId, CancellationToken ct = default);

    /// <summary>
    /// Phase 01b (FI-D44): stages the initial branch-role assignment of a user created by a branch administrator. The role must be an active branch
    /// role of <paramref name="branchId"/> (the active branch the membership is allocated to in the same save); same R4 and FI-D45 checks as
    /// <see cref="AssignAsync"/> except the target lookups.
    /// </summary>
    Task StageInitialBranchRoleAsync(RoleActor actor, UserCompanyMembership membership, Guid roleId, Guid branchId, CancellationToken ct = default);

    /// <summary>
    /// Compat for PUT users (RoleId = "set the single company role"): ends the other Active company-role assignments and adds this one.
    /// No-op when the role is already the only company role. Saves.
    /// </summary>
    Task SetSingleCompanyRoleAsync(RoleActor actor, Guid userId, Guid roleId, CancellationToken ct = default);
}
