using Sellora.Domain.Entities;
using Sellora.Domain.Roles;

namespace Sellora.Domain.Abstractions;

/// <summary>
/// Role definition writes (Phase 7, D1/D9/D10; rules R1-R3). Callers run inside an access-management scope (transaction + advisory lock,
/// actor reloaded after the lock) and commit it. Throws <see cref="AccessManagementForbiddenException"/> (403, <see cref="RoleErrorCodes"/>),
/// <see cref="AccessTargetNotFoundException"/> (404: unknown role, other company, branch not reachable), <see cref="RoleRuleException"/> (400 with
/// code) and <see cref="DomainValidationException"/> (400 field). Editing the permissions of a role or (de)activating it bumps the
/// AccessVersion of every Active holder in the same transaction.
/// </summary>
public interface IRoleService
{
    Task<Role> CreateAsync(RoleActor actor, CreateRoleInput input, CancellationToken ct = default);

    Task<Role> UpdateAsync(RoleActor actor, Guid id, UpdateRoleInput input, CancellationToken ct = default);

    /// <summary>Copy of a role at the same scope; the actor must hold every code of the source (Administrator: whole catalogue).</summary>
    Task<Role> DuplicateAsync(RoleActor actor, Guid id, CancellationToken ct = default);

    Task SetActiveAsync(RoleActor actor, Guid id, bool isActive, CancellationToken ct = default);
}
