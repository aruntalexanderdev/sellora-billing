using Microsoft.AspNetCore.Mvc;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Reports;

/// <summary>
/// Phase 7 quantity display: every stock read carries the base unit's <c>DecimalPlaces</c> and the product's breakdown units
/// (active, sellable-or-base, largest factor first). Values are asserted explicitly (kg = 3, piece = 0) because the DTO default
/// of 0 would silently mask a projection that was never wired.
/// </summary>
public class StockQuantityDisplayTests
{
    private static readonly string[] Codes = ["Inventory.View", "Reports.Stock", "Reports.Movement"];

    private sealed record World(SelloraDbContext Db, Product Pen, Product Rice, ProductUnit PenPiece, ProductUnit PenBox, ProductUnit PenHalf, ProductUnit PenFull);

    private static Product AddStock(SelloraDbContext db, Product p, decimal qty)
    {
        db.Add(new StockItem { StockLocationId = DefaultTenant.MainStoreLocationId, ProductId = p.Id, QuantityOnHand = qty, AverageCost = 2m });
        return p;
    }

    /// <summary>Pen (piece base; Box 10, Half Carton 50, Full Carton 100) and Rice (kg base; Gram 0.001, Bag 25), each with stock below the reorder level.</summary>
    private static async Task<World> SeedAsync(Action<ProductUnit, ProductUnit, ProductUnit, ProductUnit>? tweakPen = null)
    {
        var db = TestDb.NewInMemory();
        var (pc, box, half, full) = (TestUnits.Piece(), TestUnits.Box(), TestUnits.HalfCarton(), TestUnits.FullCarton());
        var (kgU, gU, bagU) = (TestUnits.Kilogram(), TestUnits.Gram(), TestUnits.Bag());
        ProductUnit penPc = null!, penBox = null!, penHalf = null!, penFull = null!;
        var pen = new ProductBuilder().WithSku("PEN").WithName("Pen").WithBaseUnit(pc).WithReorderLevel(500)
            .WithUnits(p =>
            {
                penPc = TestProductUnits.Base(p);
                penBox = TestProductUnits.Pack(p, box, penPc, 10m);
                penHalf = TestProductUnits.Pack(p, half, penPc, 50m);
                penFull = TestProductUnits.Pack(p, full, penPc, 100m);
                foreach (var u in new[] { penBox, penHalf, penFull }) u.IsSellable = true;
            }).Build();
        ProductUnit riceKg = null!;
        var rice = new ProductBuilder().WithSku("RICE").WithName("Rice").WithBaseUnit(kgU).WithReorderLevel(100)
            .WithUnits(p =>
            {
                riceKg = TestProductUnits.Base(p);
                TestProductUnits.Pack(p, gU, riceKg, 0.001m).IsSellable = true;
                TestProductUnits.Pack(p, bagU, riceKg, 25m).IsSellable = true;
            }).Build();
        tweakPen?.Invoke(penPc, penBox, penHalf, penFull);
        db.AddRange(pc, box, half, full, kgU, gU, bagU, pen, rice);
        AddStock(db, pen, 122m);
        AddStock(db, rice, 12.5m);
        await db.SaveChangesAsync();
        return new World(db, pen, rice, penPc, penBox, penHalf, penFull);
    }

    private static T Ok<T>(ActionResult<T> r) => (T)Assert.IsType<OkObjectResult>(r.Result).Value!;
    private static FakeEffectivePermissions Perms() => new(Codes);

    private static async Task<PagedResult<CurrentStockReportRowDto>> ReportAsync(SelloraDbContext db, int page = 1, int pageSize = 20) =>
        Ok(await new ReportsController(db, Perms()).CurrentStock(null, null, null, page, pageSize, ct: default));

    private static async Task<PagedResult<StockOverviewItemDto>> OverviewAsync(SelloraDbContext db, int page = 1, int pageSize = 20) =>
        Ok(await new StockController(db, Perms()).Overview(null, null, null, null, page, pageSize, null, default));

    private static async Task<StockDetailDto> DetailAsync(SelloraDbContext db, Guid id) =>
        Ok(await new StockController(db, Perms()).Detail(id, default));

    private static async Task<DashboardLowStockItemDto[]> LowStockAsync(SelloraDbContext db)
    {
        var branch = new BranchContext();
        branch.Set(null, null);
        var dash = Ok(await new DashboardController(db, new FakeEffectivePermissions("Inventory.View"), branch).Get(default));
        return [.. dash.LowStockItems!];
    }

    private static string[] Codes_(IReadOnlyList<BreakdownUnitDto>? u) => [.. u!.Select(x => x.UnitCode)];

    // ---- BaseDecimalPlaces -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CurrentStock_KgAndPieceBase_CarriesBaseDecimalPlaces()
    {
        var w = await SeedAsync();
        var rows = (await ReportAsync(w.Db)).Items.ToDictionary(r => r.ProductId);
        Assert.Equal(3, rows[w.Rice.Id].BaseDecimalPlaces);
        Assert.Equal(0, rows[w.Pen.Id].BaseDecimalPlaces);
    }

    [Fact]
    public async Task StockOverview_KgAndPieceBase_CarriesBaseDecimalPlaces()
    {
        var w = await SeedAsync();
        var rows = (await OverviewAsync(w.Db)).Items.ToDictionary(r => r.ProductId);
        Assert.Equal(3, rows[w.Rice.Id].BaseDecimalPlaces);
        Assert.Equal(0, rows[w.Pen.Id].BaseDecimalPlaces);
    }

    [Fact]
    public async Task StockDetail_KgAndPieceBase_CarriesBaseDecimalPlaces()
    {
        var w = await SeedAsync();
        Assert.Equal(3, (await DetailAsync(w.Db, w.Rice.Id)).BaseDecimalPlaces);
        Assert.Equal(0, (await DetailAsync(w.Db, w.Pen.Id)).BaseDecimalPlaces);
    }

    [Fact]
    public async Task DashboardLowStock_KgAndPieceBase_CarriesBaseDecimalPlaces()
    {
        var w = await SeedAsync();
        var items = (await LowStockAsync(w.Db)).ToDictionary(i => i.ProductId);
        Assert.Equal(3, items[w.Rice.Id].BaseDecimalPlaces);
        Assert.Equal(0, items[w.Pen.Id].BaseDecimalPlaces);
    }

    // ---- Pen canonical example -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CurrentStock_PenHierarchy_ReturnsFactorsLargestFirst()
    {
        var w = await SeedAsync();
        var pen = (await ReportAsync(w.Db)).Items.Single(r => r.ProductId == w.Pen.Id);
        Assert.Equal(["fctn", "hctn", "box", "pc"], Codes_(pen.BreakdownUnits));
        Assert.Equal([100m, 50m, 10m, 1m], pen.BreakdownUnits!.Select(u => u.Factor));
        Assert.Equal([w.PenFull.Id, w.PenHalf.Id, w.PenBox.Id, w.PenPiece.Id], pen.BreakdownUnits!.Select(u => u.ProductUnitId));
        Assert.Equal([false, false, false, true], pen.BreakdownUnits!.Select(u => u.IsBaseUnit));
        Assert.Equal(["Full Carton", "Half Carton", "Box", "Piece"], pen.BreakdownUnits!.Select(u => u.Label));
    }

    [Fact]
    public async Task StockOverviewAndDetail_PenHierarchy_ReturnSameFactors()
    {
        var w = await SeedAsync();
        var ov = (await OverviewAsync(w.Db)).Items.Single(r => r.ProductId == w.Pen.Id);
        var detail = await DetailAsync(w.Db, w.Pen.Id);
        Assert.Equal([100m, 50m, 10m, 1m], ov.BreakdownUnits!.Select(u => u.Factor));
        Assert.Equal([100m, 50m, 10m, 1m], detail.BreakdownUnits!.Select(u => u.Factor));
    }

    // ---- filtering and ordering ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Breakdown_InactiveUnit_Excluded()
    {
        var w = await SeedAsync((_, box, _, _) => box.IsActive = false);
        var pen = (await ReportAsync(w.Db)).Items.Single(r => r.ProductId == w.Pen.Id);
        Assert.Equal(["fctn", "hctn", "pc"], Codes_(pen.BreakdownUnits));
    }

    [Fact]
    public async Task Breakdown_NonSellableNonBaseUnit_Excluded()
    {
        var w = await SeedAsync((_, _, half, _) => half.IsSellable = false);
        var pen = (await ReportAsync(w.Db)).Items.Single(r => r.ProductId == w.Pen.Id);
        Assert.Equal(["fctn", "box", "pc"], Codes_(pen.BreakdownUnits));
    }

    [Fact]
    public async Task Breakdown_NonSellableBaseUnit_Included()
    {
        var w = await SeedAsync((pc, _, _, _) => pc.IsSellable = false);
        var pen = (await ReportAsync(w.Db)).Items.Single(r => r.ProductId == w.Pen.Id);
        var last = pen.BreakdownUnits!.Last();
        Assert.Equal(("pc", 1m, true), (last.UnitCode, last.Factor, last.IsBaseUnit));
    }

    [Fact]
    public async Task Breakdown_EqualFactors_OrderedBySortOrder()
    {
        // Box and Half Carton both made factor 10; SortOrder decides (Half Carton first when its SortOrder is lower).
        var w = await SeedAsync((_, box, half, _) => { box.ConversionFactorToBase = 10m; half.ConversionFactorToBase = 10m; box.SortOrder = 5; half.SortOrder = 2; });
        var pen = (await ReportAsync(w.Db)).Items.Single(r => r.ProductId == w.Pen.Id);
        Assert.Equal(["fctn", "hctn", "box", "pc"], Codes_(pen.BreakdownUnits));

        var w2 = await SeedAsync((_, box, half, _) => { box.ConversionFactorToBase = 10m; half.ConversionFactorToBase = 10m; box.SortOrder = 2; half.SortOrder = 5; });
        var pen2 = (await OverviewAsync(w2.Db)).Items.Single(r => r.ProductId == w2.Pen.Id);
        Assert.Equal(["fctn", "box", "hctn", "pc"], Codes_(pen2.BreakdownUnits));
    }

    [Fact]
    public async Task Breakdown_LabelOverride_UsedElseUnitName()
    {
        var w = await SeedAsync();
        (await w.Db.ProductUnits.FindAsync(w.PenBox.Id))!.Label = "Pack of 10";
        await w.Db.SaveChangesAsync();
        var pen = (await ReportAsync(w.Db)).Items.Single(r => r.ProductId == w.Pen.Id);
        Assert.Equal(["Full Carton", "Half Carton", "Pack of 10", "Piece"], pen.BreakdownUnits!.Select(u => u.Label));
    }

    [Fact]
    public async Task Breakdown_TwoProducts_NoLeakageBetweenProducts()
    {
        var w = await SeedAsync();
        var rows = (await ReportAsync(w.Db)).Items.ToDictionary(r => r.ProductId);
        Assert.Equal(["bag", "kg", "g"], Codes_(rows[w.Rice.Id].BreakdownUnits));
        Assert.Equal([25m, 1m, 0.001m], rows[w.Rice.Id].BreakdownUnits!.Select(u => u.Factor));
        Assert.DoesNotContain(rows[w.Pen.Id].BreakdownUnits!, u => u.UnitCode is "bag" or "kg" or "g");
        var ov = (await OverviewAsync(w.Db)).Items.ToDictionary(r => r.ProductId);
        Assert.Equal(["bag", "kg", "g"], Codes_(ov[w.Rice.Id].BreakdownUnits));
    }

    [Fact]
    public async Task Breakdown_BaseOnlyProduct_SingleBaseEntry_AndNeverNull()
    {
        var db = TestDb.NewInMemory();
        var kgU = TestUnits.Kilogram();
        var flour = new ProductBuilder().WithSku("FLR").WithName("Flour").WithBaseUnit(kgU).WithReorderLevel(100).WithBaseUnitRow().Build();
        db.AddRange(kgU, flour);
        AddStock(db, flour, 5m);
        await db.SaveChangesAsync();

        foreach (var units in new[]
        {
            (await ReportAsync(db)).Items.Single().BreakdownUnits,
            (await OverviewAsync(db)).Items.Single().BreakdownUnits,
            (await DetailAsync(db, flour.Id)).BreakdownUnits,
        })
        {
            var only = Assert.Single(units!);
            Assert.Equal(("kg", "Kilogram", 1m, true), (only.UnitCode, only.Label, only.Factor, only.IsBaseUnit));
        }
    }

    [Fact]
    public async Task Breakdown_ProductWithoutUnitRows_EmptyListNotNull()
    {
        var db = TestDb.NewInMemory();
        var pc = TestUnits.Piece();
        var bare = new ProductBuilder().WithSku("BARE").WithBaseUnit(pc).Build();
        db.AddRange(pc, bare);
        AddStock(db, bare, 1m);
        await db.SaveChangesAsync();
        Assert.Empty((await ReportAsync(db)).Items.Single().BreakdownUnits!);
        Assert.Empty((await DetailAsync(db, bare.Id)).BreakdownUnits!);
    }

    [Fact]
    public async Task CurrentStock_Paging_EachPageCarriesItsOwnUnitsAndPrecision()
    {
        var w = await SeedAsync();
        var p1 = await ReportAsync(w.Db, 1, 1);
        var p2 = await ReportAsync(w.Db, 2, 1);
        var (a, b) = (Assert.Single(p1.Items), Assert.Single(p2.Items));
        Assert.NotEqual(a.ProductId, b.ProductId);
        foreach (var r in new[] { a, b })
        {
            var (dp, codes) = r.ProductId == w.Pen.Id ? (0, new[] { "fctn", "hctn", "box", "pc" }) : (3, ["bag", "kg", "g"]);
            Assert.Equal(dp, r.BaseDecimalPlaces);
            Assert.Equal(codes, Codes_(r.BreakdownUnits));
        }

        var o2 = await OverviewAsync(w.Db, 2, 1);
        var row = Assert.Single(o2.Items);
        Assert.Equal(row.ProductId == w.Pen.Id ? 4 : 3, row.BreakdownUnits!.Count);
    }
}
