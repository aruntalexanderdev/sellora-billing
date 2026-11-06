using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Accounts;
using Sellora.Domain.Entities;
using Sellora.Domain.Expenses;
using Sellora.Domain.Payments;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Reports;

public class AccountsReportsTests
{
    private sealed record Fixture(
        SelloraDbContext Db, AccountsReportsController Reports, PurchaseService Purchases, SalesService Sales,
        FinancialAccountService Accounts, ExpenseService Expenses,
        Currency Inr, Currency Usd, Supplier InrSupplier, Supplier UsdSupplier, Customer Customer, Customer Regular,
        Product Product, ProductUnit Kg, ExpenseCategory Rent, ExpenseCategory Power, Guid UserId);

    private static async Task<Fixture> SeedAsync()
    {
        var db = new SelloraDbContext(new DbContextOptionsBuilder<SelloraDbContext>()
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
        var usd = new Currency { Id = Guid.NewGuid(), Code = "USD", Name = "US Dollar", Symbol = "$" };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var inrSupplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var usdSupplier = new Supplier { Id = Guid.NewGuid(), Name = "Global Ingredients", CurrencyId = usd.Id };
        var walkIn = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var regular = new Customer { Id = Guid.NewGuid(), Name = "Kavitha Stores" };
        var product = new Product { Id = Guid.NewGuid(), Sku = "RICE", Name = "Rice", BaseUnitId = unit.Id, BaseUnit = unit, TaxId = tax.Id, Tax = tax, ReorderLevel = 1 };
        var kg = TestProductUnits.Base(product, "kg");
        var rate = new ExchangeRate { Id = Guid.NewGuid(), CurrencyId = usd.Id, RateToBase = 83.20m, EffectiveDate = new DateOnly(2026, 9, 1), CreatedByUserId = user.Id };
        var rent = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Rent" };
        var power = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Electricity" };

        db.AddRange(role, user, unit, tax, inr, usd, settings, inrSupplier, usdSupplier, walkIn, regular, product, rate, rent, power);
        await db.SaveChangesAsync();

        var rates = new ExchangeRateService(db);
        return new Fixture(db, new AccountsReportsController(db), new PurchaseService(db, rates), new SalesService(db),
            new FinancialAccountService(db), new ExpenseService(db, rates), inr, usd, inrSupplier, usdSupplier, walkIn, regular,
            product, kg, rent, power, user.Id);
    }

    private static readonly DateOnly D1 = new(2026, 8, 20);
    private static readonly DateOnly D2 = new(2026, 9, 5);
    private static readonly DateOnly D3 = new(2026, 9, 12);

    private static T Value<T>(ActionResult<T> r) => (T)((OkObjectResult)r.Result!).Value!;

    private static Task<PurchaseOrder> Buy(Fixture f, Supplier s, DateOnly date, decimal qty, decimal rate, decimal paid = 0m, Guid? account = null, PaymentMethod method = PaymentMethod.Cash) =>
        f.Purchases.CreatePurchaseAsync(new CreatePurchaseInput(s.Id, date, null, null, method, paid,
            [new PurchaseLineInput(f.Product.Id, f.Kg.Id, qty, rate, 0, 0)], account), f.UserId);

    private static Task<SalesInvoice> Sell(Fixture f, Customer c, DateOnly date, decimal qty, decimal price, decimal paid = 0m, Guid? account = null, PaymentMethod method = PaymentMethod.Cash) =>
        f.Sales.CreateSaleAsync(new CreateSaleInput(c.Id, date, method, paid,
            [new SaleLineInput(f.Product.Id, f.Kg.Id, qty, price, 0)], account), f.UserId);

    private static Task<Expense> Spend(Fixture f, ExpenseCategory cat, DateOnly date, decimal amount, Guid? account = null, PaymentMethod method = PaymentMethod.Cash, Currency? currency = null) =>
        f.Expenses.CreateAsync(new CreateExpenseInput(cat.Id, date, (currency ?? f.Inr).Id, amount, method, null, null, account), f.UserId);

    private static Task<FinancialAccount> Account(Fixture f, string name, FinancialAccountKind kind, decimal opening, DateOnly date) =>
        f.Accounts.CreateAsync(new CreateAccountInput(name, kind, null, opening, date), f.UserId);

    // ---------------------------------------------------------------- payment reports

    [Fact]
    public async Task PurchasePayments_ListEveryEntry_InBaseCurrency_WithNetSummaryAndCurrentStatus()
    {
        var f = await SeedAsync();
        var inr = await Buy(f, f.InrSupplier, D2, 100, 50m, paid: 2000m);                 // ₹5,000; paid 2,000
        var usd = await Buy(f, f.UsdSupplier, D2, 10, 100m, paid: 1000m);                 // USD 1,000 = ₹83,200; fully paid
        await f.Purchases.AddPaymentAsync(inr.Id, new AddPurchasePaymentInput(1000m, PaymentMethod.Upi, 3000m), f.UserId);
        await f.Purchases.ReversePaymentAsync(inr.Id, (await f.Db.PurchasePayments.FirstAsync(p => p.Amount == 2000m)).Id, new ReversePaymentInput("oops"), f.UserId);

        var report = Value(await f.Reports.PurchasePayments(null, null, null, null, null, null, 1, 50, default));

        Assert.Equal(4, report.Total); // 3 payments + 1 reversal
        Assert.Equal(2000m + 1000m + 83200m, report.Payments);
        Assert.Equal(2000m, report.Reversals);
        Assert.Equal(0m, report.Refunds);
        Assert.Equal(84200m, report.Net);
        Assert.Equal("INR", report.BaseCurrency.Code);

        var foreign = report.Items.Single(r => r.CurrencyCode == "USD");
        Assert.Equal(1000m, foreign.Amount);
        Assert.Equal(83.20m, foreign.ExchangeRateToBase);
        Assert.Equal(83200m, foreign.BaseAmount);
        Assert.Equal(PaymentStatus.Paid, foreign.DocumentPaymentStatus);
        Assert.Equal(PaymentStatus.PartiallyPaid, report.Items.First(r => r.DocumentNo == inr.PurchaseNo).DocumentPaymentStatus); // 1,000 of 5,000 now
        Assert.Contains(report.Items, r => r.Kind == PaymentEntryKind.Reversal && r.Reason == "oops");
    }

    [Fact]
    public async Task PurchasePayments_Filters_ByMethodKindSupplierDateAndSearch()
    {
        var f = await SeedAsync();
        var bank = await Account(f, "SBI", FinancialAccountKind.Bank, 0m, D1);
        var a = await Buy(f, f.InrSupplier, D1, 100, 50m, paid: 1000m, account: bank.Id);
        await Buy(f, f.UsdSupplier, D2, 10, 100m, paid: 500m, method: PaymentMethod.BankTransfer);
        await f.Purchases.AddPaymentAsync(a.Id, new AddPurchasePaymentInput(200m, PaymentMethod.Upi, 4000m), f.UserId);

        Assert.Equal(1, Value(await f.Reports.PurchasePayments(null, PaymentMethod.Upi, null, null, null, null, 1, 50, default)).Total);
        Assert.Equal(1, Value(await f.Reports.PurchasePayments(null, null, null, f.UsdSupplier.Id, null, null, 1, 50, default)).Total);
        Assert.Equal(2, Value(await f.Reports.PurchasePayments("annapoorna", null, null, null, null, null, 1, 50, default)).Total);
        Assert.Equal(1, Value(await f.Reports.PurchasePayments(null, null, null, null, D2, D2, 1, 50, default)).Total);
        Assert.Equal(0, Value(await f.Reports.PurchasePayments(null, null, PaymentEntryKind.Refund, null, null, null, 1, 50, default)).Total);
        Assert.Equal("SBI", Value(await f.Reports.PurchasePayments(null, PaymentMethod.Cash, null, f.InrSupplier.Id, null, null, 1, 50, default)).Items.Single().AccountName);
    }

    [Fact]
    public async Task SalesPayments_ShowPaymentsRefundsAndReversals_AndTheirNet()
    {
        var f = await SeedAsync();
        await Buy(f, f.InrSupplier, D1, 100, 50m);
        var sale = await Sell(f, f.Customer, D2, 10, 100m, paid: 600m);
        await f.Sales.AddPaymentAsync(sale.Id, new AddSalePaymentInput(400m, PaymentMethod.Upi, 400m), f.UserId);
        await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D2, "damaged", [new SaleReturnLineInput(sale.Lines.Single().Id, 3)], new RefundDetails(300m, PaymentMethod.Cash)), f.UserId);
        var wrong = await Sell(f, f.Regular, D3, 1, 100m, paid: 50m);
        await f.Sales.ReversePaymentAsync(wrong.Id, (await f.Db.SalesPayments.FirstAsync(p => p.SalesInvoiceId == wrong.Id)).Id, new ReversePaymentInput(null), f.UserId);

        var report = Value(await f.Reports.SalesPayments(null, null, null, null, null, null, 1, 50, default));

        Assert.Equal(5, report.Total);
        Assert.Equal(1050m, report.Payments);
        Assert.Equal(300m, report.Refunds);
        Assert.Equal(50m, report.Reversals);
        Assert.Equal(700m, report.Net);
        Assert.All(report.Items, r => Assert.Equal("INR", r.CurrencyCode));
        Assert.Equal(1m, report.Items.First().ExchangeRateToBase);
        Assert.Equal(2, Value(await f.Reports.SalesPayments("kavitha", null, null, null, null, null, 1, 50, default)).Total); // Kavitha's payment and its reversal
    }

    // ---------------------------------------------------------------- outstanding

    [Fact]
    public async Task SupplierOutstanding_TotalsPerSupplier_AfterReturnsReversalsAndCancellations()
    {
        var f = await SeedAsync();
        var a1 = await Buy(f, f.InrSupplier, D1, 100, 50m, paid: 2000m);         // 5,000; due 3,000
        var a2 = await Buy(f, f.InrSupplier, D2, 10, 50m);                       // 500; unpaid
        await f.Purchases.CreateReturnAsync(new CreatePurchaseReturnInput(a1.Id, D2, null, [new PurchaseReturnLineInput(a1.Lines.Single().Id, null, 20)]), f.UserId); // -1,000 → net 4,000, due 2,000
        var cancelled = await Buy(f, f.InrSupplier, D3, 10, 50m, paid: 300m);    // paid 300 then cancelled → credit 300
        await f.Purchases.CancelPurchaseAsync(cancelled.Id, f.UserId);
        var foreign = await Buy(f, f.UsdSupplier, D2, 10, 100m, paid: 400m);     // USD 1,000; paid 400 → due USD 600 = ₹49,920
        Assert.NotNull(foreign);

        var report = Value(await f.Reports.SupplierOutstanding(null, false, null, null, 1, 50, default));

        var annapoorna = report.Items.Single(r => r.PartyName == "Annapoorna Traders");
        Assert.Equal(2, annapoorna.DocumentCount);
        Assert.Equal(5500m, annapoorna.Total);
        Assert.Equal(1000m, annapoorna.Returned);
        Assert.Equal(4500m, annapoorna.Net);
        Assert.Equal(2000m, annapoorna.Paid);
        Assert.Equal(2500m, annapoorna.Outstanding); // 2,000 on the first + 500 on the second
        Assert.Equal(300m, annapoorna.CreditBalance); // money held on the cancelled purchase
        Assert.Equal(1, annapoorna.UnpaidCount);
        Assert.Equal(1, annapoorna.PartialCount);
        Assert.Equal(0, annapoorna.PaidCount);

        var global = report.Items.Single(r => r.PartyName == "Global Ingredients");
        Assert.Equal(83200m, global.Total);
        Assert.Equal(33280m, global.Paid); // 400 × 83.20
        Assert.Equal(49920m, global.Outstanding);

        Assert.Equal(annapoorna.Outstanding + global.Outstanding, report.TotalOutstanding);
        Assert.Equal("Global Ingredients", report.Items[0].PartyName); // largest outstanding first
    }

    [Fact]
    public async Task SupplierOutstanding_OnlyOutstanding_DateRange_Search_AndTotalsCoverEveryPage()
    {
        var f = await SeedAsync();
        await Buy(f, f.InrSupplier, D1, 10, 50m, paid: 500m);  // settled
        await Buy(f, f.UsdSupplier, D2, 10, 100m);             // owed
        var one = Value(await f.Reports.SupplierOutstanding(null, false, null, null, 1, 1, default));
        Assert.Single(one.Items);
        Assert.Equal(2, one.Total);
        Assert.Equal(83200m, one.TotalOutstanding); // both parties, not just the page

        var only = Value(await f.Reports.SupplierOutstanding(null, true, null, null, 1, 50, default));
        Assert.Equal(["Global Ingredients"], only.Items.Select(r => r.PartyName));
        Assert.Equal(["Annapoorna Traders"], Value(await f.Reports.SupplierOutstanding("annapoorna", false, null, null, 1, 50, default)).Items.Select(r => r.PartyName));
        Assert.Equal(["Annapoorna Traders"], Value(await f.Reports.SupplierOutstanding(null, false, null, D1, 1, 50, default)).Items.Select(r => r.PartyName)); // up to D1 only
    }

    [Fact]
    public async Task CustomerOutstanding_TotalsPerCustomer_WithReturnsRefundsAndCredit()
    {
        var f = await SeedAsync();
        await Buy(f, f.InrSupplier, D1, 200, 50m);
        var s1 = await Sell(f, f.Regular, D2, 10, 100m, paid: 400m);     // 1,000; due 600
        await Sell(f, f.Regular, D2, 5, 100m);                           // 500; unpaid
        var s3 = await Sell(f, f.Customer, D2, 10, 100m, paid: 1000m);   // fully paid
        await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(s3.Id, D3, null, [new SaleReturnLineInput(s3.Lines.Single().Id, 4)]), f.UserId); // credit 400 → refundable
        var cancelled = await Sell(f, f.Regular, D3, 1, 100m, paid: 100m);
        await f.Sales.CancelSaleAsync(cancelled.Id, f.UserId);
        Assert.NotNull(s1);

        var report = Value(await f.Reports.CustomerOutstanding(null, false, null, null, 1, 50, default));

        var kavitha = report.Items.Single(r => r.PartyName == "Kavitha Stores");
        Assert.Equal(2, kavitha.DocumentCount);
        Assert.Equal(1500m, kavitha.Total);
        Assert.Equal(400m, kavitha.Paid);
        Assert.Equal(1100m, kavitha.Outstanding);
        Assert.Equal(100m, kavitha.CreditBalance); // held on the cancelled sale
        Assert.Equal((1, 1), (kavitha.UnpaidCount, kavitha.PartialCount));

        var walkIn = report.Items.Single(r => r.PartyName == "Walk-in Customer");
        Assert.Equal(1000m, walkIn.Total);
        Assert.Equal(400m, walkIn.Returned);
        Assert.Equal(600m, walkIn.Net);
        Assert.Equal(0m, walkIn.Outstanding);
        Assert.Equal(400m, walkIn.CreditBalance); // paid 1,000 for goods now worth 600
        Assert.Equal(1, walkIn.PaidCount);

        Assert.Equal(["Kavitha Stores"], Value(await f.Reports.CustomerOutstanding(null, true, null, null, 1, 50, default)).Items.Select(r => r.PartyName));
        Assert.Equal(1100m, report.TotalOutstanding);
    }

    // ---------------------------------------------------------------- expenses

    [Fact]
    public async Task Expenses_ListActiveOnly_ByDefault_WithBaseTotalsAndCategoryBreakdown()
    {
        var f = await SeedAsync();
        await Spend(f, f.Rent, D1, 12000m);
        await Spend(f, f.Rent, D2, 12000m, method: PaymentMethod.BankTransfer);
        await Spend(f, f.Power, D2, 100m, currency: f.Usd); // USD 100 = ₹8,320
        var mistake = await Spend(f, f.Power, D3, 999m);
        await f.Expenses.CancelAsync(mistake.Id, "duplicate", f.UserId);

        var report = Value(await f.Reports.Expenses(null, null, null, false, null, null, 1, 50, default));

        Assert.Equal(3, report.Total);
        Assert.Equal(32320m, report.TotalBase);
        Assert.Equal(["Rent", "Electricity"], report.ByCategory.Select(c => c.Category));
        Assert.Equal(24000m, report.ByCategory[0].BaseTotal);
        Assert.Equal(8320m, report.ByCategory[1].BaseTotal);
        var foreign = report.Items.Single(r => r.CurrencyCode == "USD");
        Assert.Equal(100m, foreign.Amount);
        Assert.Equal(8320m, foreign.BaseAmount);
        Assert.Equal(83.20m, foreign.ExchangeRateToBase);

        var withCancelled = Value(await f.Reports.Expenses(null, null, null, true, null, null, 1, 50, default));
        Assert.Equal(4, withCancelled.Total);
        Assert.Equal(32320m, withCancelled.TotalBase); // a cancelled expense never counts toward the total
        Assert.Equal(1, Value(await f.Reports.Expenses(null, f.Power.Id, null, false, null, null, 1, 50, default)).Total);
        Assert.Equal(1, Value(await f.Reports.Expenses(null, null, PaymentMethod.BankTransfer, false, null, null, 1, 50, default)).Total);
        Assert.Equal(2, Value(await f.Reports.Expenses(null, null, null, false, D2, D3, 1, 50, default)).Total);
    }

    // ---------------------------------------------------------------- cash flow

    [Fact]
    public async Task CashFlow_SeparatesEveryKindOfMovement_AndNetsToMoneyInLessMoneyOut()
    {
        var f = await SeedAsync();
        var cash = await Account(f, "Main Cash", FinancialAccountKind.Cash, 10000m, D1);
        var bank = await Account(f, "SBI", FinancialAccountKind.Bank, 50000m, D1);
        await Buy(f, f.InrSupplier, D1, 200, 50m, paid: 4000m, account: bank.Id);                               // out 4,000
        var sale = await Sell(f, f.Customer, D2, 10, 100m, paid: 1000m, account: cash.Id);                      // in 1,000
        await f.Sales.CreateSaleAsync(new CreateSaleInput(f.Regular.Id, D2, PaymentMethod.Upi, 200m, [new SaleLineInput(f.Product.Id, f.Kg.Id, 5, 100, 0)], bank.Id), f.UserId); // in 200
        await Spend(f, f.Rent, D2, 1500m, account: cash.Id);                                                    // out 1,500
        await f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D3, null, [new SaleReturnLineInput(sale.Lines.Single().Id, 2)], new RefundDetails(200m, PaymentMethod.Cash, cash.Id)), f.UserId); // out 200
        var back = await Buy(f, f.UsdSupplier, D3, 10, 100m, paid: 1000m);                                      // out 83,200
        await f.Purchases.CreateReturnAsync(new CreatePurchaseReturnInput(back.Id, D3, null, [new PurchaseReturnLineInput(back.Lines.Single().Id, null, 1)], new RefundDetails(100m, PaymentMethod.BankTransfer, bank.Id)), f.UserId); // in 8,320
        var mistake = await Spend(f, f.Power, D3, 700m, account: cash.Id);
        await f.Expenses.CancelAsync(mistake.Id, null, f.UserId);                                               // never happened
        await f.Accounts.TransferAsync(new TransferInput(bank.Id, cash.Id, 5000m, D3, "withdrawal"), f.UserId); // internal

        var r = Value(await f.Reports.CashFlow(null, null, default));

        Assert.Equal(1200m, r.ReceivedFromCustomers);
        Assert.Equal(4000m + 83200m, r.PaidToSuppliers);
        Assert.Equal(1500m, r.Expenses);
        Assert.Equal(200m, r.CustomerRefunds);
        Assert.Equal(8320m, r.SupplierRefunds);
        Assert.Equal(200m - 8320m, r.Refunds);
        Assert.Equal((5000m, 5000m), (r.TransfersIn, r.TransfersOut));
        Assert.Equal(1200m + 8320m, r.MoneyIn);
        Assert.Equal(4000m + 83200m + 1500m + 200m, r.MoneyOut);
        Assert.Equal(r.MoneyIn - r.MoneyOut, r.Net); // transfers and opening balances are not part of it
        Assert.Equal(["2026-08", "2026-09"], r.Months.Select(m => m.Period));
        Assert.Equal(r.Net, r.Months.Sum(m => m.Net));
        Assert.Equal(-4000m, r.Months[0].Net);

        var september = Value(await f.Reports.CashFlow(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), default));
        Assert.Equal(["2026-09"], september.Months.Select(m => m.Period));
        Assert.Equal(0m, september.MoneyOut - (83200m + 1500m + 200m));
    }

    [Fact]
    public async Task CashFlow_ExcludesOpeningBalances_AndIsEmptyWhenNothingHappened()
    {
        var f = await SeedAsync();
        await Account(f, "Main Cash", FinancialAccountKind.Cash, 10000m, D1);

        var r = Value(await f.Reports.CashFlow(null, null, default));

        Assert.Equal(0m, r.MoneyIn);
        Assert.Equal(0m, r.MoneyOut);
        Assert.Equal(0m, r.Net);
        Assert.Empty(r.Months);
    }

    // ---------------------------------------------------------------- cash & bank

    [Fact]
    public async Task CashBank_ClosingAlwaysEqualsOpeningPlusMovements_ForAnyPeriod()
    {
        var f = await SeedAsync();
        var cash = await Account(f, "Main Cash", FinancialAccountKind.Cash, 10000m, D1);      // opened before the period
        await Buy(f, f.InrSupplier, D1, 200, 50m);
        await Sell(f, f.Customer, D1, 10, 100m, paid: 1000m, account: cash.Id);               // before the period: +1,000
        await Sell(f, f.Customer, D2, 10, 100m, paid: 800m, account: cash.Id);                // in period: +800
        await Spend(f, f.Rent, D2, 300m, account: cash.Id);                                   // in period: -300
        var bank = await Account(f, "SBI", FinancialAccountKind.Bank, 20000m, D2);            // opened inside the period
        await f.Accounts.TransferAsync(new TransferInput(cash.Id, bank.Id, 2000m, D3, null), f.UserId);
        await Spend(f, f.Power, D3.AddDays(3), 999m, account: cash.Id);                      // after the period: ignored

        var report = Value(await f.Reports.CashBank(D2, D3, default));

        var c = report.Items.Single(r => r.AccountName == "Main Cash");
        Assert.Equal(11000m, c.Opening);   // 10,000 + 1,000 before 5 Sep
        Assert.Equal(800m, c.MoneyIn);
        Assert.Equal(300m, c.MoneyOut);
        Assert.Equal((0m, 2000m), (c.TransfersIn, c.TransfersOut));
        Assert.Equal(9500m, c.Closing);    // 11,000 + 800 - 300 - 2,000

        var b = report.Items.Single(r => r.AccountName == "SBI");
        Assert.Equal(20000m, b.Opening);   // an opening balance entered during the period is its opening
        Assert.Equal((0m, 0m), (b.MoneyIn, b.MoneyOut));
        Assert.Equal(22000m, b.Closing);

        foreach (var row in report.Items)
        {
            Assert.Equal(row.Opening + row.MoneyIn - row.MoneyOut + row.TransfersIn - row.TransfersOut, row.Closing);
        }
        Assert.Equal(report.Items.Sum(r => r.Closing), report.Closing);
        Assert.Equal(report.Opening + report.MoneyIn - report.MoneyOut + report.TransfersIn - report.TransfersOut, report.Closing);

        // The same accounts with no period at all: opening is only the opening-balance entries.
        var all = Value(await f.Reports.CashBank(null, null, default));
        Assert.Equal(10000m, all.Items.Single(r => r.AccountName == "Main Cash").Opening);
        Assert.Equal(1000m + 800m, all.Items.Single(r => r.AccountName == "Main Cash").MoneyIn);
        foreach (var row in all.Items)
        {
            Assert.Equal(row.Opening + row.MoneyIn - row.MoneyOut + row.TransfersIn - row.TransfersOut, row.Closing);
        }
    }

    [Fact]
    public async Task CashBank_ShowsInactiveAccountsOnlyWhenTheyHoldMoney_AndCountsUnreconciled()
    {
        var f = await SeedAsync();
        var empty = await Account(f, "Old Drawer", FinancialAccountKind.Cash, 0m, D1);
        empty.IsActive = false;
        var funded = await Account(f, "Old Bank", FinancialAccountKind.Bank, 500m, D1);
        funded.IsActive = false;
        await f.Db.SaveChangesAsync();
        var live = await Account(f, "Main Cash", FinancialAccountKind.Cash, 100m, D1);
        await f.Accounts.ReconcileAsync(live.Id, [(await f.Db.FinancialTransactions.FirstAsync(t => t.AccountId == live.Id)).Id], f.UserId);
        await Spend(f, f.Rent, D2, 10m, account: live.Id);

        var report = Value(await f.Reports.CashBank(null, null, default));

        Assert.Equal(["Main Cash", "Old Bank"], report.Items.Select(r => r.AccountName).OrderBy(n => n));
        Assert.Equal(1, report.Items.Single(r => r.AccountName == "Main Cash").UnreconciledCount); // the expense
    }

    // ---------------------------------------------------------------- payment methods

    [Fact]
    public async Task PaymentMethods_TotalReceivedAndPaidByMethod_IgnoringTransfersAndOpeningBalances()
    {
        var f = await SeedAsync();
        var cash = await Account(f, "Main Cash", FinancialAccountKind.Cash, 10000m, D1);
        var bank = await Account(f, "SBI", FinancialAccountKind.Bank, 5000m, D1);
        await Buy(f, f.InrSupplier, D1, 200, 50m, paid: 2000m, method: PaymentMethod.BankTransfer, account: bank.Id);
        await Sell(f, f.Customer, D2, 10, 100m, paid: 1000m, method: PaymentMethod.Cash, account: cash.Id);
        await Sell(f, f.Regular, D2, 5, 100m, paid: 500m, method: PaymentMethod.Upi);
        await Spend(f, f.Rent, D2, 300m, method: PaymentMethod.Cash);
        await f.Accounts.TransferAsync(new TransferInput(bank.Id, cash.Id, 1000m, D3, null), f.UserId);

        var report = Value(await f.Reports.PaymentMethods(null, null, default));

        Assert.Equal([PaymentMethod.Cash, PaymentMethod.Upi, PaymentMethod.BankTransfer], report.Items.Select(i => i.PaymentMethod));
        var c = report.Items[0];
        Assert.Equal((2, 1000m, 300m, 700m), (c.Transactions, c.Received, c.Paid, c.Net));
        Assert.Equal(500m, report.Items[1].Received);
        Assert.Equal(2000m, report.Items[2].Paid);
        Assert.Equal(1500m, report.TotalReceived);
        Assert.Equal(2300m, report.TotalPaid);
        Assert.Equal(-800m, report.TotalNet);

        var later = Value(await f.Reports.PaymentMethods(D3, null, default));
        Assert.Empty(later.Items);
    }
}
