using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Expenses;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// Expense numbers (<c>EXP-0001</c>…) are a row count + 1, so concurrent creates must be serialised by the same
/// per-series advisory lock as the other documents. A gate transaction holds that lock; both creates must wait on it
/// and, once released, end with distinct sequential numbers. Without the lock both writers finish while the gate is
/// held (WaitBlockedAsync fails) and the second one collides on the ExpenseNo unique index.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ExpenseNumberConcurrencyTests(PostgresFixture postgres)
{
    private const string ExpenseSeriesLock = "SELECT pg_advisory_xact_lock(hashtext('sellora:docno:EXP'))";

    [Fact]
    public async Task CreateExpense_Concurrent_WaitForTheSeriesLockAndGetDistinctNumbers()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var (categoryId, currencyId, userId) = await SeedAsync(database);
        var input = new CreateExpenseInput(categoryId, new DateOnly(2026, 11, 1), currencyId, 100m, PaymentMethod.Cash, null, null);

        await using var race = await LockRace.BeginAsync(database, gate => gate.Database.ExecuteSqlRawAsync(ExpenseSeriesLock));
        var first = await race.StartAsync("first", db => new ExpenseService(db, new ExchangeRateService(db)).CreateAsync(input, userId));
        await race.WaitBlockedAsync(first, "%pg_advisory_xact_lock%");
        var second = await race.StartAsync("second", db => new ExpenseService(db, new ExchangeRateService(db)).CreateAsync(input, userId));
        await race.WaitBlockedAsync(second, "%pg_advisory_xact_lock%");

        var outcomes = await race.ReleaseAsync();

        Assert.All(outcomes, Assert.Null);
        await using var check = database.CreateDbContext();
        var numbers = await check.Expenses.OrderBy(e => e.ExpenseNo).Select(e => e.ExpenseNo).ToListAsync();
        Assert.Equal(["EXP-0001", "EXP-0002"], numbers);
    }

    private static async Task<(Guid CategoryId, Guid CurrencyId, Guid UserId)> SeedAsync(TestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var settings = await db.BusinessSettings.SingleOrDefaultAsync();
        Guid currencyId;
        if (settings is null)
        {
            var inr = new Currency { Id = Guid.NewGuid(), Code = "XTS", Name = "Test Rupee", Symbol = "₹" };
            db.AddRange(inr, new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id });
            currencyId = inr.Id;
        }
        else
        {
            currencyId = settings.BaseCurrencyId;
        }

        var role = new Role { Id = Guid.NewGuid(), Name = "Expense Race Admin", IsSystem = false };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Race", LastName = "Tester", Username = "expense-race",
            Email = "expense-race@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var category = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Race Rent" };
        db.AddRange(role, user, category);
        await db.SaveChangesAsync();
        return (category.Id, currencyId, user.Id);
    }
}
