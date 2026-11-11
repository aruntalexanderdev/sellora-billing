using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;

namespace Sellora.Tests.Permissions;

/// <summary>
/// Phase 6 T6 and the seed-script drift test: the governance catalogue is disjoint from the permission catalogue, never reaches a role,
/// the JWT or the resolver, and scripts/sql/seed-permissions.sql mirrors it exactly (code and description) with the deterministic ids.
/// </summary>
public partial class GovernanceCatalogueTests
{
    [GeneratedRegex(@"^\s*\('(Company\.Governance\.[A-Za-z]+)',\s*'((?:[^']|'')*)'\),?\s*$", RegexOptions.Multiline)]
    private static partial Regex GovernanceRow();

    private static List<(string Code, string Description)> ScriptRows() =>
        GovernanceRow().Matches(File.ReadAllText(PermissionSeedScriptTests.ScriptPath()))
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value.Replace("''", "'"))).ToList();

    [Fact]
    public void Catalogue_HasExactlyTheFiveCodes_WithDescriptions()
    {
        Assert.Equal(
            ["Company.Governance.AssignPermissions", "Company.Governance.ManageAdministrators", "Company.Governance.ManageMembers", "Company.Governance.ManageOwners", "Company.Governance.View"],
            GovernancePermissionCatalog.All.Select(d => d.Code).Order(StringComparer.Ordinal).ToArray());
        Assert.All(GovernancePermissionCatalog.All, d => Assert.False(string.IsNullOrWhiteSpace(d.Description)));
        Assert.All(GovernancePermissionCatalog.All, d => Assert.StartsWith(GovernancePermissionCatalog.Prefix, d.Code));
    }

    [Fact]
    public void GovernanceCatalogue_IsDisjointFromThePermissionCatalogue_AndNoPermissionHasTheGovernancePrefix()
    {
        var governance = GovernancePermissionCatalog.All.Select(d => d.Code).ToHashSet();
        var permissions = PermissionCatalog.All.Select(p => p.Code).ToHashSet();

        Assert.Empty(governance.Intersect(permissions));
        Assert.DoesNotContain(permissions, c => c.StartsWith(GovernancePermissionCatalog.Prefix, StringComparison.Ordinal));
    }

    [Fact]
    public void SystemAdministrator_ResolvesNoGovernanceCode_AndNeitherDoesAnyRole()
    {
        var admin = PermissionResolver.Resolve(new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true, IsActive = true, RolePermissions = [] });
        var clerk = PermissionResolver.Resolve(new Role { Id = Guid.NewGuid(), Name = "Clerk", IsSystem = false, IsActive = true, RolePermissions = [] });

        Assert.Equal(PermissionCatalog.All.Count, admin.Count);
        Assert.DoesNotContain(admin, c => c.StartsWith(GovernancePermissionCatalog.Prefix, StringComparison.Ordinal));
        Assert.DoesNotContain(clerk, c => c.StartsWith(GovernancePermissionCatalog.Prefix, StringComparison.Ordinal));
    }

    [Fact]
    public void GovernanceCodes_AreNotAClaimInTheDefaultTokenShape()
    {
        // Governance is read from the database per request (D9): the permission claim type must never carry a governance code.
        var admin = PermissionResolver.Resolve(new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true, IsActive = true, RolePermissions = [] });

        Assert.Empty(admin.Intersect(GovernancePermissionCatalog.All.Select(d => d.Code)));
        Assert.Equal("sellora_session", AuthClaimTypes.SessionKind);
        Assert.Equal("platform", AuthClaimTypes.PlatformSession);
    }

    [Fact]
    public void Ids_AreDeterministicMd5OfTheDocumentedStrings()
    {
        static Guid Md5(string text) => Guid.Parse(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))));
        var member = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var memberId = Md5("gov:" + member.ToString("D"));

        Assert.Equal(memberId, GovernancePermissionCatalog.MemberIdFor(member));
        foreach (var d in GovernancePermissionCatalog.All)
        {
            Assert.Equal(Md5("govperm:" + d.Code), GovernancePermissionCatalog.IdFor(d.Code));
            Assert.Equal(Md5("govgrant:" + memberId.ToString("D") + d.Code), GovernancePermissionCatalog.GrantIdFor(memberId, d.Code));
        }
    }

    [Fact]
    public void SeedScript_GovernanceRows_EqualTheCatalogue_CodeAndDescription()
    {
        var script = ScriptRows().OrderBy(r => r.Code, StringComparer.Ordinal).ToList();
        var catalogue = GovernancePermissionCatalog.All.Select(d => (d.Code, d.Description)).OrderBy(r => r.Code, StringComparer.Ordinal).ToList();

        Assert.Equal(catalogue, script);
    }

    [Fact]
    public void SeedScript_PermissionRowsNeverContainGovernanceCodes_AndTheGovernanceInsertIsIdempotent()
    {
        var sql = File.ReadAllText(PermissionSeedScriptTests.ScriptPath());

        Assert.DoesNotContain(PermissionSeedScriptTests.ParseScript(), r => r.Code.StartsWith(GovernancePermissionCatalog.Prefix, StringComparison.Ordinal));
        var governanceInsert = sql[sql.IndexOf("INSERT INTO \"GovernancePermissions\"", StringComparison.Ordinal)..];
        Assert.Contains("ON CONFLICT", governanceInsert);
        Assert.Contains("md5('govperm:'", governanceInsert);
        Assert.DoesNotMatch(@"(?im)^\s*(UPDATE|DELETE|TRUNCATE|DROP)\b", sql);
    }
}
