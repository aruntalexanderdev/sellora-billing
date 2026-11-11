using System.Security.Cryptography;
using System.Text;

namespace Sellora.Domain.Permissions;

/// <summary>
/// Governance permission catalogue (Phase 6 D8). Deliberately NOT part of <see cref="PermissionCatalog"/>: these codes live in the
/// GovernancePermissions table, are never JWT claims and are never resolved for the system Administrator role.
/// Mirrored by the migration, DbSeeder and scripts/sql/seed-permissions.sql (a test guards drift).
/// </summary>
public static class GovernancePermissionCatalog
{
    public const string Prefix = "Company.Governance.";

    public const string View = "Company.Governance.View";
    public const string ManageMembers = "Company.Governance.ManageMembers";
    public const string ManageOwners = "Company.Governance.ManageOwners";
    public const string AssignPermissions = "Company.Governance.AssignPermissions";
    public const string ManageAdministrators = "Company.Governance.ManageAdministrators";

    public static readonly IReadOnlyList<GovernancePermissionDefinition> All = new List<GovernancePermissionDefinition>
    {
        new(View, "View governance members, their titles and grants, and the governance audit trail."),
        new(ManageMembers, "Add, retitle and end governance members other than owners."),
        new(ManageOwners, "Add, retitle, end or change the grants of Owner-title governance members."),
        new(AssignPermissions, "Grant and revoke governance permissions (only codes the actor holds)."),
        new(ManageAdministrators, "Grant and remove the Administrator role (delegated company administration)."),
    };

    /// <summary>Deterministic catalogue id = md5('govperm:' || code) read as a uuid (identical to the migration SQL).</summary>
    public static Guid IdFor(string code) => Md5Guid("govperm:" + code);

    /// <summary>Deterministic member id used by the migration backfill: md5('gov:' || membershipId).</summary>
    public static Guid MemberIdFor(Guid membershipId) => Md5Guid("gov:" + membershipId.ToString("D"));

    /// <summary>Deterministic grant id used by the migration backfill: md5('govgrant:' || memberId || code).</summary>
    public static Guid GrantIdFor(Guid memberId, string code) => Md5Guid("govgrant:" + memberId.ToString("D") + code);

    private static Guid Md5Guid(string text) => Guid.Parse(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))));
}

public sealed record GovernancePermissionDefinition(string Code, string Description);
