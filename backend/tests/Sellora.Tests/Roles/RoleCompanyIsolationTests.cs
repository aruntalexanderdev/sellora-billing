using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Roles;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Roles;

/// <summary>Phase 7 T3: roles and assignments are company-owned. A role id of company A is unknown to company B (404 / invalid role), and a context refuses to write another company's role.</summary>
public class RoleCompanyIsolationTests
{
    private static DbContextOptions<SelloraDbContext> NewOptions() => new DbContextOptionsBuilder<SelloraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private static SelloraDbContext DbOf(DbContextOptions<SelloraDbContext> options, Guid company) => new(options, TestCompany.For(company), TestCompany.Branch);

    private sealed record Two(DbContextOptions<SelloraDbContext> Options, Guid RoleA, Guid RoleB, Guid UserA, Guid MemberA, Guid UserB, Guid MemberB, Guid AdminRoleB);

    private static async Task<Two> SeedAsync()
    {
        var options = NewOptions();
        var permission = new Permission { Id = Guid.NewGuid(), Code = "Sales.View", Module = "Sales", Action = "View", Description = "d" };
        Guid roleA, roleB, userA, memberA, userB, memberB, adminB;
        await using (var a = DbOf(options, TestCompany.DefaultId))
        {
            a.Permissions.Add(permission);
            var admin = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
            var role = new Role { Id = Guid.NewGuid(), Name = "Seller" };
            role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
            var user = Rbac.NewUser("a-admin");
            a.AddRange(admin, role);
            var (m, _) = Rbac.Join(a, user, admin);
            await a.SaveChangesAsync();
            (roleA, userA, memberA) = (role.Id, user.Id, m.Id);
        }
        await using (var b = DbOf(options, TestCompany.OtherId))
        {
            var admin = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
            var role = new Role { Id = Guid.NewGuid(), Name = "Seller" }; // same name in B: names are unique per company
            var user = Rbac.NewUser("b-admin");
            b.AddRange(admin, role);
            var (m, _) = Rbac.Join(b, user, admin, TestCompany.OtherId);
            await b.SaveChangesAsync();
            (roleB, userB, memberB, adminB) = (role.Id, user.Id, m.Id, admin.Id);
        }
        return new Two(options, roleA, roleB, userA, memberA, userB, memberB, adminB);
    }

    private static async Task<RoleActor> ActorAsync(SelloraDbContext db, Guid user, Guid member)
    {
        var set = await EffectivePermissionService.LoadAsync(db, member, default);
        return new RoleActor(user, member, set.IsAdministrator, true, set, "t");
    }

    [Fact]
    public async Task Roles_OfCompanyA_AreInvisibleToCompanyB_ByIdAndByList()
    {
        var s = await SeedAsync();
        await using var b = DbOf(s.Options, TestCompany.OtherId);

        Assert.Null(await b.Roles.SingleOrDefaultAsync(r => r.Id == s.RoleA));
        Assert.Equal(2, await b.Roles.CountAsync()); // its own Administrator and Seller only
        Assert.DoesNotContain(b.Roles.AsEnumerable(), r => r.Id == s.RoleA);
        Assert.Empty(b.RolePermissions); // grants are filtered through their role
    }

    [Fact]
    public async Task RoleService_UpdateDuplicateAndStatus_OfAnotherCompanysRole_AreNotFound()
    {
        var s = await SeedAsync();
        await using var b = DbOf(s.Options, TestCompany.OtherId);
        var actor = await ActorAsync(b, s.UserB, s.MemberB);
        var service = new RoleService(b);

        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => service.UpdateAsync(actor, s.RoleA, new UpdateRoleInput("x", null, [], null, null)));
        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => service.DuplicateAsync(actor, s.RoleA));
        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => service.SetActiveAsync(actor, s.RoleA, false));
    }

    [Fact]
    public async Task AssignAsync_ARoleOfAnotherCompany_IsRejectedAsAnInvalidRole_AndNothingIsWritten()
    {
        var s = await SeedAsync();
        await using var b = DbOf(s.Options, TestCompany.OtherId);
        var actor = await ActorAsync(b, s.UserB, s.MemberB);
        var target = Rbac.NewUser("b-target");
        var teammate = new Role { Id = Guid.NewGuid(), Name = "Empty" };
        b.Add(teammate);
        Rbac.Join(b, target, teammate, TestCompany.OtherId);
        await b.SaveChangesAsync();
        b.ChangeTracker.Clear();
        var service = new RoleAssignmentService(b, new UserAccessService(b));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.AssignAsync(actor, target.Id, s.RoleA, null));

        Assert.Equal("roleId", ex.Field);
        b.ChangeTracker.Clear();
        Assert.Equal(1, await b.UserRoleAssignments.CountAsync(a => a.RoleId == teammate.Id));
        Assert.Equal(0, await b.UserRoleAssignments.IgnoreQueryFilters().CountAsync(a => a.RoleId == s.RoleA && a.CompanyId == TestCompany.OtherId));
    }

    [Fact]
    public async Task AssignAsync_ATargetOfAnotherCompany_IsNotFound()
    {
        var s = await SeedAsync();
        await using var b = DbOf(s.Options, TestCompany.OtherId);
        var actor = await ActorAsync(b, s.UserB, s.MemberB);
        var service = new RoleAssignmentService(b, new UserAccessService(b));

        await Assert.ThrowsAsync<AccessTargetNotFoundException>(() => service.AssignAsync(actor, s.UserA, s.RoleB, null));
    }

    [Fact]
    public async Task EffectivePermissions_OfAMembership_AreEvaluatedInItsOwnCompanyOnly()
    {
        var s = await SeedAsync();
        await using var a = DbOf(s.Options, TestCompany.DefaultId);

        var asA = await EffectivePermissionService.LoadAsync(a, s.MemberA, default);
        var crossed = await EffectivePermissionService.LoadAsync(a, s.MemberB, default); // company B's membership read with an A context: roles are matched by the membership's own company

        Assert.True(asA.IsAdministrator);
        Assert.True(crossed.IsAdministrator); // B's own Administrator assignment, B's role row: never A's
        Assert.Empty(crossed.CompanyCodes);
    }

    [Fact]
    public async Task DbContext_RefusesToWriteARoleOfAnotherCompany()
    {
        var s = await SeedAsync();
        await using var a = DbOf(s.Options, TestCompany.DefaultId);
        a.Roles.Add(new Role { Id = Guid.NewGuid(), CompanyId = TestCompany.OtherId, Name = "Smuggled" });

        await Assert.ThrowsAnyAsync<Exception>(() => a.SaveChangesAsync());
    }
}
