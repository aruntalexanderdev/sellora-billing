using Sellora.Domain.Abstractions;
using Sellora.Domain.Permissions;

namespace Sellora.Tests.Roles;

/// <summary>Phase 7 E1-E5 on the pure set: company-level codes, branch-level codes, the Administrator, the working-branch list and the permitted-branches rule the handler narrows by.</summary>
public class EffectivePermissionSetTests
{
    private static readonly Guid B1 = Guid.NewGuid(), B2 = Guid.NewGuid();

    private static EffectivePermissionSet Set(bool admin = false, string[]? company = null, Dictionary<Guid, string[]>? branch = null) =>
        new(admin, (company ?? []).ToHashSet(), (branch ?? []).ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value.ToHashSet()));

    [Fact]
    public void None_HoldsNothingAnywhere()
    {
        Assert.False(EffectivePermissionSet.None.HasCompanyLevel("Sales.View"));
        Assert.Empty(EffectivePermissionSet.None.CodesAtBranch(B1));
        Assert.Empty(EffectivePermissionSet.None.PermittedBranches("Sales.View")!);
    }

    [Fact]
    public void PermittedBranches_CompanyLevelCode_IsNull_MeaningEveryAccessibleBranch()
    {
        var set = Set(company: ["Sales.View"], branch: new() { [B1] = ["Sales.View"] });

        Assert.Null(set.PermittedBranches("Sales.View"));
    }

    [Fact]
    public void PermittedBranches_BranchOnlyCode_ListsExactlyTheBranchesWhoseRolesHoldIt()
    {
        var set = Set(branch: new() { [B1] = ["Sales.View", "Purchase.View"], [B2] = ["Sales.View"] });

        Assert.Equal(new[] { B1, B2 }.Order(), set.PermittedBranches("Sales.View")!.Order());
        Assert.Equal([B1], set.PermittedBranches("Purchase.View")!);
        Assert.Empty(set.PermittedBranches("Sales.Create")!);
    }

    [Fact]
    public void Administrator_HoldsEveryCodeCompanyWide_AndTheWholeCatalogueAtAnyBranch()
    {
        var set = Set(admin: true);

        Assert.True(set.HasCompanyLevel("Reports.Profit"));
        Assert.Null(set.PermittedBranches("Anything.Really"));
        Assert.Equal(PermissionCatalog.All.Count, set.CodesAtBranch(B1).Count);
        Assert.Equal(PermissionCatalog.All.Count, set.CodesAtBranch(null).Count);
    }

    [Fact]
    public void CodesAtBranch_IsCompanyCodesPlusThatBranchsCodesOnly()
    {
        var set = Set(company: ["Purchase.View"], branch: new() { [B1] = ["Sales.View"], [B2] = ["Sales.Create"] });

        Assert.Equal(["Purchase.View", "Sales.View"], set.CodesAtBranch(B1).Order());
        Assert.Equal(["Purchase.View", "Sales.Create"], set.CodesAtBranch(B2).Order());
        Assert.Equal(["Purchase.View"], set.CodesAtBranch(null));
        Assert.Equal(["Purchase.View"], set.CodesAtBranch(Guid.NewGuid()));
    }

    [Fact]
    public void HasAtBranch_CompanyCodeAnywhere_BranchCodeOnlyThere()
    {
        var set = Set(company: ["Purchase.View"], branch: new() { [B1] = ["Sales.View"] });

        Assert.True(set.HasAtBranch("Purchase.View", B2));
        Assert.True(set.HasAtBranch("Sales.View", B1));
        Assert.False(set.HasAtBranch("Sales.View", B2));
    }
}
