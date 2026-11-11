using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// T9 — stock locks under the company scope. The first-stock insert stamps the company; a product of company B is invisible to
/// company A's lock queries (<c>"CompanyId" = @company</c> predicate), so A can neither lock nor insert stock for it — and does
/// not wait on B's row locks either.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CompanyStockLockTests(PostgresFixture postgres)
{
    private static readonly Guid CompanyB = Guid.Parse("0c0a1f00-0000-4000-8000-0000000000b2");

    private sealed record Seeds(StockScenario A, Guid ProductB, Guid ProductUnitB);

    private static async Task<Seeds> SeedAsync(TestDatabase database)
    {
        StockScenario a;
        await using (var db = database.CreateDbContext())
        {
            a = await StockScenario.SeedAsync(db);
            db.Companies.Add(new Company { Id = CompanyB, Code = "B", Name = "Company B" });
            await db.SaveChangesAsync();
        }
        await using var dbB = database.CreateDbContext(CompanyB);
        var mainB = BranchBuilder.MainBranch(CompanyB);
        dbB.Branches.Add(mainB);
        dbB.StockLocations.Add(BranchBuilder.Store(mainB)); // Phase 8a: company B's stock posts at its default store
        var kg = await dbB.Units.SingleAsync(u => u.ShortCode == "kg");
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 5%", RatePercent = 5m };
        var inr = await dbB.Currencies.SingleAsync(c => c.Code == "INR");
        var product = new Product { Id = Guid.NewGuid(), Sku = "RICE-BP10", Name = "Ponni Rice (B)", BaseUnitId = kg.Id, TaxId = tax.Id, ReorderLevel = 0 };
        var unit = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = kg.Id, IsBaseUnit = true, Label = "Loose kg", ConversionFactorToBase = 1m };
        product.Units.Add(unit);
        dbB.AddRange(tax, product, new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "B", InvoicePrefix = "INV", BaseCurrencyId = inr.Id });
        await dbB.SaveChangesAsync();
        return new Seeds(a, product.Id, unit.Id);
    }

    private static CreateAdjustmentInput OpeningStock(Guid productId, Guid unitId) =>
        new(productId, unitId, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 5m, 10m, "Opening");

    [Fact]
    public async Task FirstStockIn_StampsTheStockItemAndBatchesWithTheCurrentCompany()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seeds = await SeedAsync(database);
        await using (var db = database.CreateDbContext())
        {
            await seeds.A.PurchaseBagsAsync(db, 2m);
        }
        await using (var dbB = database.CreateDbContext(CompanyB))
        {
            await new StockAdjustmentService(dbB).CreateAdjustmentAsync(OpeningStock(seeds.ProductB, seeds.ProductUnitB), seeds.A.UserId);
        }

        var rows = await CompanyMigrations.QueryAsync(database,
            """SELECT "ProductId"::text || '/' || "CompanyId"::text || '/' || "QuantityOnHand" FROM "StockItems" ORDER BY "QuantityOnHand" DESC""", r => r.GetString(0));
        Assert.Equal([$"{seeds.A.ProductId}/{CompanyMigrations.DefaultCompany}/20.000", $"{seeds.ProductB}/{CompanyMigrations.OtherCompany}/5.000"], rows);
        Assert.Equal(0L, await CompanyMigrations.ScalarAsync<long>(database,
            """SELECT COUNT(*) FROM "PurchaseBatches" b JOIN "Products" p ON p."Id" = b."ProductId" WHERE b."CompanyId" <> p."CompanyId" """));
        Assert.Equal(0L, await CompanyMigrations.ScalarAsync<long>(database,
            """SELECT COUNT(*) FROM "StockMovements" m JOIN "Products" p ON p."Id" = m."ProductId" WHERE m."CompanyId" <> p."CompanyId" """));
    }

    [Fact]
    public async Task StockInForOtherCompanyProduct_FromCompanyA_IsRejectedWithoutWaitingOnBsLocksOrCreatingStock()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seeds = await SeedAsync(database);

        // The gate holds B's product row FOR UPDATE; a query that could see that row would block.
        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("Products", "Id", seeds.ProductB));
        var writer = await race.StartAsync("A-on-B-product", db => new StockAdjustmentService(db).CreateAdjustmentAsync(OpeningStock(seeds.ProductB, seeds.ProductUnitB), seeds.A.UserId));

        Assert.False(await race.WaitBlockedOrFinishedAsync(writer), "Company A must not wait on (or even see) company B's product row.");
        var outcome = await writer.Outcome;
        await race.ReleaseAsync();

        Assert.IsType<DomainValidationException>(outcome);
        Assert.Equal(0L, await CompanyMigrations.ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "StockItems" WHERE "ProductId" = '{seeds.ProductB}'"""));
        Assert.Equal(0L, await CompanyMigrations.ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "StockMovements" WHERE "ProductId" = '{seeds.ProductB}'"""));
    }

    [Fact]
    public async Task ProductLocks_OwnProductStillBlocksOnTheGate_ProvingTheLockQueryTargetsTheCompanysRow()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seeds = await SeedAsync(database);

        // Positive control for the test above: the same gate on A's own product does block A's stock-in.
        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("Products", "Id", seeds.A.ProductId));
        var writer = await race.StartAsync("A-on-own-product", db => seeds.A.PurchaseBagsAsync(db, 1m));
        await race.WaitBlockedAsync(writer, "%FOR KEY SHARE%");

        var outcomes = await race.ReleaseAsync();

        Assert.All(outcomes, Assert.Null);
    }
}
