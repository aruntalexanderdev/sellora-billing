namespace Sellora.Domain.Entities;

/// <summary>System-seeded, read-only catalogue entry (docx §8.4). Never created/edited/deleted through the API.</summary>
public class Permission
{
    public Guid Id { get; set; }

    /// <summary>e.g. "Inventory.View" — stable identifier used as the JWT permission claim value and policy name.</summary>
    public required string Code { get; set; }
    public required string Module { get; set; }
    public required string Action { get; set; }
    public required string Description { get; set; }

    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}
