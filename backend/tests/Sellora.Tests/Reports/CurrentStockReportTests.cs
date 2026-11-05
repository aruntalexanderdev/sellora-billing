using Microsoft.AspNetCore.Mvc;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Reports;

/// <summary>
/// <c>GET api/v1/reports/current-stock</c> selling value (Phase 2 design §5.6, analysis §15): the per-base-unit selling
/// rate is the base unit's own price when it is priced, otherwise the smallest-factor priced <em>active, sellable</em>
/// unit's price divided by its factor (the same anchor as the pricing grid's SuggestedPrice). Quantities stay in base units per product.
/// </summary>
public class CurrentStockReportTests
{
    private sealed record Fixture(SelloraDbContext Db, Product Rice, ProductUnit Kg, ProductUnit Gram, ProductUnit Bag, User User);

    /// <summary>Rice: kg base, Gram 0.001, Bag 25; 50 kg on hand at average cost 40.</summary>
    private static async Task<Fixture> SeedAsync()
    {
        var db = TestDb.NewInMemory();
        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin",
            Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var (kgUnit, gUnit, bagUnit) = (TestUnits.Kilogram(), TestUnits.Gram(), TestUnits.Bag());
        ProductUnit kg = null!, g = null!, bag = null!;
        var rice = new ProductBuilder().WithSku("RICE").WithName("Rice").WithBaseUnit(kgUnit).WithReorderLevel(10)
            .WithUnits(p =>
            {
                kg = TestProductUnits.Base(p);
                g = TestProductUnits.Pack(p, gUnit, kg, 0.001m);
                bag = TestProductUnits.Pack(p, bagUnit, kg, 25m);
            })
            .Build();
        db.AddRange(role, user, kgUnit, gUnit, bagUnit, rice, new StockItem { ProductId = rice.Id, QuantityOnHand = 50m, AverageCost = 40m });
        await db.SaveChangesAsync();
        return new Fixture(db, rice, kg, g, bag, user);
    }

    private static async Task PriceAsync(Fixture f, ProductUnit unit, decimal price)
    {
        f.Db.SellingPrices.Add(new SellingPrice { Id = Guid.NewGuid(), ProductUnitId = unit.Id, Price = price, CreatedByUserId = f.User.Id });
        await f.Db.SaveChangesAsync();
    }

    private static async Task<CurrentStockReportRowDto> RowAsync(Fixture f)
    {
        var result = await new ReportsController(f.Db).CurrentStock(null, null, null, ct: default);
        var page = (PagedResult<CurrentStockReportRowDto>)Assert.IsType<OkObjectResult>(result.Result).Value!;
        return Assert.Single(page.Items);
    }

    [Fact]
    public async Task CurrentStock_BaseAndSmallerFactorRowPriced_PrefersBasePrice()
    {
        var f = await SeedAsync();
        await PriceAsync(f, f.Kg, 60m);
        await PriceAsync(f, f.Gram, 0.07m); // 70 per kg if it were used

        var row = await RowAsync(f);

        Assert.Equal(60m, row.SellingPricePerBaseUnit);
        Assert.Equal(3000m, row.SellingValue);
        Assert.Equal((50m, 40m, 2000m, "kg"), (row.QuantityOnHand, row.AverageCost, row.StockValue, row.BaseUnitLabel));
    }

    [Fact]
    public async Task CurrentStock_BaseUnpriced_UsesSmallestFactorPricedActiveUnit()
    {
        var f = await SeedAsync();
        await PriceAsync(f, f.Bag, 1500m);   // 60 per kg
        await PriceAsync(f, f.Gram, 0.07m);  // 70 per kg, smaller factor

        var row = await RowAsync(f);

        Assert.Equal(70m, row.SellingPricePerBaseUnit);
    }

    [Fact]
    public async Task CurrentStock_InactivePricedRow_Ignored()
    {
        var f = await SeedAsync();
        await PriceAsync(f, f.Bag, 1500m);
        await PriceAsync(f, f.Gram, 0.07m);
        f.Gram.IsActive = false;
        await f.Db.SaveChangesAsync();

        var row = await RowAsync(f);

        Assert.Equal(60m, row.SellingPricePerBaseUnit);
        Assert.Equal(3000m, row.SellingValue);
    }

    [Fact]
    public async Task CurrentStock_NonSellablePricedBaseRow_Ignored()
    {
        var f = await SeedAsync();
        await PriceAsync(f, f.Kg, 60m);     // migrated base row: active but not sellable, still has an open price
        await PriceAsync(f, f.Bag, 1750m);  // 70 per kg
        f.Kg.IsSellable = false;
        await f.Db.SaveChangesAsync();

        var row = await RowAsync(f);

        Assert.Equal(70m, row.SellingPricePerBaseUnit);
        Assert.Equal(3500m, row.SellingValue);
    }

    [Fact]
    public async Task CurrentStock_ClosedPricesOnly_NoSellingValue()
    {
        var f = await SeedAsync();
        f.Db.SellingPrices.Add(new SellingPrice
        {
            Id = Guid.NewGuid(), ProductUnitId = f.Kg.Id, Price = 60m, CreatedByUserId = f.User.Id,
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-2), EffectiveTo = DateTimeOffset.UtcNow.AddDays(-1),
        });
        await f.Db.SaveChangesAsync();

        var row = await RowAsync(f);

        Assert.Null(row.SellingPricePerBaseUnit);
        Assert.Null(row.SellingValue);
    }

    [Fact]
    public async Task CurrentStock_NothingPriced_NoSellingValue()
    {
        var f = await SeedAsync();

        var row = await RowAsync(f);

        Assert.Null(row.SellingPricePerBaseUnit);
        Assert.Null(row.SellingValue);
        Assert.Equal(2000m, row.StockValue);
    }
}
