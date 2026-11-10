using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Domain.Roles;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.TestData;

/// <summary>
/// InMemory company with Main plus branches B1 and B2, the permission rows, a protected Administrator and helpers that give a user a membership,
/// role assignments and branch allocations. Used by the effective-permission, handler and role-management unit tests.
/// </summary>
public sealed class RbacWorld
{
    public SelloraDbContext Db { get; } = TestDb.NewInMemory();
    public Dictionary<string, Permission> Permissions { get; }
    public Branch B1 { get; } = BranchBuilder.Extra("B1");
    public Branch B2 { get; } = BranchBuilder.Extra("B2");
    public Role Administrator { get; }

    public RbacWorld()
    {
        Permissions = PermissionCatalog.All
            .Select(p => new Permission { Id = Guid.NewGuid(), Code = p.Code, Module = p.Module, Action = p.Action, Description = p.Description })
            .ToDictionary(p => p.Code);
        Db.Permissions.AddRange(Permissions.Values);
        Administrator = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        Db.AddRange(B1, B2, Administrator);
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
    }

    public Role CompanyRole(string name, params string[] codes) => Save(Rbac.RoleWith(name, Permissions, codes));

    public Role BranchRole(string name, Branch branch, params string[] codes)
    {
        var role = Rbac.RoleWith(name, Permissions, codes);
        role.ScopeType = RoleScope.Branch;
        role.BranchId = branch.Id;
        return Save(role);
    }

    private Role Save(Role role)
    {
        Db.Roles.Add(role);
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
        return role;
    }

    /// <summary>A user with an Active membership (default company) and no roles yet.</summary>
    public (User User, UserCompanyMembership Membership) AddMember(string name, CompanyAccessScope scope = CompanyAccessScope.CompanyWide,
        MembershipStatus status = MembershipStatus.Active, Guid? companyId = null, User? existing = null)
    {
        var user = existing ?? Rbac.NewUser(name);
        var m = new UserCompanyMembership
        {
            Id = Guid.NewGuid(), UserId = user.Id, CompanyId = companyId ?? TestCompany.DefaultId, Status = status, AccessScope = scope,
        };
        if (existing is null) Db.Users.Add(user);
        Db.UserCompanyMemberships.Add(m);
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
        return (user, m);
    }

    public UserRoleAssignment Assign(UserCompanyMembership m, Role role)
    {
        var a = Rbac.Assignment(m, role);
        Db.UserRoleAssignments.Add(a);
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
        return a;
    }

    public UserBranchAssignment Allocate(UserCompanyMembership m, Branch branch, bool primary = false)
    {
        var a = new UserBranchAssignment
        {
            Id = Guid.NewGuid(), CompanyId = m.CompanyId, UserCompanyMembershipId = m.Id, BranchId = branch.Id, IsPrimary = primary,
            Status = AssignmentStatus.Active, StartAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        Db.UserBranchAssignments.Add(a);
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
        return a;
    }

    /// <summary>Shortcut: a CompanyWide member holding the role.</summary>
    public (User User, UserCompanyMembership Membership) CompanyWideWith(string name, Role role)
    {
        var (u, m) = AddMember(name);
        Assign(m, role);
        return (u, m);
    }

    /// <summary>Shortcut: an AssignedBranches member allocated to <paramref name="branches"/> holding <paramref name="role"/> (the first branch is primary).</summary>
    public (User User, UserCompanyMembership Membership) BranchMemberWith(string name, Role role, params Branch[] branches)
    {
        var (u, m) = AddMember(name, CompanyAccessScope.AssignedBranches);
        for (var i = 0; i < branches.Length; i++) Allocate(m, branches[i], primary: i == 0);
        Assign(m, role);
        return (u, m);
    }

    public async Task SetRoleActiveAsync(Role role, bool active)
    {
        var tracked = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(Db.Roles, r => r.Id == role.Id);
        tracked.IsActive = active;
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    public async Task BumpAsync(UserCompanyMembership m)
    {
        var tracked = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(Db.UserCompanyMemberships, x => x.Id == m.Id);
        tracked.AccessVersion++;
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    /// <summary>The role-management actor the controller would build after the advisory lock: fresh effective set, administrator flag and membership scope.</summary>
    public async Task<RoleActor> ActorOfAsync(User user, UserCompanyMembership membership)
    {
        Db.ChangeTracker.Clear();
        var fresh = await Db.UserCompanyMemberships.AsNoTracking().SingleAsync(m => m.Id == membership.Id);
        var set = await EffectivePermissionService.LoadAsync(Db, membership.Id, default);
        return new RoleActor(user.Id, membership.Id, set.IsAdministrator, fresh.AccessScope == CompanyAccessScope.CompanyWide, set, "test");
    }

    public RoleService Roles { get { Db.ChangeTracker.Clear(); return new RoleService(Db); } }

    public RoleAssignmentService Assignments { get { Db.ChangeTracker.Clear(); return new RoleAssignmentService(Db, new UserAccessService(Db)); } }

    public async Task<long> VersionOfAsync(UserCompanyMembership m)
    {
        Db.ChangeTracker.Clear();
        return (await Db.UserCompanyMemberships.AsNoTracking().SingleAsync(x => x.Id == m.Id)).AccessVersion;
    }

    public async Task<List<UserRoleAssignment>> AssignmentsOfAsync(UserCompanyMembership m)
    {
        Db.ChangeTracker.Clear();
        return await Db.UserRoleAssignments.AsNoTracking().Where(a => a.UserCompanyMembershipId == m.Id).OrderBy(a => a.StartAt).ToListAsync();
    }

    public async Task<List<string>> CodesOfAsync(Guid roleId)
    {
        Db.ChangeTracker.Clear();
        return await Db.RolePermissions.AsNoTracking().Where(rp => rp.RoleId == roleId).Select(rp => rp.Permission.Code).OrderBy(c => c).ToListAsync();
    }

    public async Task<List<AuditEvent>> AuditAsync(string action)
    {
        Db.ChangeTracker.Clear();
        return await Db.AuditEvents.AsNoTracking().Where(a => a.Action == action).ToListAsync();
    }
}
