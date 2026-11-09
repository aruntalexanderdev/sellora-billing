using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Expenses;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// T8 — per-company gap-free document numbering (<c>DocumentNumbers.NextAsync</c>: <c>UPDATE … RETURNING</c> on the
/// <c>DocumentSequences</c> row) driven through the real services. A gate transaction holds the counter row
/// (<c>FOR UPDATE</c>); the writers must be seen waiting in the <c>UPDATE "DocumentSequences"</c> statement (pg_stat_activity),
/// which proves the numbering takes that row lock — if numbering stopped locking the row (e.g. a MAX+1 or an unlocked read) the
/// writers would finish while the gate is held and <c>WaitBlockedAsync</c> fails.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DocumentNumberConcurrencyTests(PostgresFixture postgres)
{
    private const string SequenceUpdate = """%UPDATE "DocumentSequences"%""";
    private static readonly Guid CompanyB = Guid.Parse("0c0a1f00-0000-4000-8000-0000000000b2");
    private static readonly Guid CompanyC = Guid.Parse("0c0a1f00-0000-4000-8000-0000000000c3");

    private static long Suffix(string number) => long.Parse(Regex.Match(number, "[0-9]+$").Value);

    private static ExpenseService Expenses(SelloraDbContext db) => new(db, new ExchangeRateService(db));

    private static CreateExpenseInput Input(Guid categoryId, Guid currencyId) =>
        new(categoryId, new DateOnly(2026, 11, 1), currencyId, 100m, PaymentMethod.Cash, null, null);

    private static async Task<Guid> SequenceIdAsync(TestDatabase database, Guid company, string series) =>
        await CompanyMigrations.ScalarAsync<Guid>(database, $"""SELECT "Id" FROM "DocumentSequences" WHERE "CompanyId" = '{company}' AND "BranchId" IS NULL AND "Series" = '{series}'""");

    private static Task<long> NextValueAsync(TestDatabase database, Guid company, string series) =>
        CompanyMigrations.ScalarAsync<long>(database, $"""SELECT "NextValue" FROM "DocumentSequences" WHERE "CompanyId" = '{company}' AND "BranchId" IS NULL AND "Series" = '{series}'""");

    /// <summary>Default company: settings, a user, a category; plus currency. Returns the ids needed to create expenses.</summary>
    private static async Task<(Guid CategoryId, Guid CurrencyId, Guid UserId)> SeedDefaultAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var inr = new Currency { Id = Guid.NewGuid(), Code = "XTS", Name = "Test Rupee", Symbol = "₹" };
        var role = new Role { Id = Guid.NewGuid(), Name = "Number Race Admin", IsSystem = false };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Race", LastName = "Tester", Username = "number-race",
            Email = "number-race@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var category = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Race Rent" };
        db.AddRange(inr, role, user, category, new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id });
        await db.SaveChangesAsync();
        return (category.Id, inr.Id, user.Id);
    }

    /// <summary>A further company created after the migrations: it has NO DocumentSequences rows yet.</summary>
    private static async Task<Guid> SeedCompanyAsync(TestDatabase database, Guid companyId, Guid currencyId)
    {
        await using (var platform = database.CreateDbContext())
        {
            platform.Companies.Add(new Company { Id = companyId, Code = "C" + companyId.ToString("N")[^4..], Name = "Company " + companyId.ToString("N")[^4..] });
            await platform.SaveChangesAsync();
        }
        await using var db = database.CreateDbContext(companyId);
        var category = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Race Rent" };
        db.AddRange(category, new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Company " + companyId, InvoicePrefix = "INV", BaseCurrencyId = currencyId });
        await db.SaveChangesAsync();
        return category.Id;
    }

    [Fact]
    public async Task CreateExpense_ConcurrentSameCompany_WaitOnTheSequenceRowAndGetContiguousNumbers()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var (categoryId, currencyId, userId) = await SeedDefaultAsync(database);
        var sequenceId = await SequenceIdAsync(database, DefaultTenant.CompanyId, "EXP");
        var input = Input(categoryId, currencyId);

        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("DocumentSequences", "Id", sequenceId));
        var first = await race.StartAsync("first", db => Expenses(db).CreateAsync(input, userId));
        await race.WaitBlockedAsync(first, SequenceUpdate);
        var second = await race.StartAsync("second", db => Expenses(db).CreateAsync(input, userId));
        await race.WaitBlockedAsync(second, SequenceUpdate);

        var outcomes = await race.ReleaseAsync();

        Assert.All(outcomes, Assert.Null);
        await using var check = database.CreateDbContext();
        Assert.Equal(["EXP-0001", "EXP-0002"], await check.Expenses.OrderBy(e => e.ExpenseNo).Select(e => e.ExpenseNo).ToListAsync());
        Assert.Equal(3L, await NextValueAsync(database, DefaultTenant.CompanyId, "EXP"));
    }

    [Fact]
    public async Task CreateSale_ConcurrentSameCompany_WaitOnTheInvoiceSequenceAndGetDistinctContiguousNumbers()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        StockScenario scenario;
        await using (var db = database.CreateDbContext())
        {
            scenario = await StockScenario.SeedAsync(db);
            await scenario.PurchaseBagsAsync(db, 1m);
        }
        var sequenceId = await SequenceIdAsync(database, DefaultTenant.CompanyId, "INV");

        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("DocumentSequences", "Id", sequenceId));
        var first = await race.StartAsync("first", db => scenario.SellLooseKgAsync(db, 1m));
        await race.WaitBlockedAsync(first, SequenceUpdate);
        // The second sale queues behind the first (stock row) or on the counter; either way it must not finish.
        var second = await race.StartAsync("second", db => scenario.SellLooseKgAsync(db, 1m));
        await race.WaitBlockedAsync(second);

        var outcomes = await race.ReleaseAsync();

        Assert.All(outcomes, Assert.Null);
        await using var check = database.CreateDbContext();
        var numbers = (await check.SalesInvoices.Select(i => i.InvoiceNo).ToListAsync()).Select(Suffix).Order().ToList();
        Assert.Equal([1L, 2L], numbers);
        Assert.Equal(3L, await NextValueAsync(database, DefaultTenant.CompanyId, "INV"));
        await StockInvariants.AssertHoldAsync(check);
    }

    [Fact]
    public async Task CreateExpense_OtherCompanyWhileFirstCompanysSequenceIsLocked_IsNotBlockedAndKeepsItsOwnNumbering()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var (categoryA, currencyId, userId) = await SeedDefaultAsync(database);
        var categoryB = await SeedCompanyAsync(database, CompanyB, currencyId);
        var sequenceA = await SequenceIdAsync(database, DefaultTenant.CompanyId, "EXP");

        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("DocumentSequences", "Id", sequenceA));
        var writerA = await race.StartAsync("A", db => Expenses(db).CreateAsync(Input(categoryA, currencyId), userId));
        await race.WaitBlockedAsync(writerA, SequenceUpdate);
        var writerB = await race.StartAsync("B", db => Expenses(db).CreateAsync(Input(categoryB, currencyId), userId), CompanyB);

        // B finishes while A's counter row is still locked by the gate.
        Assert.False(await race.WaitBlockedOrFinishedAsync(writerB, SequenceUpdate), "Company B must not wait on company A's sequence row.");
        Assert.Null(await writerB.Outcome);
        Assert.False(writerA.Outcome.IsCompleted);

        var outcomes = await race.ReleaseAsync();

        Assert.All(outcomes, Assert.Null);
        // Both companies continue their own series: the same number in both, scoped unique per company.
        await using var checkA = database.CreateDbContext();
        await using var checkB = database.CreateDbContext(CompanyB);
        Assert.Equal(["EXP-0001"], await checkA.Expenses.Select(e => e.ExpenseNo).ToListAsync());
        Assert.Equal(["EXP-0001"], await checkB.Expenses.Select(e => e.ExpenseNo).ToListAsync());
    }

    [Fact]
    public async Task CreateExpense_FailsAfterNumbering_RollsBackAndConsumesNoNumber()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var (categoryId, currencyId, userId) = await SeedDefaultAsync(database);
        Guid blockerId;
        await using (var db = database.CreateDbContext())
        {
            // A legacy row already holding EXP-0001 makes the service's insert collide after the number was taken.
            var blocker = new Expense
            {
                Id = Guid.NewGuid(), ExpenseNo = "EXP-0001", CategoryId = categoryId, ExpenseDate = new DateOnly(2026, 10, 1), CurrencyId = currencyId,
                Amount = 1, BaseAmount = 1, PaymentMethod = PaymentMethod.Cash, CreatedByUserId = userId,
            };
            db.Expenses.Add(blocker);
            await db.SaveChangesAsync();
            blockerId = blocker.Id;
        }
        Assert.Equal(1L, await NextValueAsync(database, DefaultTenant.CompanyId, "EXP"));

        await using (var db = database.CreateDbContext())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => Expenses(db).CreateAsync(Input(categoryId, currencyId), userId));
        }

        Assert.Equal(1L, await NextValueAsync(database, DefaultTenant.CompanyId, "EXP")); // the counter increment was rolled back
        await CompanyMigrations.ExecuteAsync(database, $"""DELETE FROM "Expenses" WHERE "Id" = '{blockerId}'""");
        await using (var db = database.CreateDbContext())
        {
            var created = await Expenses(db).CreateAsync(Input(categoryId, currencyId), userId);
            Assert.Equal("EXP-0001", created.ExpenseNo);
        }
        Assert.Equal(2L, await NextValueAsync(database, DefaultTenant.CompanyId, "EXP"));
    }

    [Fact]
    public async Task CreateExpense_FirstDocumentOfCompanyWithoutSequenceRow_CreatesTheCounterAndStartsAtOne()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var (_, currencyId, userId) = await SeedDefaultAsync(database);
        var categoryB = await SeedCompanyAsync(database, CompanyB, currencyId);
        Assert.Equal(0L, await CompanyMigrations.ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "DocumentSequences" WHERE "CompanyId" = '{CompanyB}'"""));

        await using (var db = database.CreateDbContext(CompanyB))
        {
            var first = await Expenses(db).CreateAsync(Input(categoryB, currencyId), userId);
            var second = await Expenses(db).CreateAsync(Input(categoryB, currencyId), userId);
            Assert.Equal(["EXP-0001", "EXP-0002"], new[] { first.ExpenseNo, second.ExpenseNo });
        }

        Assert.Equal(3L, await NextValueAsync(database, CompanyB, "EXP"));
        Assert.Equal(1L, await CompanyMigrations.ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "DocumentSequences" WHERE "CompanyId" = '{CompanyB}'"""));
    }

    [Fact]
    public async Task CreateExpense_TwoFirstDocumentsOfANewCompanyRacingOnTheCounterInsert_BothSucceedWithDistinctNumbers()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var (_, currencyId, userId) = await SeedDefaultAsync(database);
        var categoryC = await SeedCompanyAsync(database, CompanyC, currencyId);

        // The gate stands in for a concurrent first creator: it inserts the counter row and keeps it uncommitted.
        await using var race = await LockRace.BeginAsync(database, gate => gate.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "DocumentSequences" ("Id", "CompanyId", "BranchId", "Series", "NextValue", "UpdatedAt") VALUES ({Guid.NewGuid()}, {CompanyC}, NULL, 'EXP', 1, now())"""));
        var first = await race.StartAsync("first", db => Expenses(db).CreateAsync(Input(categoryC, currencyId), userId), CompanyC);
        await race.WaitBlockedAsync(first, """%INSERT INTO "DocumentSequences"%""");
        var second = await race.StartAsync("second", db => Expenses(db).CreateAsync(Input(categoryC, currencyId), userId), CompanyC);
        await race.WaitBlockedAsync(second, """%INSERT INTO "DocumentSequences"%""");

        var outcomes = await race.ReleaseAsync(commit: true);

        Assert.All(outcomes, Assert.Null);
        await using var check = database.CreateDbContext(CompanyC);
        Assert.Equal(["EXP-0001", "EXP-0002"], await check.Expenses.OrderBy(e => e.ExpenseNo).Select(e => e.ExpenseNo).ToListAsync());
        Assert.Equal(3L, await NextValueAsync(database, CompanyC, "EXP"));
    }
}
