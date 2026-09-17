using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;

namespace Sellora.Tests.Auth;

public class PermissionResolverTests
{
    [Fact]
    public void Resolve_SystemRole_ReturnsEveryCatalogueCode()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };

        var resolved = PermissionResolver.Resolve(role);

        Assert.Equal(PermissionCatalog.All.Count, resolved.Count);
        Assert.All(PermissionCatalog.All, def => Assert.Contains(def.Code, resolved));
    }

    [Fact]
    public void Resolve_NonSystemRole_ReturnsOnlyMappedPermissions()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = "Sales User", IsSystem = false };
        var salesView = new Permission { Id = Guid.NewGuid(), Code = "Sales.View", Module = "Sales", Action = "View", Description = "x" };
        role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = salesView.Id, Permission = salesView });

        var resolved = PermissionResolver.Resolve(role);

        Assert.Equal(["Sales.View"], resolved);
    }

    [Fact]
    public void Resolve_NonSystemRoleWithNoMappings_ReturnsEmpty()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = "Read Only", IsSystem = false };

        var resolved = PermissionResolver.Resolve(role);

        Assert.Empty(resolved);
    }
}
