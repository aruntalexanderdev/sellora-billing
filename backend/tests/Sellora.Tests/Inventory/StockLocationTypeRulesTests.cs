using Sellora.Domain.Inventory;

namespace Sellora.Tests.Inventory;

/// <summary>Phase 3 T2 — capability rules derived purely from the location type (multi-company architecture §8).</summary>
public class StockLocationTypeRulesTests
{
    // type, requiresBranch, canSell, canReceivePurchase, isSystemManaged
    public static TheoryData<StockLocationType, bool, bool, bool, bool> Matrix => new()
    {
        { StockLocationType.BranchStore, true, true, true, true },
        { StockLocationType.BranchWarehouse, true, false, true, false },
        { StockLocationType.CompanyWarehouse, false, false, true, false },
        { StockLocationType.Vehicle, false, true, false, false },
        { StockLocationType.Transit, false, false, false, true },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Rules_EachType_MatchTheCapabilityMatrix(StockLocationType type, bool requiresBranch, bool canSell, bool canReceive, bool systemManaged)
    {
        Assert.Equal(requiresBranch, StockLocationTypeRules.RequiresBranch(type));
        Assert.Equal(canSell, StockLocationTypeRules.CanSell(type));
        Assert.Equal(canReceive, StockLocationTypeRules.CanReceivePurchase(type));
        Assert.Equal(systemManaged, StockLocationTypeRules.IsSystemManaged(type));
    }

    [Fact]
    public void Matrix_CoversEveryEnumValue()
    {
        var covered = Matrix.Select(r => (StockLocationType)r[0]).Order().ToArray();
        Assert.Equal(Enum.GetValues<StockLocationType>().Order().ToArray(), covered);
    }

    [Fact]
    public void Transit_IsNeverASaleOrPurchaseLocation()
    {
        Assert.False(StockLocationTypeRules.CanSell(StockLocationType.Transit));
        Assert.False(StockLocationTypeRules.CanReceivePurchase(StockLocationType.Transit));
    }

    [Fact]
    public void LocationType_EnumNames_MatchTheDatabaseCheckConstraintValues()
    {
        Assert.Equal(["BranchStore", "BranchWarehouse", "CompanyWarehouse", "Transit", "Vehicle"], Enum.GetNames<StockLocationType>().Order());
    }
}
