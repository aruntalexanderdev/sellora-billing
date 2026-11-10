namespace Sellora.Domain.Entities;

/// <summary>
/// System-seeded governance permission catalogue entry (Phase 6 D7/D8). A table of its own, so RolePermissions (FK to Permissions)
/// can never reference a governance code. Never created/edited through the API. Deterministic ids: see GovernancePermissionCatalog.IdFor.
/// </summary>
public class GovernancePermission
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Description { get; set; }
}
