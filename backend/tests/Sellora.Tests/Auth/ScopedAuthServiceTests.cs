using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Auth;

/// <summary>
/// Phase 7 T14: login, refresh and company switch run before a company context exists. The effective permissions returned are those of the membership in the chosen company at the
/// working branch (primary allocation, else the default branch); a user shared by two companies holds different roles in each; tokens carry no permission claims.
/// </summary>
public class ScopedAuthServiceTests
{
    private static readonly Guid CompanyB = TestCompany.OtherId;

    private sealed class World
    {
        public DbContextOptions<SelloraDbContext> Options { get; } = new DbContextOptionsBuilder<SelloraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public PasswordHasher Hasher { get; } = new();
        public User Sam { get; }
        public Branch MainA { get; } = BranchBuilder.MainBranch();
        public Branch ExtraA { get; } = BranchBuilder.Extra("EXA");
        public Branch MainB { get; }
        public Role SellerA { get; } = new() { Id = Guid.NewGuid(), Name = "Seller", IsActive = true };
        public Role AdminB { get; } = new() { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true, IsActive = true };

        public World()
        {
            MainB = BranchBuilder.MainBranch(CompanyB);
            Sam = Rbac.NewUser("sam");
            Sam.PasswordHash = Hasher.Hash("Passw0rd!23");
            using (var seed = new SelloraDbContext(Options, TestCompany.Context, TestCompany.Branch))
            {
                var permissions = Sellora.Domain.Permissions.PermissionCatalog.All.Select(p => new Permission { Id = Guid.NewGuid(), Code = p.Code, Module = p.Module, Action = p.Action, Description = p.Description }).ToList();
                seed.Permissions.AddRange(permissions);
                seed.Companies.Add(new Company { Id = TestCompany.DefaultId, Code = "DEFAULT", Name = "Company A", CreatedAt = DateTimeOffset.UtcNow.AddDays(-10) });
                seed.Companies.Add(new Company { Id = CompanyB, Code = "B", Name = "Company B", CreatedAt = DateTimeOffset.UtcNow.AddDays(-5) });
                seed.Users.Add(Sam);
                seed.Branches.AddRange(MainA, ExtraA);
                SellerA.RolePermissions.Add(new RolePermission { RoleId = SellerA.Id, PermissionId = permissions.Single(p => p.Code == "Sales.View").Id });
                seed.Roles.Add(SellerA);
                var m = Rbac.Join(seed, Sam, SellerA).Membership;
                m.CreatedAt = DateTimeOffset.UtcNow.AddDays(-9);
                seed.SaveChanges();
            }
            using var b = new SelloraDbContext(Options, TestCompany.For(CompanyB), TestCompany.Branch);
            b.Branches.Add(MainB);
            b.Roles.Add(AdminB);
            var mb = new UserCompanyMembership { Id = Guid.NewGuid(), UserId = Sam.Id, CompanyId = CompanyB, Status = MembershipStatus.Active, CreatedAt = DateTimeOffset.UtcNow.AddDays(-4) };
            b.UserCompanyMemberships.Add(mb); // the user row already exists (platform table): only the membership and the assignment belong to company B
            b.UserRoleAssignments.Add(Rbac.Assignment(mb, AdminB));
            b.SaveChanges();
        }

        public AuthService Service(bool withCache = true)
        {
            // no company context: exactly the state of login/refresh/switch
            var db = new SelloraDbContext(Options, new CompanyContext(), TestCompany.Branch);
            var tokens = new JwtTokenService(Microsoft.Extensions.Options.Options.Create(new JwtOptions { Issuer = "t", Audience = "t", SigningKey = Convert.ToBase64String(new byte[32]), AccessTokenMinutes = 15, RefreshTokenDays = 14 }));
            return new AuthService(db, Hasher, tokens, withCache ? Rbac.Effective(db, new MembershipContext()) : null);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoginAsync_SharedUser_UsesTheOldestMembership_WithThatCompanysRoleAndPermissions(bool withCache)
    {
        var w = new World();

        var result = await w.Service(withCache).LoginAsync("sam", "Passw0rd!23");

        Assert.True(result.Succeeded);
        Assert.Equal((TestCompany.DefaultId, "Seller"), (result.Auth!.User.CompanyId, result.Auth.User.RoleName));
        Assert.Equal(["Sales.View"], result.Auth.User.Permissions.ToArray());
        Assert.Equal([w.SellerA.Id], result.Auth.User.Roles!.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task SwitchCompanyAsync_EvaluatesTheOtherCompanysAdministrator_NotTheFirstCompanysRole()
    {
        var w = new World();
        var login = (await w.Service().LoginAsync("sam", "Passw0rd!23")).Auth!;

        var switched = await w.Service().SwitchCompanyAsync(w.Sam.Id, CompanyB, login.RefreshToken);

        Assert.True(switched.Succeeded);
        var user = switched.Auth!.User;
        Assert.Equal((CompanyB, "Administrator"), (user.CompanyId, user.RoleName));
        Assert.Equal(Sellora.Domain.Permissions.PermissionCatalog.All.Count, user.Permissions.Count);
        Assert.Equal([w.AdminB.Id], user.Roles!.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task RefreshAsync_KeepsTheCompany_AndReEvaluatesRolesEdited_InBetween()
    {
        var w = new World();
        var login = (await w.Service(withCache: false).LoginAsync("sam", "Passw0rd!23")).Auth!;
        await using (var db = new SelloraDbContext(w.Options, TestCompany.Context, TestCompany.Branch))
        {
            var perm = db.Permissions.AsEnumerable().Single(p => p.Code == "Purchase.View");
            db.RolePermissions.Add(new RolePermission { RoleId = w.SellerA.Id, PermissionId = perm.Id });
            await db.SaveChangesAsync();
        }

        var refreshed = await w.Service(withCache: false).RefreshAsync(login.RefreshToken);

        Assert.True(refreshed.Succeeded);
        Assert.Equal(TestCompany.DefaultId, refreshed.Auth!.User.CompanyId);
        Assert.Equal(["Purchase.View", "Sales.View"], refreshed.Auth.User.Permissions.Order().ToArray());
    }

    [Fact]
    public async Task LoginAsync_AssignedBranchesUser_ReceivesCompanyCodesPlusBranchCodesOfTheWorkingBranchOnly()
    {
        var w = new World();
        string[] codes = ["Sales.Create"];
        await using (var db = new SelloraDbContext(w.Options, TestCompany.Context, TestCompany.Branch))
        {
            var main = await db.Branches.SingleAsync(b => b.Id == w.MainA.Id);
            var extra = await db.Branches.SingleAsync(b => b.Id == w.ExtraA.Id);
            var mainRole = new Role { Id = Guid.NewGuid(), Name = "Main cashier", ScopeType = RoleScope.Branch, BranchId = main.Id };
            var extraRole = new Role { Id = Guid.NewGuid(), Name = "Extra cashier", ScopeType = RoleScope.Branch, BranchId = extra.Id };
            foreach (var role in new[] { mainRole, extraRole })
                role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = db.Permissions.AsEnumerable().Single(p => p.Code == codes[0]).Id });
            extraRole.RolePermissions.Add(new RolePermission { RoleId = extraRole.Id, PermissionId = db.Permissions.AsEnumerable().Single(p => p.Code == "Reports.Sales").Id });
            db.Roles.AddRange(mainRole, extraRole);
            var scoped = Rbac.NewUser("scoped");
            scoped.PasswordHash = w.Hasher.Hash("Passw0rd!23");
            var (m, _) = Rbac.Join(db, scoped, w.SellerA, scope: CompanyAccessScope.AssignedBranches);
            db.UserBranchAssignments.AddRange(
                new UserBranchAssignment { Id = Guid.NewGuid(), CompanyId = TestCompany.DefaultId, UserCompanyMembershipId = m.Id, BranchId = main.Id, IsPrimary = false },
                new UserBranchAssignment { Id = Guid.NewGuid(), CompanyId = TestCompany.DefaultId, UserCompanyMembershipId = m.Id, BranchId = extra.Id, IsPrimary = true });
            db.UserRoleAssignments.AddRange(Rbac.Assignment(m, mainRole), Rbac.Assignment(m, extraRole));
            await db.SaveChangesAsync();
        }

        var result = await w.Service().LoginAsync("scoped", "Passw0rd!23");

        Assert.True(result.Succeeded);
        Assert.Equal(["Reports.Sales", "Sales.Create", "Sales.View"], result.Auth!.User.Permissions.Order().ToArray()); // primary = Extra: Seller + Extra's codes; Main's role does not apply here
        Assert.Equal(3, result.Auth.User.Roles!.Count);
        Assert.Equal(2, result.Auth.User.Roles.Count(r => r.ScopeType == RoleScope.Branch));
    }

    [Fact]
    public async Task LoginAsync_BranchRoleWithoutAnAllocationAtItsBranch_GrantsNothing()
    {
        var w = new World();
        await using (var db = new SelloraDbContext(w.Options, TestCompany.Context, TestCompany.Branch))
        {
            var role = new Role { Id = Guid.NewGuid(), Name = "Extra cashier", ScopeType = RoleScope.Branch, BranchId = w.ExtraA.Id };
            role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = db.Permissions.AsEnumerable().Single(p => p.Code == "Sales.Create").Id });
            db.Roles.Add(role);
            var user = Rbac.NewUser("orphan");
            user.PasswordHash = w.Hasher.Hash("Passw0rd!23");
            var (m, _) = Rbac.Join(db, user, role, scope: CompanyAccessScope.AssignedBranches, branchId: w.ExtraA.Id);
            db.UserBranchAssignments.Add(new UserBranchAssignment { Id = Guid.NewGuid(), CompanyId = TestCompany.DefaultId, UserCompanyMembershipId = m.Id, BranchId = w.MainA.Id, IsPrimary = true });
            await db.SaveChangesAsync();
        }

        var result = await w.Service().LoginAsync("orphan", "Passw0rd!23");

        Assert.True(result.Succeeded);
        Assert.Empty(result.Auth!.User.Permissions);
    }

    [Fact]
    public async Task AccessToken_CarriesNoPermissionOrRoleNameClaims()
    {
        var w = new World();

        var result = await w.Service().LoginAsync("sam", "Passw0rd!23");

        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(result.Auth!.AccessToken);
        Assert.DoesNotContain(jwt.Claims, c => c.Type is AuthClaimTypes.Permission or AuthClaimTypes.RoleName);
        Assert.Contains(jwt.Claims, c => c.Type == AuthClaimTypes.CompanyId && c.Value == TestCompany.DefaultId.ToString());
    }
}
