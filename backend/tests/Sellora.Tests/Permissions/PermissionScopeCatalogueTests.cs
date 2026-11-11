using Sellora.Domain.Permissions;

namespace Sellora.Tests.Permissions;

/// <summary>Phase 7 D5/Q2: the permission scope metadata lives in the catalogue. The conservative branch-eligible list is pinned here, so widening it (Phase 8) is a deliberate change.</summary>
public class PermissionScopeCatalogueTests
{
    private static readonly string[] ExpectedBranchEligible =
    [
        "Sales.View", "Sales.Create", "Sales.PriceOverride", "Sales.Edit", "Sales.Receive", "Sales.Return", "Sales.Refund", "Sales.Cancel",
        "Purchase.View", "Purchase.Create", "Purchase.Edit", "Purchase.Pay", "Purchase.Return", "Purchase.Refund", "Purchase.Cancel",
        "Inventory.Adjust", "StockLocations.View", "Inventory.View",
        "StockTransfers.View", "StockTransfers.Create", "StockTransfers.Approve", "StockTransfers.Dispatch", "StockTransfers.Receive", "StockTransfers.Cancel",
        "Expenses.View", "Expenses.Create", "Expenses.Edit", "Expenses.Cancel",
        "Customers.View",
        "Reports.Sales", "Reports.Purchase", "Reports.Stock", "Reports.Movement",
        "Branch.Roles.View", "Branch.Roles.Manage", "Branch.Users.AssignRoles",
    ];

    [Fact]
    public void BranchEligibleCodes_AreExactlyTheApprovedConservativeList()
    {
        var actual = PermissionCatalog.All.Where(p => p.Scope == PermissionScope.CompanyOrBranch).Select(p => p.Code).Order().ToArray();

        Assert.Equal(ExpectedBranchEligible.Order(), actual);
    }

    [Theory]
    [InlineData("Inventory.View")]
    [InlineData("Reports.Stock")]
    [InlineData("Reports.Movement")]
    public void Phase8StockReadCodes_AreBranchEligible(string code)
    {
        Assert.Equal(PermissionScope.CompanyOrBranch, PermissionCatalog.ScopeOf(code));
    }

    [Fact]
    public void Phase11D15_CustomersView_IsBranchEligible_ButCustomerWritesStayCompanyOnly()
    {
        Assert.Equal(PermissionScope.CompanyOrBranch, PermissionCatalog.ScopeOf("Customers.View"));
        Assert.All(PermissionCatalog.All.Where(p => p.Code.StartsWith("Customers.", StringComparison.Ordinal) && p.Code != "Customers.View"),
            p => Assert.Equal(PermissionScope.CompanyOnly, p.Scope));
    }

    [Fact]
    public void EveryCatalogueCode_HasAScope_ConsistentWithIsBranchEligibleAndScopeOf()
    {
        foreach (var p in PermissionCatalog.All)
        {
            Assert.Equal(PermissionCatalog.IsBranchEligible(p.Code), p.Scope == PermissionScope.CompanyOrBranch);
            Assert.Equal(p.Scope, PermissionCatalog.ScopeOf(p.Code));
        }
    }

    [Fact]
    public void UnknownCodes_AreCompanyOnly_FailClosed()
    {
        Assert.Equal(PermissionScope.CompanyOnly, PermissionCatalog.ScopeOf("Nothing.Here"));
        Assert.False(PermissionCatalog.IsBranchEligible("Nothing.Here"));
    }

    [Theory]
    [InlineData("Users.")]
    [InlineData("Roles.")]
    [InlineData("Masters.")]
    [InlineData("Company.")]
    [InlineData("Cash.")]
    [InlineData("Inventory.Create")]
    [InlineData("Inventory.Edit")]
    [InlineData("Inventory.Price")]
    [InlineData("Reports.Profit")]
    [InlineData("Reports.Accounts")]
    public void AdministrationMastersAndStockCodes_AreNeverBranchEligible(string prefix)
    {
        Assert.All(PermissionCatalog.All.Where(p => p.Code.StartsWith(prefix, StringComparison.Ordinal)), p => Assert.Equal(PermissionScope.CompanyOnly, p.Scope));
    }

    [Fact]
    public void TheFourPhase7Codes_AreInTheCatalogue_UnderAdministration_WithTheDesignedScopes()
    {
        var byCode = PermissionCatalog.All.ToDictionary(p => p.Code);

        Assert.Equal(PermissionScope.CompanyOnly, byCode["Company.Users.AssignRoles"].Scope);
        foreach (var code in new[] { "Branch.Roles.View", "Branch.Roles.Manage", "Branch.Users.AssignRoles" }) Assert.Equal(PermissionScope.CompanyOrBranch, byCode[code].Scope);
        foreach (var code in new[] { "Company.Users.AssignRoles", "Branch.Roles.View", "Branch.Roles.Manage", "Branch.Users.AssignRoles" }) Assert.Equal("Administration", byCode[code].Module);
    }

    [Fact]
    public void GovernanceCodes_AreNotPartOfThePermissionCatalogue()
    {
        Assert.DoesNotContain(PermissionCatalog.All, p => p.Code.StartsWith("Company.Governance.", StringComparison.Ordinal));
    }

    [Fact]
    public void CatalogueCodes_AreUnique()
    {
        Assert.Equal(PermissionCatalog.All.Count, PermissionCatalog.All.Select(p => p.Code).Distinct().Count());
    }
}
