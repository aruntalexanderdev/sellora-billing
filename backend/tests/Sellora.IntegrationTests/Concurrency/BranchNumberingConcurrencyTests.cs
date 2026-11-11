using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// T5 (PostgreSQL) — document counters are per (company, branch, series): creates in one branch queue on that branch's counter row
/// only, another branch is never blocked, numbers are distinct and contiguous per branch, a rolled-back create hands its number back, and the
/// first document of a branch creates its counter row (<c>ON CONFLICT (CompanyId, BranchId, Series) WHERE BranchId IS NOT NULL</c>).
/// </summary>
[Collection(PostgresCollection.Name)]
public class BranchNumberingConcurrencyTests(PostgresFixture postgres)
{
    private const string SequenceUpdate = """%UPDATE "DocumentSequences"%""";

    private sealed record Setup(TestDatabase Database, StockScenario Scenario, Guid BranchA, Guid BranchB);

    private async Task<Setup> SeedAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        StockScenario scenario;
        Guid a, b;
        await using (var db = database.CreateDbContext())
        {
            scenario = await StockScenario.SeedAsync(db);
            var branchA = BranchBuilder.Extra("AAA", "Branch A");
            var branchB = BranchBuilder.Extra("BBB", "Branch B");
            db.Branches.AddRange(branchA, branchB);
            db.StockLocations.AddRange(BranchBuilder.Store(branchA), BranchBuilder.Store(branchB)); // every branch owns its store (production invariant; 8b posts there)
            await db.SaveChangesAsync();
            (a, b) = (branchA.Id, branchB.Id);
        }
        return new Setup(database, scenario, a, b);
    }

    private static Task<PurchaseOrder> BuyAsync(Setup s, SelloraDbContext db) => s.Scenario.PurchaseBagsAsync(db, 1m);

    private static async Task<Guid> CounterIdAsync(TestDatabase database, Guid branch, string series) =>
        await CompanyMigrations.ScalarAsync<Guid>(database, $"""SELECT "Id" FROM "DocumentSequences" WHERE "BranchId" = '{branch}' AND "Series" = '{series}'""");

    private static async Task<long> NextAsync(TestDatabase database, Guid branch, string series) =>
        await CompanyMigrations.ScalarAsync<long>(database, $"""SELECT "NextValue" FROM "DocumentSequences" WHERE "BranchId" = '{branch}' AND "Series" = '{series}'""");

    [Fact]
    public async Task CreatePurchase_FirstDocumentOfABranch_CreatesItsCounterRowAndNumbersFromOne()
    {
        var s = await SeedAsync();
        await using var _ = s.Database;
        Assert.Equal(0L, await CompanyMigrations.ScalarAsync<long>(s.Database, $"""SELECT COUNT(*) FROM "DocumentSequences" WHERE "BranchId" = '{s.BranchA}'"""));

        await using (var db = s.Database.CreateDbContext(null, Sellora.Infrastructure.Data.BranchContext.Unrestricted(s.BranchA)))
        {
            Assert.Equal("PUR-AAA-0001", (await BuyAsync(s, db)).PurchaseNo);
        }

        Assert.Equal(2L, await NextAsync(s.Database, s.BranchA, "PUR"));
        Assert.Equal(1L, await CompanyMigrations.ScalarAsync<long>(s.Database, $"""SELECT COUNT(*) FROM "DocumentSequences" WHERE "BranchId" = '{s.BranchA}' AND "Series" = 'PUR'"""));
        // Main's counter is untouched by the branch's first document.
        Assert.Equal(1L, await NextAsync(s.Database, DefaultTenant.BranchId, "PUR"));
    }

    [Fact]
    public async Task CreatePurchase_ConcurrentSameBranch_QueueOnTheBranchCounterAndGetDistinctContiguousNumbers()
    {
        var s = await SeedAsync();
        await using var _ = s.Database;
        await using (var db = s.Database.CreateDbContext(null, Sellora.Infrastructure.Data.BranchContext.Unrestricted(s.BranchA))) await BuyAsync(s, db); // creates the counter row
        var counterA = await CounterIdAsync(s.Database, s.BranchA, "PUR");

        await using var race = await LockRace.BeginAsync(s.Database, LockRace.LockRow("DocumentSequences", "Id", counterA));
        var first = await race.StartAsync("first", db => BuyAsync(s, db), branchId: s.BranchA);
        await race.WaitBlockedAsync(first, SequenceUpdate);
        var second = await race.StartAsync("second", db => BuyAsync(s, db), branchId: s.BranchA);
        await race.WaitBlockedAsync(second, SequenceUpdate);
        var third = await race.StartAsync("third", db => BuyAsync(s, db), branchId: s.BranchA);
        await race.WaitBlockedAsync(third, SequenceUpdate);

        var outcomes = await race.ReleaseAsync();

        Assert.All(outcomes, Assert.Null);
        await using var check = s.Database.CreateDbContext();
        Assert.Equal(["PUR-AAA-0001", "PUR-AAA-0002", "PUR-AAA-0003", "PUR-AAA-0004"], await check.PurchaseOrders.Where(p => p.BranchId == s.BranchA).OrderBy(p => p.PurchaseNo).Select(p => p.PurchaseNo).ToListAsync());
        Assert.Equal(5L, await NextAsync(s.Database, s.BranchA, "PUR"));
    }

    [Fact]
    public async Task CreatePurchase_OtherBranchWhileFirstBranchCounterIsLocked_IsNotBlockedAndKeepsItsOwnNumbering()
    {
        var s = await SeedAsync();
        await using var _ = s.Database;
        await using (var db = s.Database.CreateDbContext(null, Sellora.Infrastructure.Data.BranchContext.Unrestricted(s.BranchA))) await BuyAsync(s, db);
        await using (var db = s.Database.CreateDbContext(null, Sellora.Infrastructure.Data.BranchContext.Unrestricted(s.BranchB))) await BuyAsync(s, db);
        var counterA = await CounterIdAsync(s.Database, s.BranchA, "PUR");

        await using var race = await LockRace.BeginAsync(s.Database, LockRace.LockRow("DocumentSequences", "Id", counterA));
        var writerA = await race.StartAsync("A", db => BuyAsync(s, db), branchId: s.BranchA);
        await race.WaitBlockedAsync(writerA, SequenceUpdate);
        var writerB = await race.StartAsync("B", db => BuyAsync(s, db), branchId: s.BranchB);

        Assert.False(await race.WaitBlockedOrFinishedAsync(writerB, SequenceUpdate), "Branch B must not wait on branch A's counter row.");
        Assert.Null(await writerB.Outcome);
        Assert.False(writerA.Outcome.IsCompleted);

        var outcomes = await race.ReleaseAsync();

        Assert.All(outcomes, Assert.Null);
        await using var check = s.Database.CreateDbContext();
        Assert.Equal(["PUR-AAA-0001", "PUR-AAA-0002"], await check.PurchaseOrders.Where(p => p.BranchId == s.BranchA).OrderBy(p => p.PurchaseNo).Select(p => p.PurchaseNo).ToListAsync());
        Assert.Equal(["PUR-BBB-0001", "PUR-BBB-0002"], await check.PurchaseOrders.Where(p => p.BranchId == s.BranchB).OrderBy(p => p.PurchaseNo).Select(p => p.PurchaseNo).ToListAsync());
    }

    [Fact]
    public async Task CreatePurchase_ConcurrentFirstDocumentsOfOneBranch_BothSucceedWithTwoDistinctNumbersAndOneCounterRow()
    {
        var s = await SeedAsync();
        await using var _ = s.Database;

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = s.Database.CreateDbContext(null, Sellora.Infrastructure.Data.BranchContext.Unrestricted(s.BranchB));
            return (await BuyAsync(s, db)).PurchaseNo;
        }));

        Assert.Equal(["PUR-BBB-0001", "PUR-BBB-0002", "PUR-BBB-0003", "PUR-BBB-0004"], results.Order().ToArray());
        Assert.Equal(1L, await CompanyMigrations.ScalarAsync<long>(s.Database, $"""SELECT COUNT(*) FROM "DocumentSequences" WHERE "BranchId" = '{s.BranchB}' AND "Series" = 'PUR'"""));
        Assert.Equal(5L, await NextAsync(s.Database, s.BranchB, "PUR"));
    }

    [Fact]
    public async Task CreatePurchase_FailsAfterNumbering_RollsBackAndLeavesNoGap()
    {
        var s = await SeedAsync();
        await using var _ = s.Database;
        await using (var db = s.Database.CreateDbContext(null, Sellora.Infrastructure.Data.BranchContext.Unrestricted(s.BranchA)))
        {
            // A legacy row already holding PUR-AAA-0001 makes the next insert collide after the number was taken.
            db.PurchaseOrders.Add(new PurchaseOrder
            {
                Id = Guid.NewGuid(), BranchId = s.BranchA, StockLocationId = Sellora.Domain.Companies.DefaultTenant.MainStoreLocationId, PurchaseNo = "PUR-AAA-0001", SupplierId = s.Scenario.SupplierId,
                CurrencyId = (await db.Currencies.SingleAsync(c => c.Code == "INR")).Id, PurchaseDate = StockScenario.PurchaseDate,
                PaymentMethod = PaymentMethod.Cash, CreatedByUserId = s.Scenario.UserId,
            });
            await db.SaveChangesAsync();
        }
        await using (var db = s.Database.CreateDbContext(null, Sellora.Infrastructure.Data.BranchContext.Unrestricted(s.BranchA)))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => BuyAsync(s, db));
        }

        Assert.Equal(0L, await CompanyMigrations.ScalarAsync<long>(s.Database, $"""SELECT COUNT(*) FROM "DocumentSequences" WHERE "BranchId" = '{s.BranchA}' AND "Series" = 'PUR' AND "NextValue" <> 1"""));
    }

    [Fact]
    public async Task CreateSale_TwoBranchesInParallel_NumberIndependentlyUnderTheirOwnInvoicePrefixes()
    {
        var s = await SeedAsync();
        await using var _ = s.Database;
        await using (var db = s.Database.CreateDbContext())
        {
            // 8b: each branch sells from its own store: 3 kg at each.
            foreach (var branch in new[] { s.BranchA, s.BranchB }) await StockSeed.AddAsync(db, await StockSeed.StoreOfAsync(db, branch), s.Scenario.ProductId, 3m, 5m, s.Scenario.UserId);
        }

        var numbers = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            await using var db = s.Database.CreateDbContext(null, Sellora.Infrastructure.Data.BranchContext.Unrestricted(i % 2 == 0 ? s.BranchA : s.BranchB));
            return (await s.Scenario.SellLooseKgAsync(db, 1m)).InvoiceNo;
        }));

        Assert.Equal(["INV-AAA-0001", "INV-AAA-0002", "INV-AAA-0003"], numbers.Where(n => n.StartsWith("INV-AAA")).Order().ToArray());
        Assert.Equal(["INV-BBB-0001", "INV-BBB-0002", "INV-BBB-0003"], numbers.Where(n => n.StartsWith("INV-BBB")).Order().ToArray());
        await using var check = s.Database.CreateDbContext();
        await StockInvariants.AssertHoldAsync(check);
    }

    [Fact]
    public async Task CreateExpense_AnyBranchTag_ShareOneCompanyLevelCounterRow()
    {
        var s = await SeedAsync();
        await using var _ = s.Database;
        Guid categoryId, currencyId;
        await using (var db = s.Database.CreateDbContext())
        {
            var category = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Rent" };
            db.ExpenseCategories.Add(category);
            await db.SaveChangesAsync();
            categoryId = category.Id;
            currencyId = await db.Currencies.Where(c => c.Code == "INR").Select(c => c.Id).SingleAsync();
        }

        foreach (var tag in new Guid?[] { null, s.BranchA, s.BranchB })
        {
            await using var db = s.Database.CreateDbContext();
            await new ExpenseService(db, new ExchangeRateService(db)).CreateAsync(
                new Sellora.Domain.Expenses.CreateExpenseInput(categoryId, StockScenario.SaleDate, currencyId, 5m, PaymentMethod.Cash, null, null) { BranchId = tag }, s.Scenario.UserId);
        }

        await using var check = s.Database.CreateDbContext();
        Assert.Equal(["EXP-0001", "EXP-0002", "EXP-0003"], await check.Expenses.OrderBy(e => e.ExpenseNo).Select(e => e.ExpenseNo).ToListAsync());
        Assert.Equal(1L, await CompanyMigrations.ScalarAsync<long>(s.Database, """SELECT COUNT(*) FROM "DocumentSequences" WHERE "Series" = 'EXP' AND "BranchId" IS NULL"""));
        Assert.Equal(0L, await CompanyMigrations.ScalarAsync<long>(s.Database, """SELECT COUNT(*) FROM "DocumentSequences" WHERE "Series" = 'EXP' AND "BranchId" IS NOT NULL"""));
    }

    [Fact]
    public async Task CreateBranch_WhileTheCompanyRowIsLocked_WaitsOnTheCompanyLockAndThenSucceeds()
    {
        var s = await SeedAsync();
        await using var _ = s.Database;

        await using var race = await LockRace.BeginAsync(s.Database, LockRace.LockRow("Companies", "Id", DefaultTenant.CompanyId, "FOR NO KEY UPDATE")); // another company-row writer (CompanyLocks)
        var writer = await race.StartAsync("create", db => new BranchService(db, new StockLocationUsageService(db)).CreateAsync(
            new CreateBranchInput("CCC", "Branch C", null, null, null, null, null, null, null), s.Scenario.UserId));
        await race.WaitBlockedAsync(writer, """%FROM "Companies"%FOR NO KEY UPDATE%""");

        var outcomes = await race.ReleaseAsync();

        Assert.All(outcomes, Assert.Null);
        await using var check = s.Database.CreateDbContext();
        Assert.True(await check.Branches.AnyAsync(b => b.Code == "CCC"));
    }

    [Fact]
    public async Task SettingsPrefixCheck_WhileTheCompanyRowIsLocked_WaitsSoItCannotRaceABranchEdit()
    {
        var s = await SeedAsync();
        await using var _ = s.Database;

        await using var race = await LockRace.BeginAsync(s.Database, LockRace.LockRow("Companies", "Id", DefaultTenant.CompanyId, "FOR NO KEY UPDATE")); // another company-row writer (CompanyLocks)
        var writer = await race.StartAsync("settings", db => new BranchService(db, new StockLocationUsageService(db)).EnsureCompanyInvoicePrefixAllowedAsync("SAL"));
        await race.WaitBlockedAsync(writer, """%FROM "Companies"%FOR NO KEY UPDATE%""");

        var outcomes = await race.ReleaseAsync();

        Assert.All(outcomes, Assert.Null);
    }
}
