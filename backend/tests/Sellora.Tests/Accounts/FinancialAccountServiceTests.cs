using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Accounts;
using Sellora.Domain.Entities;
using Sellora.Domain.Expenses;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Accounts;

public class FinancialAccountServiceTests
{
    private sealed record Fixture(
        SelloraDbContext Db, FinancialAccountService Accounts, PurchaseService Purchases, ISalesService Sales, ExpenseService Expenses,
        Supplier Supplier, Customer Customer, Product Product, ProductUnit Kg, ExpenseCategory Rent, Guid UserId);

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
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Kilogram", ShortCode = "kg", MeasurementCategory = MeasurementCategory.Weight, DecimalPlaces = 3, StandardFactor = 1m };
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 0%", RatePercent = 0m };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var product = new Product { Id = Guid.NewGuid(), Sku = "RICE", Name = "Rice", BaseUnitId = unit.Id, BaseUnit = unit, TaxId = tax.Id, Tax = tax, ReorderLevel = 1 };
        var kg = TestProductUnits.Base(product, "kg");
        var rent = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Rent" };

        db.AddRange(role, user, unit, tax, inr, settings, supplier, customer, product, rent);
        await db.SaveChangesAsync();

        var rates = new ExchangeRateService(db);
        return new Fixture(db, new FinancialAccountService(db), new PurchaseService(db, rates), ListPricedSales.For(db), new ExpenseService(db, rates),
            supplier, customer, product, kg, rent, user.Id);
    }

    private static Task<FinancialAccount> Account(Fixture f, string name, FinancialAccountKind kind = FinancialAccountKind.Cash, decimal opening = 0m) =>
        f.Accounts.CreateAsync(new CreateAccountInput(name, kind, null, opening, new DateOnly(2026, 9, 1)), f.UserId);

    // A ledger-derived balance, exactly as the API computes it.
    private static async Task<decimal> BalanceAsync(Fixture f, Guid accountId) =>
        (await f.Db.FinancialTransactions.AsNoTracking()
            .Where(t => t.AccountId == accountId && t.Status == FinancialTransactionStatus.Active).ToListAsync())
        .Sum(t => t.Direction == MoneyDirection.In ? t.BaseAmount : -t.BaseAmount);

    [Fact]
    public async Task CreateAsync_OpeningBalanceBecomesTheFirstTransaction_ZeroWritesNone()
    {
        var f = await SeedAsync();

        var funded = await Account(f, "Main Cash", opening: 5000m);
        var empty = await Account(f, "Counter Cash");

        var txn = await f.Db.FinancialTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(FinancialTransactionType.OpeningBalance, txn.Type);
        Assert.Equal(MoneyDirection.In, txn.Direction);
        Assert.Equal(funded.Id, txn.AccountId);
        Assert.Equal(5000m, await BalanceAsync(f, funded.Id));
        Assert.Equal(0m, await BalanceAsync(f, empty.Id));
    }

    [Fact]
    public async Task CreateAsync_DuplicateNameNegativeOpeningAndFutureDate_AreRejected()
    {
        var f = await SeedAsync();
        await Account(f, "Main Cash");

        var dup = await Assert.ThrowsAsync<DomainValidationException>(() => Account(f, " main cash "));
        Assert.Equal("Name", dup.Field);
        var neg = await Assert.ThrowsAsync<DomainValidationException>(() => Account(f, "X", opening: -1m));
        Assert.Equal("OpeningBalance", neg.Field);
        var future = await Assert.ThrowsAsync<DomainValidationException>(
            () => f.Accounts.CreateAsync(new CreateAccountInput("Y", FinancialAccountKind.Bank, null, 10m, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30))), f.UserId));
        Assert.Equal("OpeningDate", future.Field);
    }

    [Fact]
    public async Task Payments_MoveTheChosenAccountsBalance_AndUntrackedOnesMoveNone()
    {
        var f = await SeedAsync();
        var cash = await Account(f, "Main Cash", opening: 10000m);
        var bank = await Account(f, "SBI", FinancialAccountKind.Bank, opening: 20000m);

        // Sale: 10 kg @ 100 = 1000, 600 received into cash, then 400 into bank.
        var purchase = await f.Purchases.CreatePurchaseAsync(new CreatePurchaseInput(f.Supplier.Id, new DateOnly(2026, 9, 5), null, null,
            PaymentMethod.BankTransfer, 3000m, [new PurchaseLineInput(f.Product.Id, f.Kg.Id, 100, 50, 0, 0)], bank.Id), f.UserId); // 5000, pay 3000 from bank
        var sale = await f.Sales.CreateSaleAsync(new CreateSaleInput(f.Customer.Id, new DateOnly(2026, 9, 6), PaymentMethod.Cash, 600m,
            [new SaleLineInput(f.Product.Id, f.Kg.Id, 10, 100, 0)], cash.Id), f.UserId);
        await f.Sales.AddPaymentAsync(sale.Id, new AddSalePaymentInput(400m, PaymentMethod.Upi, 400m, bank.Id), f.UserId);
        await f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(500m, PaymentMethod.Cash, 2000m, cash.Id), f.UserId);
        await f.Expenses.CreateAsync(new CreateExpenseInput(f.Rent.Id, new DateOnly(2026, 9, 7), (await f.Db.Currencies.SingleAsync()).Id, 250m, PaymentMethod.Cash, null, null, cash.Id), f.UserId);
        await f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(100m, PaymentMethod.Cash, 1500m), f.UserId); // no account

        Assert.Equal(10000m + 600m - 500m - 250m, await BalanceAsync(f, cash.Id));
        Assert.Equal(20000m - 3000m + 400m, await BalanceAsync(f, bank.Id));
    }

    [Fact]
    public async Task CancellingAnExpense_ReturnsTheMoneyToTheAccountBalance()
    {
        var f = await SeedAsync();
        var cash = await Account(f, "Main Cash", opening: 1000m);
        var expense = await f.Expenses.CreateAsync(new CreateExpenseInput(f.Rent.Id, new DateOnly(2026, 9, 7), (await f.Db.Currencies.SingleAsync()).Id, 250m, PaymentMethod.Cash, null, null, cash.Id), f.UserId);
        Assert.Equal(750m, await BalanceAsync(f, cash.Id));

        await f.Expenses.CancelAsync(expense.Id, null, f.UserId);

        Assert.Equal(1000m, await BalanceAsync(f, cash.Id));
    }

    [Fact]
    public async Task Payment_ToUnknownOrInactiveAccount_IsRejected_AndWritesNothing()
    {
        var f = await SeedAsync();
        var closed = await Account(f, "Closed");
        closed.IsActive = false;
        await f.Db.SaveChangesAsync();
        var before = await f.Db.FinancialTransactions.CountAsync();

        foreach (var id in new[] { Guid.NewGuid(), closed.Id })
        {
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Expenses.CreateAsync(
                new CreateExpenseInput(f.Rent.Id, new DateOnly(2026, 9, 7), (f.Db.Currencies.Single()).Id, 10m, PaymentMethod.Cash, null, null, id), f.UserId));
            Assert.Equal("AccountId", ex.Field);
        }
        Assert.Equal(before, await f.Db.FinancialTransactions.CountAsync());
        Assert.Empty(await f.Db.Expenses.ToListAsync());
    }

    [Fact]
    public async Task TransferAsync_WritesTwoLinkedLegs_MovesMoneyAndKeepsTheTotalUnchanged()
    {
        var f = await SeedAsync();
        var cash = await Account(f, "Main Cash", opening: 5000m);
        var bank = await Account(f, "HDFC", FinancialAccountKind.Bank, opening: 1000m);

        await f.Accounts.TransferAsync(new TransferInput(cash.Id, bank.Id, 2000m, new DateOnly(2026, 9, 10), " deposit "), f.UserId);

        var legs = await f.Db.FinancialTransactions.AsNoTracking().Where(t => t.Type == FinancialTransactionType.Transfer).ToListAsync();
        Assert.Equal(2, legs.Count);
        Assert.Single(legs.Select(l => l.TransferGroupId).Distinct());
        Assert.Equal(MoneyDirection.Out, legs.Single(l => l.AccountId == cash.Id).Direction);
        Assert.Equal(MoneyDirection.In, legs.Single(l => l.AccountId == bank.Id).Direction);
        Assert.All(legs, l => Assert.Equal("deposit", l.Notes));
        Assert.Equal(3000m, await BalanceAsync(f, cash.Id));
        Assert.Equal(3000m, await BalanceAsync(f, bank.Id));
    }

    [Fact]
    public async Task TransferAsync_InvalidRequests_AreRejected_AndWriteNothing()
    {
        var f = await SeedAsync();
        var cash = await Account(f, "Main Cash", opening: 100m);
        var bank = await Account(f, "HDFC", FinancialAccountKind.Bank);
        var closed = await Account(f, "Closed");
        closed.IsActive = false;
        await f.Db.SaveChangesAsync();
        var before = await f.Db.FinancialTransactions.CountAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        async Task<string> Field(TransferInput input) =>
            (await Assert.ThrowsAsync<DomainValidationException>(() => f.Accounts.TransferAsync(input, f.UserId))).Field;

        Assert.Equal("ToAccountId", await Field(new(cash.Id, cash.Id, 10m, today, null)));
        Assert.Equal("ToAccountId", await Field(new(cash.Id, Guid.NewGuid(), 10m, today, null)));
        Assert.Equal("FromAccountId", await Field(new(closed.Id, bank.Id, 10m, today, null)));
        Assert.Equal("Amount", await Field(new(cash.Id, bank.Id, 0m, today, null)));
        Assert.Equal("Amount", await Field(new(cash.Id, bank.Id, -5m, today, null)));
        Assert.Equal("TransferDate", await Field(new(cash.Id, bank.Id, 10m, today.AddDays(30), null)));
        Assert.Equal(before, await f.Db.FinancialTransactions.CountAsync());
    }

    [Fact]
    public async Task Reconcile_MarksAndClears_AndRejectsForeignOrCancelledTransactions()
    {
        var f = await SeedAsync();
        var cash = await Account(f, "Main Cash", opening: 1000m);
        var other = await Account(f, "Counter", opening: 50m);
        var opening = await f.Db.FinancialTransactions.SingleAsync(t => t.AccountId == cash.Id);
        var otherOpening = await f.Db.FinancialTransactions.SingleAsync(t => t.AccountId == other.Id);

        Assert.Equal(1, await f.Accounts.ReconcileAsync(cash.Id, [opening.Id, opening.Id], f.UserId));
        Assert.NotNull((await f.Db.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Id == opening.Id)).ReconciledAt);
        Assert.Equal(0, await f.Accounts.ReconcileAsync(cash.Id, [opening.Id], f.UserId)); // already reconciled: untouched

        var foreign = await Assert.ThrowsAsync<DomainValidationException>(() => f.Accounts.ReconcileAsync(cash.Id, [otherOpening.Id], f.UserId));
        Assert.Equal("TransactionIds", foreign.Field);
        Assert.Null(await f.Accounts.ReconcileAsync(Guid.NewGuid(), [opening.Id], f.UserId));

        Assert.Equal(1, await f.Accounts.UnreconcileAsync(cash.Id, [opening.Id]));
        Assert.Null((await f.Db.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Id == opening.Id)).ReconciledAt);

        var expense = await f.Expenses.CreateAsync(new CreateExpenseInput(f.Rent.Id, new DateOnly(2026, 9, 7), (await f.Db.Currencies.SingleAsync()).Id, 10m, PaymentMethod.Cash, null, null, cash.Id), f.UserId);
        await f.Expenses.CancelAsync(expense.Id, null, f.UserId);
        var cancelled = await f.Db.FinancialTransactions.SingleAsync(t => t.ExpenseId == expense.Id);
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => f.Accounts.ReconcileAsync(cash.Id, [cancelled.Id], f.UserId));
        Assert.Equal("TransactionIds", ex.Field);
    }

    [Fact]
    public async Task UpdateAsync_RenamesWithoutChangingKind_AndRejectsDuplicates()
    {
        var f = await SeedAsync();
        var cash = await Account(f, "Main Cash");
        await Account(f, "SBI", FinancialAccountKind.Bank);

        var updated = await f.Accounts.UpdateAsync(cash.Id, new UpdateAccountInput("Front Counter", " drawer 1 "));
        Assert.Equal("Front Counter", updated!.Name);
        Assert.Equal("drawer 1", updated.Reference);
        Assert.Equal(FinancialAccountKind.Cash, updated.Kind);

        var dup = await Assert.ThrowsAsync<DomainValidationException>(() => f.Accounts.UpdateAsync(cash.Id, new UpdateAccountInput("sbi", null)));
        Assert.Equal("Name", dup.Field);
        Assert.Null(await f.Accounts.UpdateAsync(Guid.NewGuid(), new UpdateAccountInput("Z", null)));
    }
}
