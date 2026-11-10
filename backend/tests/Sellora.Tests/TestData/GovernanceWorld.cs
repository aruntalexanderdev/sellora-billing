using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.TestData;

/// <summary>
/// Phase 6 fixture on EF InMemory: the five governance catalogue rows, a system Administrator role and a plain Staff role, and helpers that
/// add users with an Active / CompanyWide membership and (optionally) a governance member with grants. Services are rebuilt on a cleared
/// change tracker for every call, so each call reads the saved state like a new request would.
/// </summary>
public sealed class GovernanceWorld
{
    public const string View = GovernancePermissionCatalog.View;
    public const string Members = GovernancePermissionCatalog.ManageMembers;
    public const string Owners = GovernancePermissionCatalog.ManageOwners;
    public const string Assign = GovernancePermissionCatalog.AssignPermissions;
    public const string Admins = GovernancePermissionCatalog.ManageAdministrators;

    public static readonly string[] AllCodes = [View, Members, Owners, Assign, Admins];

    public SelloraDbContext Db { get; } = TestDb.NewInMemory();
    public Role AdminRole { get; } = new() { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true, IsActive = true };
    public Role StaffRole { get; } = new() { Id = Guid.NewGuid(), Name = "Staff", IsSystem = false, IsActive = true };
    public Role OtherRole { get; } = new() { Id = Guid.NewGuid(), Name = "Other", IsSystem = false, IsActive = true };

    public GovernanceWorld()
    {
        Db.Roles.AddRange(AdminRole, StaffRole, OtherRole);
        foreach (var def in GovernancePermissionCatalog.All)
        {
            Db.GovernancePermissions.Add(new GovernancePermission { Id = GovernancePermissionCatalog.IdFor(def.Code), Code = def.Code, Description = def.Description });
        }
        Db.SaveChanges();
    }

    public GovernanceService Service
    {
        get
        {
            Db.ChangeTracker.Clear();
            return new GovernanceService(Db, new UserAccessService(Db));
        }
    }

    public UserAccessService Access
    {
        get
        {
            Db.ChangeTracker.Clear();
            return new UserAccessService(Db);
        }
    }

    public User AddUser(string name, Role? role = null, UserStatus status = UserStatus.Active, MembershipStatus membership = MembershipStatus.Active,
        CompanyAccessScope scope = CompanyAccessScope.CompanyWide)
    {
        role ??= StaffRole;
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = name, LastName = "T", Username = name.ToLowerInvariant(), Email = $"{name.ToLowerInvariant()}@x.io",
            PasswordHash = "x", RoleId = role.Id, Status = status,
        };
        Db.Users.Add(user);
        Db.UserCompanyMemberships.Add(new UserCompanyMembership
        {
            Id = Guid.NewGuid(), UserId = user.Id, CompanyId = TestCompany.DefaultId, Status = membership, AccessScope = scope,
        });
        Db.SaveChanges();
        return user;
    }

    /// <summary>A governance member with the title and exactly the given grants (the migration/seeder shape).</summary>
    public CompanyGovernanceMember AddMember(User user, GovernanceTitle title, params string[] codes)
    {
        var membership = Db.UserCompanyMemberships.AsEnumerable().Single(m => m.UserId == user.Id && m.CompanyId == TestCompany.DefaultId);
        var member = new CompanyGovernanceMember
        {
            Id = Guid.NewGuid(), CompanyId = TestCompany.DefaultId, UserCompanyMembershipId = membership.Id, Title = title,
            Status = GovernanceMemberStatus.Active, StartAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        Db.CompanyGovernanceMembers.Add(member);
        foreach (var code in codes)
        {
            Db.GovernanceMemberPermissions.Add(new GovernanceMemberPermission
            {
                Id = Guid.NewGuid(), CompanyId = TestCompany.DefaultId, GovernanceMemberId = member.Id, GovernancePermissionId = GovernancePermissionCatalog.IdFor(code),
            });
        }
        Db.SaveChanges();
        return member;
    }

    /// <summary>An administrator Owner with all five codes (what the Phase 6 migration produces).</summary>
    public (User User, CompanyGovernanceMember Member) AddOwner(string name) =>
        AddOwnerWith(name, AllCodes);

    public (User User, CompanyGovernanceMember Member) AddOwnerWith(string name, params string[] codes)
    {
        var user = AddUser(name, AdminRole);
        return (user, AddMember(user, GovernanceTitle.Owner, codes));
    }

    public void MakePlatformAdmin(User user, PlatformAdminStatus status = PlatformAdminStatus.Active)
    {
        Db.PlatformAdministrators.Add(new PlatformAdministrator
        {
            UserId = user.Id, Status = status, GrantedAt = DateTimeOffset.UtcNow,
            RevokedAt = status == PlatformAdminStatus.Active ? null : DateTimeOffset.UtcNow,
        });
        Db.SaveChanges();
    }

    public static GovernanceActor Actor(User user) => new(user.Id, "test-correlation");

    // ---- reads of the saved state ----

    public CompanyGovernanceMember Reload(CompanyGovernanceMember member)
    {
        Db.ChangeTracker.Clear();
        return Db.CompanyGovernanceMembers.AsEnumerable().Single(m => m.Id == member.Id);
    }

    public List<string> CodesOf(CompanyGovernanceMember member)
    {
        Db.ChangeTracker.Clear();
        return Db.GovernanceMemberPermissions.Where(p => p.GovernanceMemberId == member.Id).Select(p => p.GovernancePermission.Code).AsEnumerable().Order().ToList();
    }

    public long AccessVersion(User user)
    {
        Db.ChangeTracker.Clear();
        return Db.UserCompanyMemberships.AsEnumerable().Single(m => m.UserId == user.Id && m.CompanyId == TestCompany.DefaultId).AccessVersion;
    }

    public List<AuditEvent> Audit(string action)
    {
        Db.ChangeTracker.Clear();
        return Db.AuditEvents.AsEnumerable().Where(a => a.Action == action).ToList();
    }
}
