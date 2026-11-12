using Microsoft.AspNetCore.Mvc;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Companies;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.PenScenario;

namespace Sellora.IntegrationTests.Reports;

/// <summary>
/// Phase 7 on Npgsql: the breakdown-unit projection (<c>Label ?? Unit.Name</c>, <c>Unit.ShortCode</c>, ordering by factor descending then
/// SortOrder, the <c>IsActive AND (IsSellable OR IsBaseUnit)</c> filter) must translate to SQL; InMemory cannot prove that.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StockQuantityDisplayPostgresTests(PostgresFixture postgres)
{
    private static T Ok<T>(ActionResult<T> r) => (T)Assert.IsType<OkObjectResult>(r.Result).Value!;

    [Fact]
    public async Task CurrentStockAndOverview_PenWithLabelOverride_TranslatesBreakdownOnPostgres()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var s = await SeedAsync(db);
        await StockSeed.AddAsync(db, DefaultTenant.MainStoreLocationId, s.PenId, 122m, 2m, s.UserId);

        // Label override on Box (Label ?? Unit.Name), Half Carton made non-sellable (filter), Full Carton kept.
        var boxRowId = (await ActiveRowAsync(db, s.PenId, s.Box)).Id;
        var rows = db.ProductUnits.Where(u => u.ProductId == s.PenId).ToList();
        rows.Single(r => r.Id == boxRowId).Label = "Pack of 10";
        rows.Single(r => r.UnitId == s.HCtn).IsSellable = false;
        await db.SaveChangesAsync();

        var perms = new FakePermissions("Inventory.View", "Reports.Stock", "Reports.Movement");
        await using var read = database.CreateDbContext();
        var report = Ok(await new ReportsController(read, perms).CurrentStock(null, null, null, ct: default)).Items.Single(r => r.ProductId == s.PenId);
        var overview = Ok(await new StockController(read, perms).Overview(null, null, null, null, 1, 20, null, default)).Items.Single(r => r.ProductId == s.PenId);

        foreach (var units in new[] { report.BreakdownUnits, overview.BreakdownUnits })
        {
            Assert.Equal(["fctn", "box", "pc"], units!.Select(u => u.UnitCode));
            Assert.Equal([100m, 10m, 1m], units!.Select(u => u.Factor));
            Assert.Equal(["Full Carton", "Pack of 10", "Piece"], units!.Select(u => u.Label));
            Assert.Equal([false, false, true], units!.Select(u => u.IsBaseUnit));
        }
        Assert.Equal((0, 0), (report.BaseDecimalPlaces, overview.BaseDecimalPlaces));
    }
}
