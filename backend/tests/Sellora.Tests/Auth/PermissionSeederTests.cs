using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Auth;

/// <summary>Sales.PriceOverride (Phase 5 decision Q2) reaches the Permissions table through the catalog but is granted to no
/// existing role: not even those holding Sales.Create, which do get the older Sales.Receive migration grant.</summary>
public class PermissionSeederTests
{
    [Fact]
    public async Task SeedAsync_RolesHoldingSalesCreate_GetSalesReceiveButNeverSalesPriceOverride()
    {
        var db = TestDb.NewInMemory();
        var cashier = new Role { Id = Guid.NewGuid(), Name = "Cashier", IsSystem = false };
        db.Roles.Add(cashier);
        db.Currencies.Add(new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" }); // a migration seeds this on Postgres
        foreach (var code in new[] { "Sales.Create", "Sales.View" })
        {
            var def = PermissionCatalog.All.Single(p => p.Code == code);
            var permission = new Permission { Id = Guid.NewGuid(), Code = def.Code, Module = def.Module, Action = def.Action, Description = def.Description };
            db.Permissions.Add(permission);
            db.RolePermissions.Add(new RolePermission { RoleId = cashier.Id, PermissionId = permission.Id });
        }
        await db.SaveChangesAsync();

        await DbSeeder.SeedAsync(db, new PasswordHasher());

        Assert.Contains(await db.Permissions.ToListAsync(), p => p.Code == "Sales.PriceOverride");
        var held = await db.RolePermissions.Where(rp => rp.RoleId == cashier.Id).Select(rp => rp.Permission.Code).ToListAsync();
        Assert.Contains("Sales.Receive", held);
        Assert.DoesNotContain("Sales.PriceOverride", held);
        var overrideId = (await db.Permissions.SingleAsync(p => p.Code == "Sales.PriceOverride")).Id;
        Assert.Empty(await db.RolePermissions.Where(rp => rp.PermissionId == overrideId).ToListAsync());
    }
}
