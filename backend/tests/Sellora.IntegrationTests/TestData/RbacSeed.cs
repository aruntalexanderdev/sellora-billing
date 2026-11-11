using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.IntegrationTests.TestData;

/// <summary>
/// Phase 7 fixtures on PostgreSQL: users hold roles through <c>UserRoleAssignments</c> (Users.RoleId is ignored by evaluation). EF helpers for a context already set to the
/// company, and a raw-SQL form for fixtures that write users with SQL.
/// </summary>
public static class RbacSeed
{
    /// <summary>Adds an Active company-role assignment of <paramref name="roleId"/> to <paramref name="membership"/> (the caller saves).</summary>
    public static UserRoleAssignment Assign(SelloraDbContext db, UserCompanyMembership membership, Guid roleId, RoleScope scope = RoleScope.Company, Guid? branchId = null)
    {
        var row = new UserRoleAssignment
        {
            Id = Guid.NewGuid(), CompanyId = membership.CompanyId, UserCompanyMembershipId = membership.Id, RoleId = roleId, ScopeType = scope, BranchId = branchId,
            Status = AssignmentStatus.Active, StartAt = DateTimeOffset.UtcNow.AddMinutes(-1), CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        db.UserRoleAssignments.Add(row);
        return row;
    }

    /// <summary>The company's protected Administrator role (Phase 7 D9): added under a context set to <paramref name="db"/>'s company; the caller saves.</summary>
    public static Role AddAdministratorRole(SelloraDbContext db) =>
        db.Roles.Add(new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true, IsActive = true, Description = "Full access" }).Entity;

    /// <summary>SQL that gives the user's membership in <paramref name="companyId"/> an Active company-role assignment of the role selected by <paramref name="roleIdSql"/> (a uuid literal or scalar subquery).</summary>
    public static string AssignSql(Guid userId, string companyId, string roleIdSql) => $"""
        INSERT INTO "UserRoleAssignments" ("Id", "CompanyId", "UserCompanyMembershipId", "RoleId", "ScopeType", "Status", "StartAt", "CreatedAt")
        SELECT gen_random_uuid(), m."CompanyId", m."Id", {roleIdSql}, 'Company', 'Active', now() - interval '1 minute', now() - interval '1 minute'
        FROM "UserCompanyMemberships" m WHERE m."UserId" = '{userId}' AND m."CompanyId" = '{companyId}';
        """;
}
