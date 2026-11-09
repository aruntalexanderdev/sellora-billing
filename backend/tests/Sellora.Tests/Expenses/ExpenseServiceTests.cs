using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Expenses;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.Expenses;

public class ExpenseServiceTests
{
    private sealed record Fixture(
        SelloraDbContext Db, ExpenseService Service, Currency Inr, Currency Usd, ExchangeRate UsdRate,
        ExpenseCategory Rent, ExpenseCategory Old, Guid UserId);

    private static async Task<Fixture> SeedAsync()
    {
        var db = Sellora.Tests.TestData.TestCompany.NewDb(new DbContextOptionsBuilder<SelloraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin",
            Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var usd = new Currency { Id = Guid.NewGuid(), Code = "USD", Name = "US Dollar", Symbol = "$" };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var rate = new ExchangeRate { Id = Guid.NewGuid(), CurrencyId = usd.Id, RateToBase = 83.20m, EffectiveDate = new DateOnly(2026, 9, 1), CreatedByUserId = user.Id };
        var rent = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Rent" };
        var old = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Old", IsActive = false };

        db.AddRange(role, user, inr, usd, settings, rate, rent, old);
        await db.SaveChangesAsync();
        return new Fixture(db, new ExpenseService(db, new ExchangeRateService(db)), inr, usd, rate, rent, old, user.Id);
    }

    private static CreateExpenseInput Input(Fixture f, Currency currency, decimal amount, DateOnly? date = null) =>
        new(f.Rent.Id, date ?? new DateOnly(2026, 9, 5), currency.Id, amount, PaymentMethod.BankTransfer, " INV-77 ", null);

    [Fact]
    public async Task CreateAsync_BaseCurrency_UsesRateOfOne_AndNumbersSequentially()
    {
        var f = await SeedAsync();

        var first = await f.Service.CreateAsync(Input(f, f.Inr, 12000m), f.UserId);
        var second = await f.Service.CreateAsync(Input(f, f.Inr, 500m), f.UserId);

        Assert.Equal("EXP-0001", first.ExpenseNo);
        Assert.Equal("EXP-0002", second.ExpenseNo);
        Assert.Equal(1m, first.ExchangeRateToBase);
        Assert.Equal(12000m, first.BaseAmount);
        Assert.Equal("INV-77", first.Reference);
        Assert.Equal(ExpenseStatus.Active, first.Status);
    }

    [Fact]
    public async Task CreateAsync_ForeignCurrency_SnapshotsRateAndConvertsToBase()
    {
        var f = await SeedAsync();

        var expense = await f.Service.CreateAsync(Input(f, f.Usd, 100.005m), f.UserId);

        Assert.Equal(100.01m, expense.Amount); // rounded to USD precision
        Assert.Equal(83.20m, expense.ExchangeRateToBase);
        Assert.Equal(8320.83m, expense.BaseAmount); // 100.01 x 83.20 = 8320.832

        // A later rate change never touches the saved expense.
        f.UsdRate.RateToBase = 99m;
        await f.Db.SaveChangesAsync();
        var reloaded = await f.Db.Expenses.AsNoTracking().SingleAsync();
        Assert.Equal(83.20m, reloaded.ExchangeRateToBase);
        Assert.Equal(8320.83m, reloaded.BaseAmount);
    }

    [Fact]
    public async Task CreateAsync_ForeignCurrencyWithoutRate_IsRejected()
    {
        var f = await SeedAsync();

        await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Service.CreateAsync(Input(f, f.Usd, 10m, new DateOnly(2026, 8, 1)), f.UserId));
        Assert.Empty(await f.Db.Expenses.ToListAsync());
    }

    [Theory]
    [InlineData(0, "Amount")]
    [InlineData(-5, "Amount")]
    public async Task CreateAsync_NonPositiveAmount_IsRejected(double amount, string field)
    {
        var f = await SeedAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateAsync(Input(f, f.Inr, (decimal)amount), f.UserId));
        Assert.Equal(field, ex.Field);
    }

    [Fact]
    public async Task CreateAsync_InactiveOrUnknownCategory_FutureDate_AreRejected()
    {
        var f = await SeedAsync();

        var inactive = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Service.CreateAsync(Input(f, f.Inr, 10m) with { CategoryId = f.Old.Id }, f.UserId));
        Assert.Equal("CategoryId", inactive.Field);

        var unknown = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Service.CreateAsync(Input(f, f.Inr, 10m) with { CategoryId = Guid.NewGuid() }, f.UserId));
        Assert.Equal("CategoryId", unknown.Field);

        var future = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Service.CreateAsync(Input(f, f.Inr, 10m, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10))), f.UserId));
        Assert.Equal("ExpenseDate", future.Field);
    }

    [Fact]
    public async Task UpdateAsync_ChangesOnlyCategoryReferenceAndNotes()
    {
        var f = await SeedAsync();
        var other = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Salary" };
        f.Db.ExpenseCategories.Add(other);
        await f.Db.SaveChangesAsync();
        var expense = await f.Service.CreateAsync(Input(f, f.Usd, 100m), f.UserId);

        var updated = await f.Service.UpdateAsync(expense.Id, new UpdateExpenseInput(other.Id, "R-1", "note"));

        Assert.Equal(other.Id, updated!.CategoryId);
        Assert.Equal("R-1", updated.Reference);
        Assert.NotNull(updated.UpdatedAt);
        Assert.Equal(100m, updated.Amount);
        Assert.Equal(8320m, updated.BaseAmount);
        Assert.Equal(PaymentMethod.BankTransfer, updated.PaymentMethod);
    }

    [Fact]
    public async Task UpdateAsync_KeepsItsOwnInactiveCategory_ButRejectsSwitchingToOne()
    {
        var f = await SeedAsync();
        var expense = await f.Service.CreateAsync(Input(f, f.Inr, 10m), f.UserId);
        f.Rent.IsActive = false;
        await f.Db.SaveChangesAsync();

        Assert.NotNull(await f.Service.UpdateAsync(expense.Id, new UpdateExpenseInput(f.Rent.Id, "x", null)));
        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Service.UpdateAsync(expense.Id, new UpdateExpenseInput(f.Old.Id, null, null)));
        Assert.Equal("CategoryId", ex.Field);
    }

    [Fact]
    public async Task CancelAsync_KeepsTheRecord_RecordsWhoAndWhy_AndBlocksFurtherChanges()
    {
        var f = await SeedAsync();
        var expense = await f.Service.CreateAsync(Input(f, f.Inr, 10m), f.UserId);

        var cancelled = await f.Service.CancelAsync(expense.Id, " entered twice ", f.UserId);

        Assert.Equal(ExpenseStatus.Cancelled, cancelled!.Status);
        Assert.Equal(f.UserId, cancelled.CancelledByUserId);
        Assert.Equal("entered twice", cancelled.CancellationReason);
        Assert.NotNull(cancelled.CancelledAt);
        Assert.Single(await f.Db.Expenses.ToListAsync());

        var again = await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CancelAsync(expense.Id, null, f.UserId));
        Assert.Equal("Status", again.Field);
        var edit = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Service.UpdateAsync(expense.Id, new UpdateExpenseInput(f.Rent.Id, null, null)));
        Assert.Equal("Status", edit.Field);
        Assert.Null(await f.Service.CancelAsync(Guid.NewGuid(), null, f.UserId));
        Assert.Null(await f.Service.UpdateAsync(Guid.NewGuid(), new UpdateExpenseInput(f.Rent.Id, null, null)));
    }

    [Fact]
    public async Task CreateAsync_WritesAnExpensePaymentTransaction_AndCancelMarksItCancelled()
    {
        var f = await SeedAsync();
        var expense = await f.Service.CreateAsync(Input(f, f.Usd, 100m), f.UserId);

        var txn = await f.Db.FinancialTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(FinancialTransactionType.ExpensePayment, txn.Type);
        Assert.Equal(MoneyDirection.Out, txn.Direction);
        Assert.Equal(FinancialTransactionStatus.Active, txn.Status);
        Assert.Equal(expense.Id, txn.ExpenseId);
        Assert.Equal(f.Usd.Id, txn.CurrencyId);
        Assert.Equal(100m, txn.Amount);
        Assert.Equal(83.20m, txn.ExchangeRateToBase);
        Assert.Equal(8320m, txn.BaseAmount);
        Assert.Equal(PaymentMethod.BankTransfer, txn.PaymentMethod);
        Assert.Equal(f.UserId, txn.CreatedByUserId);

        await f.Service.CancelAsync(expense.Id, null, f.UserId);

        var after = await f.Db.FinancialTransactions.AsNoTracking().SingleAsync(); // kept, not deleted
        Assert.Equal(FinancialTransactionStatus.Cancelled, after.Status);
        Assert.Equal(8320m, after.BaseAmount);
    }

    [Fact]
    public async Task RejectedExpense_WritesNoTransaction()
    {
        var f = await SeedAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Service.CreateAsync(Input(f, f.Inr, 0m), f.UserId));

        Assert.Empty(await f.Db.FinancialTransactions.ToListAsync());
    }
}
