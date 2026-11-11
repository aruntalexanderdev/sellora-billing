using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.TestData;

/// <summary>
/// Phase 7 fixtures on EF InMemory: users hold roles through <see cref="UserRoleAssignment"/> rows (Users.RoleId is deprecated and ignored by evaluation).
/// <see cref="Join"/> adds a user, an Active membership in a company and an Active assignment of a role in one call.
/// </summary>
public static class Rbac
{
    /// <summary>Adds <paramref name="user"/> (when not tracked yet), an Active membership and an Active assignment of <paramref name="role"/>; the caller saves.</summary>
    public static (UserCompanyMembership Membership, UserRoleAssignment Assignment) Join(
        SelloraDbContext db, User user, Role role, Guid? companyId = null, CompanyAccessScope scope = CompanyAccessScope.CompanyWide,
        MembershipStatus status = MembershipStatus.Active, Guid? branchId = null)
    {
        if (db.Entry(user).State == EntityState.Detached && !db.Users.Local.Contains(user)) db.Users.Add(user);
        var cid = companyId ?? TestCompany.DefaultId;
        var membership = new UserCompanyMembership
        {
            Id = Guid.NewGuid(), UserId = user.Id, CompanyId = cid, Status = status, AccessScope = scope,
        };
        var assignment = Assignment(membership, role, branchId);
        db.UserCompanyMemberships.Add(membership);
        db.UserRoleAssignments.Add(assignment);
        return (membership, assignment);
    }

    /// <summary>An Active assignment of <paramref name="role"/> to <paramref name="membership"/> (a branch role needs <paramref name="branchId"/>).</summary>
    public static UserRoleAssignment Assignment(UserCompanyMembership membership, Role role, Guid? branchId = null) => new()
    {
        Id = Guid.NewGuid(), CompanyId = membership.CompanyId, UserCompanyMembershipId = membership.Id, RoleId = role.Id,
        ScopeType = role.ScopeType, BranchId = role.ScopeType == RoleScope.Branch ? branchId ?? role.BranchId : null,
        Status = AssignmentStatus.Active, StartAt = DateTimeOffset.UtcNow.AddMinutes(-1),
    };

    public static User NewUser(string username) => new()
    {
        Id = Guid.NewGuid(), FirstName = username, LastName = "Test", Username = username, Email = $"{username}@selloramart.in",
        PasswordHash = "x", Status = UserStatus.Active,
    };

    /// <summary>Role with the given catalogue codes (the permission rows must already exist in <paramref name="permissions"/>).</summary>
    public static Role RoleWith(string name, IReadOnlyDictionary<string, Permission> permissions, params string[] codes)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = name };
        foreach (var code in codes) role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permissions[code].Id });
        return role;
    }

    /// <summary>Real evaluation service over <paramref name="db"/> with the given membership context (shared cache when supplied).</summary>
    public static EffectivePermissionService Effective(SelloraDbContext db, IMembershipContext membership, IMemoryCache? cache = null) =>
        new(db, cache ?? new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 }), membership);

    /// <summary>Membership context set to the actor's current membership and AccessVersion (what the middleware does per request).</summary>
    public static MembershipContext ContextOf(SelloraDbContext db, Guid userId, Guid? companyId = null)
    {
        var cid = companyId ?? TestCompany.DefaultId;
        var m = db.UserCompanyMemberships.AsNoTracking().AsEnumerable().FirstOrDefault(x => x.UserId == userId && x.CompanyId == cid && x.Status == MembershipStatus.Active);
        var context = new MembershipContext();
        if (m is not null) context.Set(m.Id, m.AccessVersion);
        return context;
    }

    /// <summary>Active role ids (company and branch scope) of the user's membership in the default company.</summary>
    public static async Task<List<Guid>> ActiveRoleIdsAsync(SelloraDbContext db, Guid userId, Guid? companyId = null)
    {
        var cid = companyId ?? TestCompany.DefaultId;
        db.ChangeTracker.Clear();
        return await db.UserRoleAssignments.AsNoTracking()
            .Where(a => a.CompanyId == cid && a.Status == AssignmentStatus.Active && db.UserCompanyMemberships.Any(m => m.Id == a.UserCompanyMembershipId && m.UserId == userId))
            .Select(a => a.RoleId).ToListAsync();
    }
}
