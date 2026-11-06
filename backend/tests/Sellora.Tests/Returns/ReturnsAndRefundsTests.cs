using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Accounts;
using Sellora.Domain.Entities;
using Sellora.Domain.Payments;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Returns;

public class ReturnsAndRefundsTests
{
    private sealed record Fixture(
        SelloraDbContext Db, PurchaseService Purchases, SalesService Sales, FinancialAccountService Accounts,
        Currency Inr, Currency Usd, Supplier InrSupplier, Supplier UsdSupplier, Customer Customer,
        Product Product, ProductUnit Kg, Guid UserId);

    // 5% tax makes per-line rounding matter; USD @ 83.20 covers foreign-currency purchases.
    private static async Task<Fixture> SeedAsync(decimal taxRate = 5m)
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
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST", RatePercent = taxRate };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var usd = new Currency { Id = Guid.NewGuid(), Code = "USD", Name = "US Dollar", Symbol = "$" };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var inrSupplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var usdSupplier = new Supplier { Id = Guid.NewGuid(), Name = "Global Ingredients", CurrencyId = usd.Id };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var product = new Product { Id = Guid.NewGuid(), Sku = "RICE", Name = "Rice", BaseUnitId = unit.Id, BaseUnit = unit, TaxId = tax.Id, Tax = tax, ReorderLevel = 1 };
        var kg = TestProductUnits.Base(product, "kg");
        var rate = new ExchangeRate { Id = Guid.NewGuid(), CurrencyId = usd.Id, RateToBase = 83.20m, EffectiveDate = new DateOnly(2026, 9, 1), CreatedByUserId = user.Id };

        db.AddRange(role, user, unit, tax, inr, usd, settings, inrSupplier, usdSupplier, customer, product, rate);
        await db.SaveChangesAsync();

        var rates = new ExchangeRateService(db);
        return new Fixture(db, new PurchaseService(db, rates), new SalesService(db), new FinancialAccountService(db),
            inr, usd, inrSupplier, usdSupplier, customer, product, kg, user.Id);
    }

    private static readonly DateOnly D = new(2026, 9, 10);

    private static Task<PurchaseOrder> Buy(Fixture f, Supplier s, decimal qty, decimal rate, decimal paid = 0m, Guid? account = null) =>
        f.Purchases.CreatePurchaseAsync(new CreatePurchaseInput(s.Id, D, null, null, PaymentMethod.Cash, paid,
            [new PurchaseLineInput(f.Product.Id, f.Kg.Id, qty, rate, 0, 0)], account), f.UserId);

    private static Task<SalesInvoice> Sell(Fixture f, decimal qty, decimal price, decimal paid = 0m, Guid? account = null) =>
        f.Sales.CreateSaleAsync(new CreateSaleInput(f.Customer.Id, D, PaymentMethod.Cash, paid,
            [new SaleLineInput(f.Product.Id, f.Kg.Id, qty, price, 0)], account), f.UserId);

    private static Task<SalesReturn> ReturnSale(Fixture f, SalesInvoice sale, decimal qty, RefundDetails? refund = null) =>
        f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, "damaged",
            [new SaleReturnLineInput(sale.Lines.Single().Id, qty)], refund), f.UserId);

    private static Task<PurchaseReturn> ReturnPurchase(Fixture f, PurchaseOrder p, decimal qty, RefundDetails? refund = null) =>
        f.Purchases.CreateReturnAsync(new CreatePurchaseReturnInput(p.Id, D, "wrong grade",
            [new PurchaseReturnLineInput(p.Lines.Single().Id, null, qty)], refund), f.UserId);

    private static async Task<decimal> Balance(Fixture f, Guid accountId) =>
        (await f.Db.FinancialTransactions.AsNoTracking().Where(t => t.AccountId == accountId && t.Status == FinancialTransactionStatus.Active).ToListAsync())
        .Sum(t => t.Direction == MoneyDirection.In ? t.BaseAmount : -t.BaseAmount);

    private static async Task<T> Fails<T>(Func<Task<T>> act, string field)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(act);
        Assert.Equal(field, ex.Field);
        return default!;
    }

    // ---------------------------------------------------------------- sales returns

    [Fact]
    public async Task SalesReturn_PutsGoodsBackInTheBatch_CreditsTheInvoice_AndLogsTheMovement()
    {
        var f = await SeedAsync();
        await Buy(f, f.InrSupplier, 100, 50m);
        var sale = await Sell(f, 10, 100m, paid: 1050m); // 1000 + 5% = 1050, fully paid

        var ret = await ReturnSale(f, sale, 4); // 4 × 100 + 5% = 420

        Assert.Equal("SRT-0001", ret.ReturnNo);
        Assert.Equal(400m, ret.TaxableTotal);
        Assert.Equal(20m, ret.TaxTotal);
        Assert.Equal(420m, ret.Total);
        Assert.Equal(200m, ret.Lines.Single().CostRestored); // 4 kg × ₹50 landed cost

        var invoice = await f.Db.SalesInvoices.AsNoTracking().SingleAsync();
        Assert.Equal(420m, invoice.ReturnedAmount);
        Assert.Equal(630m, invoice.NetTotal);
        Assert.Equal(420m, invoice.RefundableAmount); // paid 1050, owes 630
        Assert.Equal(0m, invoice.DueAmount);
        Assert.Equal(PaymentStatus.Paid, invoice.PaymentStatus);

        Assert.Equal(94m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand); // 100 - 10 + 4
        Assert.Equal(94m, (await f.Db.PurchaseBatches.SingleAsync()).RemainingBaseQty);
        var movement = await f.Db.StockMovements.SingleAsync(m => m.Type == StockMovementType.SaleReturn);
        Assert.Equal(4m, movement.QuantityBaseUnits);
        Assert.Equal(ret.ReturnNo, movement.ReferenceNo);
    }

    [Fact]
    public async Task SalesReturn_OfEverythingRemaining_CreditsTheInvoiceExactly_DespiteRounding()
    {
        var f = await SeedAsync(taxRate: 18m);
        await Buy(f, f.InrSupplier, 100, 50m);
        var sale = await Sell(f, 3, 33.33m); // awkward figures: 99.99 + 18% tax

        await ReturnSale(f, sale, 1);
        await ReturnSale(f, sale, 1);
        await ReturnSale(f, sale, 1);

        var invoice = await f.Db.SalesInvoices.AsNoTracking().SingleAsync();
        Assert.Equal(invoice.GrandTotal, invoice.ReturnedAmount); // not a paisa off
        Assert.Equal(0m, invoice.NetTotal);
        Assert.Equal(0m, invoice.DueAmount);
        Assert.Equal(100m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task SalesReturn_RestoresTheExactBatchesSoldFrom_OldestFirst()
    {
        var f = await SeedAsync();
        var first = await Buy(f, f.InrSupplier, 6, 50m);   // batch A: 6 kg @ 50
        var second = await Buy(f, f.InrSupplier, 6, 80m);  // batch B: 6 kg @ 80
        var sale = await Sell(f, 9, 200m);                 // FIFO: 6 from A, 3 from B

        var ret = await ReturnSale(f, sale, 7);            // back: 6 to A, 1 to B (oldest first)

        var batches = await f.Db.PurchaseBatches.AsNoTracking().OrderBy(b => b.CreatedAt).ToListAsync();
        Assert.Equal(6m, batches[0].RemainingBaseQty); // A: 0 + 6
        Assert.Equal(4m, batches[1].RemainingBaseQty); // B: 3 + 1
        Assert.Equal(6 * 50m + 1 * 80m, ret.Lines.Single().CostRestored);
        Assert.Equal([6m, 1m], (await f.Db.SalesInvoiceLineBatchConsumptions.AsNoTracking().OrderBy(c => c.PurchaseBatch.CreatedAt).ToListAsync()).Select(c => c.ReturnedQtyBaseUnits));
    }

    [Fact]
    public async Task SalesReturn_InvalidRequests_AreRejected_AndChangeNothing()
    {
        var f = await SeedAsync();
        await Buy(f, f.InrSupplier, 100, 50m);
        var sale = await Sell(f, 10, 100m);
        await ReturnSale(f, sale, 8);
        var lineId = sale.Lines.Single().Id;
        var stockBefore = (await f.Db.StockItems.SingleAsync()).QuantityOnHand;

        await Fails(() => ReturnSale(f, sale, 3), "Lines");   // only 2 left to return
        await Fails(() => ReturnSale(f, sale, 0), "Lines");
        await Fails(() => ReturnSale(f, sale, -1), "Lines");
        await Fails(() => f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null, []), f.UserId), "Lines");
        await Fails(() => f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null,
            [new SaleReturnLineInput(lineId, 1), new SaleReturnLineInput(lineId, 1)]), f.UserId), "Lines");
        await Fails(() => f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D, null,
            [new SaleReturnLineInput(Guid.NewGuid(), 1)]), f.UserId), "Lines");
        await Fails(() => f.Sales.CreateReturnAsync(new CreateSaleReturnInput(Guid.NewGuid(), D, null, [new SaleReturnLineInput(lineId, 1)]), f.UserId), "SalesInvoiceId");
        await Fails(() => f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, D.AddDays(-5), null, [new SaleReturnLineInput(lineId, 1)]), f.UserId), "ReturnDate");
        await Fails(() => f.Sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)), null, [new SaleReturnLineInput(lineId, 1)]), f.UserId), "ReturnDate");

        Assert.Single(await f.Db.SalesReturns.ToListAsync());
        Assert.Equal(stockBefore, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task SalesReturn_OnACancelledSale_IsRejected_AndCancellingAfterAReturnIsBlocked()
    {
        var f = await SeedAsync();
        await Buy(f, f.InrSupplier, 100, 50m);
        var cancelled = await Sell(f, 5, 100m);
        await f.Sales.CancelSaleAsync(cancelled.Id, f.UserId);
        await Fails(() => ReturnSale(f, cancelled, 1), "Status");

        var returned = await Sell(f, 5, 100m);
        await ReturnSale(f, returned, 1);
        await Fails(async () => { await f.Sales.CancelSaleAsync(returned.Id, f.UserId); return 0; }, "Status");
    }

    [Fact]
    public async Task SalesReturn_WithARefund_PaysMoneyBackFromTheChosenAccount_LinkedToTheReturn()
    {
        var f = await SeedAsync();
        var cash = await f.Accounts.CreateAsync(new CreateAccountInput("Main Cash", FinancialAccountKind.Cash, null, 0m, D), f.UserId);
        await Buy(f, f.InrSupplier, 100, 50m);
        var sale = await Sell(f, 10, 100m, paid: 1050m, account: cash.Id); // +1050 into cash

        var ret = await ReturnSale(f, sale, 4, new RefundDetails(420m, PaymentMethod.Cash, cash.Id, "returned goods"));

        var refund = await f.Db.SalesPayments.AsNoTracking().SingleAsync(p => p.Kind == PaymentEntryKind.Refund);
        Assert.Equal(420m, refund.Amount);
        Assert.Equal(ret.Id, refund.SalesReturnId);
        Assert.Equal("returned goods", refund.Reason);
        var txn = await f.Db.FinancialTransactions.AsNoTracking().SingleAsync(t => t.SalesPaymentId == refund.Id);
        Assert.Equal(FinancialTransactionType.CustomerRefund, txn.Type);
        Assert.Equal(MoneyDirection.Out, txn.Direction);
        Assert.Equal(cash.Id, txn.AccountId);
        Assert.Equal(1050m - 420m, await Balance(f, cash.Id));

        var invoice = await f.Db.SalesInvoices.AsNoTracking().SingleAsync();
        Assert.Equal(630m, invoice.PaidAmount); // held from the customer, net of the refund
        Assert.Equal(0m, invoice.RefundableAmount);
        Assert.Equal(0m, invoice.DueAmount);
        Assert.Equal(PaymentStatus.Paid, invoice.PaymentStatus);
        // The original payment is untouched.
        Assert.Equal(1050m, (await f.Db.SalesPayments.AsNoTracking().SingleAsync(p => p.Kind == PaymentEntryKind.Payment)).Amount);
    }

    [Fact]
    public async Task SalesReturn_RefundAboveWhatIsRefundable_RollsTheWholeReturnBack()
    {
        var f = await SeedAsync();
        await Buy(f, f.InrSupplier, 100, 50m);
        var sale = await Sell(f, 10, 100m, paid: 500m); // owes 1050 - paid 500

        // Returning 4 (420) leaves net 630 vs paid 500 → nothing is refundable yet.
        await Fails(() => ReturnSale(f, sale, 4, new RefundDetails(1m, PaymentMethod.Cash)), "Amount");
        Assert.Empty(await f.Db.SalesReturns.AsNoTracking().ToListAsync());
        Assert.Equal(90m, (await f.Db.StockItems.AsNoTracking().SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task SalesRefund_OfACancelledSalesPayment_Works_ButNeverAboveWhatIsHeld()
    {
        var f = await SeedAsync();
        await Buy(f, f.InrSupplier, 100, 50m);
        var sale = await Sell(f, 10, 100m, paid: 1050m);
        await f.Sales.CancelSaleAsync(sale.Id, f.UserId);

        var cancelled = await f.Db.SalesInvoices.AsNoTracking().SingleAsync();
        Assert.Equal(1050m, cancelled.RefundableAmount);
        Assert.Equal(0m, cancelled.DueAmount);

        await Fails(() => f.Sales.AddRefundAsync(sale.Id, new AddSaleRefundInput(1050.01m, PaymentMethod.Cash, 1050m), f.UserId), "Amount");
        await Fails(() => f.Sales.AddRefundAsync(sale.Id, new AddSaleRefundInput(0m, PaymentMethod.Cash, 1050m), f.UserId), "Amount");
        await Fails(() => f.Sales.AddRefundAsync(sale.Id, new AddSaleRefundInput(100m, PaymentMethod.Cash, 999m), f.UserId), "ExpectedRefundable");

        var after = await f.Sales.AddRefundAsync(sale.Id, new AddSaleRefundInput(300m, PaymentMethod.Upi, 1050m), f.UserId);
        Assert.Equal(750m, after!.PaidAmount);
        Assert.Equal(750m, after.RefundableAmount);

        var last = await f.Sales.AddRefundAsync(sale.Id, new AddSaleRefundInput(750m, PaymentMethod.Upi, 750m), f.UserId);
        Assert.Equal(0m, last!.PaidAmount);
        await Fails(() => f.Sales.AddRefundAsync(sale.Id, new AddSaleRefundInput(1m, PaymentMethod.Upi, 0m), f.UserId), "Amount");
        Assert.Null(await f.Sales.AddRefundAsync(Guid.NewGuid(), new AddSaleRefundInput(1m, PaymentMethod.Cash, 1m), f.UserId));
    }

    [Fact]
    public async Task SalesRefund_OnALiveUnreturnedSale_IsRejected_BecauseTheCustomerStillOwesForTheGoods()
    {
        var f = await SeedAsync();
        await Buy(f, f.InrSupplier, 100, 50m);
        var sale = await Sell(f, 10, 100m, paid: 1050m);

        await Fails(() => f.Sales.AddRefundAsync(sale.Id, new AddSaleRefundInput(10m, PaymentMethod.Cash, 0m), f.UserId), "Amount");
    }

    // ---------------------------------------------------------------- payment reversals

    [Fact]
    public async Task SalesPaymentReversal_UndoesTheExactPayment_ThroughTheSameAccount_AndOnlyOnce()
    {
        var f = await SeedAsync();
        var cash = await f.Accounts.CreateAsync(new CreateAccountInput("Main Cash", FinancialAccountKind.Cash, null, 0m, D), f.UserId);
        await Buy(f, f.InrSupplier, 100, 50m);
        var sale = await Sell(f, 10, 100m, paid: 300m, account: cash.Id);
        await f.Sales.AddPaymentAsync(sale.Id, new AddSalePaymentInput(200m, PaymentMethod.Upi, 750m), f.UserId);
        var wrong = await f.Db.SalesPayments.AsNoTracking().SingleAsync(p => p.Amount == 300m);

        var after = await f.Sales.ReversePaymentAsync(sale.Id, wrong.Id, new ReversePaymentInput("entered against the wrong sale"), f.UserId);

        Assert.Equal(200m, after!.PaidAmount);
        Assert.Equal(850m, after.DueAmount);
        Assert.Equal(PaymentStatus.PartiallyPaid, after.PaymentStatus);
        var reversal = await f.Db.SalesPayments.AsNoTracking().SingleAsync(p => p.Kind == PaymentEntryKind.Reversal);
        Assert.Equal(wrong.Id, reversal.ReversesPaymentId);
        Assert.Equal(300m, reversal.Amount);
        Assert.Equal(wrong.PaymentMethod, reversal.PaymentMethod);
        Assert.Equal("entered against the wrong sale", reversal.Reason);
        Assert.Equal(3, await f.Db.SalesPayments.CountAsync()); // original kept, reversal appended
        var txn = await f.Db.FinancialTransactions.AsNoTracking().SingleAsync(t => t.SalesPaymentId == reversal.Id);
        Assert.Equal(FinancialTransactionType.PaymentReversal, txn.Type);
        Assert.Equal(MoneyDirection.Out, txn.Direction);
        Assert.Equal(cash.Id, txn.AccountId);
        Assert.Equal(0m, await Balance(f, cash.Id)); // +300 then -300

        await Fails(() => f.Sales.ReversePaymentAsync(sale.Id, wrong.Id, new ReversePaymentInput(null), f.UserId), "PaymentId"); // once only
        await Fails(() => f.Sales.ReversePaymentAsync(sale.Id, reversal.Id, new ReversePaymentInput(null), f.UserId), "PaymentId"); // a reversal is not a payment
        await Fails(() => f.Sales.ReversePaymentAsync(sale.Id, Guid.NewGuid(), new ReversePaymentInput(null), f.UserId), "PaymentId");
        Assert.Null(await f.Sales.ReversePaymentAsync(Guid.NewGuid(), wrong.Id, new ReversePaymentInput(null), f.UserId));
    }

    [Fact]
    public async Task SalesPaymentReversal_IsBlockedWhenPartOfItHasBeenRefundedSince()
    {
        var f = await SeedAsync();
        await Buy(f, f.InrSupplier, 100, 50m);
        var sale = await Sell(f, 10, 100m, paid: 1050m);
        await ReturnSale(f, sale, 5, new RefundDetails(525m, PaymentMethod.Cash)); // paid is now 525
        var original = await f.Db.SalesPayments.AsNoTracking().SingleAsync(p => p.Kind == PaymentEntryKind.Payment);

        await Fails(() => f.Sales.ReversePaymentAsync(sale.Id, original.Id, new ReversePaymentInput(null), f.UserId), "PaymentId");
    }

    // ---------------------------------------------------------------- purchase returns

    [Fact]
    public async Task PurchaseReturn_TakesStockOutOfTheBatch_ReducesWhatIsOwed_AndLogsTheMovement()
    {
        var f = await SeedAsync();
        var purchase = await Buy(f, f.InrSupplier, 100, 50m, paid: 2000m); // 5000 + 5% = 5250

        var ret = await ReturnPurchase(f, purchase, 20); // 20 × 50 + 5% = 1050

        Assert.Equal("PRT-0001", ret.ReturnNo);
        Assert.Equal(1000m, ret.TaxableTotal);
        Assert.Equal(1050m, ret.Total);
        Assert.Equal(1050m, ret.OriginalTotal);
        Assert.Equal(1000m, ret.Lines.Single().CostRemoved);

        var p = await f.Db.PurchaseOrders.AsNoTracking().SingleAsync();
        Assert.Equal(1050m, p.ReturnedAmount);
        Assert.Equal(4200m, p.NetTotal);
        Assert.Equal(2200m, p.OriginalDueAmount); // 4200 - 2000 paid
        Assert.Equal(2200m, p.DueAmount);
        Assert.Equal(PaymentStatus.PartiallyPaid, p.PaymentStatus);

        Assert.Equal(80m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        Assert.Equal(50m, (await f.Db.StockItems.SingleAsync()).AverageCost); // unchanged: goods left at their own cost
        Assert.Equal(80m, (await f.Db.PurchaseBatches.SingleAsync()).RemainingBaseQty);
        var movement = await f.Db.StockMovements.SingleAsync(m => m.Type == StockMovementType.PurchaseReturn);
        Assert.Equal(-20m, movement.QuantityBaseUnits);
    }

    [Fact]
    public async Task PurchaseReturn_CannotReturnGoodsAlreadySold()
    {
        var f = await SeedAsync();
        var purchase = await Buy(f, f.InrSupplier, 100, 50m);
        await Sell(f, 90, 100m); // only 10 kg left in the batch

        await Fails(() => ReturnPurchase(f, purchase, 11), "Lines");
        await ReturnPurchase(f, purchase, 10);

        Assert.Equal(0m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
        Assert.Equal(0m, (await f.Db.PurchaseBatches.SingleAsync()).RemainingBaseQty);
    }

    [Fact]
    public async Task PurchaseReturn_InvalidRequests_AreRejected_AndChangeNothing()
    {
        var f = await SeedAsync();
        var purchase = await Buy(f, f.InrSupplier, 100, 50m);
        await ReturnPurchase(f, purchase, 90);
        var lineId = purchase.Lines.Single().Id;

        await Fails(() => ReturnPurchase(f, purchase, 11), "Lines");
        await Fails(() => ReturnPurchase(f, purchase, 0), "Lines");
        await Fails(() => f.Purchases.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, null, []), f.UserId), "Lines");
        await Fails(() => f.Purchases.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D, null,
            [new PurchaseReturnLineInput(lineId, null, 1), new PurchaseReturnLineInput(lineId, null, 1)]), f.UserId), "Lines");
        await Fails(() => f.Purchases.CreateReturnAsync(new CreatePurchaseReturnInput(Guid.NewGuid(), D, null, [new PurchaseReturnLineInput(lineId, null, 1)]), f.UserId), "PurchaseOrderId");
        await Fails(() => f.Purchases.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, D.AddDays(-5), null, [new PurchaseReturnLineInput(lineId, null, 1)]), f.UserId), "ReturnDate");

        Assert.Single(await f.Db.PurchaseReturns.ToListAsync());
        Assert.Equal(10m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task PurchaseReturn_OfEverything_CreditsExactlyTheGrandTotal_InBothCurrencies()
    {
        var f = await SeedAsync(taxRate: 18m);
        var purchase = await Buy(f, f.UsdSupplier, 3, 33.33m); // USD figures with awkward rounding

        await ReturnPurchase(f, purchase, 1);
        await ReturnPurchase(f, purchase, 1);
        await ReturnPurchase(f, purchase, 1);

        var p = await f.Db.PurchaseOrders.AsNoTracking().SingleAsync();
        Assert.Equal(p.GrandTotal, p.ReturnedAmount);
        Assert.Equal(p.OriginalGrandTotal, p.OriginalReturnedAmount);
        Assert.Equal(0m, p.OriginalDueAmount);
        Assert.Equal(0m, p.DueAmount);
        Assert.Equal(0m, (await f.Db.StockItems.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task PurchaseReturn_OnACancelledPurchase_IsRejected_AndCancellingAfterAReturnIsBlocked()
    {
        var f = await SeedAsync();
        var cancelled = await Buy(f, f.InrSupplier, 10, 50m);
        await f.Purchases.CancelPurchaseAsync(cancelled.Id, f.UserId);
        await Fails(() => ReturnPurchase(f, cancelled, 1), "Status");

        var returned = await Buy(f, f.InrSupplier, 10, 50m);
        await ReturnPurchase(f, returned, 1);
        await Fails(async () => { await f.Purchases.CancelPurchaseAsync(returned.Id, f.UserId); return 0; }, "Status");
    }

    [Fact]
    public async Task PurchaseReturn_ThenSettling_PaysOnlyTheNetAmount_ExactlyInForeignCurrency()
    {
        var f = await SeedAsync(taxRate: 0m);
        var purchase = await Buy(f, f.UsdSupplier, 10, 100m, paid: 400m); // USD 1000 = ₹83,200; paid USD 400
        await ReturnPurchase(f, purchase, 3);                             // credit USD 300 = ₹24,960

        var before = await f.Db.PurchaseOrders.AsNoTracking().SingleAsync();
        Assert.Equal(300m, before.OriginalReturnedAmount);
        Assert.Equal(300m, before.OriginalDueAmount); // 700 - 400

        await Fails(() => f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(300.01m, PaymentMethod.Cash, 300m), f.UserId), "Amount");
        var settled = await f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(300m, PaymentMethod.Cash, 300m), f.UserId);

        Assert.Equal(700m, settled!.OriginalPaidAmount);
        Assert.Equal(settled.NetTotal, settled.PaidAmount); // exactly the base net total, no stray paisa
        Assert.Equal(0m, settled.DueAmount);
        Assert.Equal(PaymentStatus.Paid, settled.PaymentStatus);
    }

    // ---------------------------------------------------------------- supplier refunds and purchase reversals

    [Fact]
    public async Task SupplierRefund_AfterAReturn_BringsMoneyIn_AndTheLedgerNetsToThePaidTotal()
    {
        var f = await SeedAsync(taxRate: 0m);
        var bank = await f.Accounts.CreateAsync(new CreateAccountInput("SBI", FinancialAccountKind.Bank, null, 100000m, D), f.UserId);
        var purchase = await Buy(f, f.UsdSupplier, 10, 100m, paid: 1000m, account: bank.Id); // fully paid: USD 1000 = ₹83,200
        await ReturnPurchase(f, purchase, 4); // credit USD 400 → supplier holds USD 400 too much

        var p = await f.Db.PurchaseOrders.AsNoTracking().SingleAsync();
        Assert.Equal(400m, p.OriginalRefundableAmount);

        await Fails(() => f.Purchases.AddRefundAsync(purchase.Id, new AddPurchaseRefundInput(400.01m, PaymentMethod.BankTransfer, 400m, bank.Id), f.UserId), "Amount");
        await Fails(() => f.Purchases.AddRefundAsync(purchase.Id, new AddPurchaseRefundInput(100m, PaymentMethod.BankTransfer, 350m, bank.Id), f.UserId), "ExpectedRefundable");

        await f.Purchases.AddRefundAsync(purchase.Id, new AddPurchaseRefundInput(150m, PaymentMethod.BankTransfer, 400m, bank.Id, "credit note settled"), f.UserId);
        var after = await f.Purchases.AddRefundAsync(purchase.Id, new AddPurchaseRefundInput(250m, PaymentMethod.BankTransfer, 250m, bank.Id), f.UserId);

        Assert.Equal(600m, after!.OriginalPaidAmount); // 1000 - 150 - 250
        Assert.Equal(after.NetTotal, after.PaidAmount); // net is ₹49,920 = USD 600
        Assert.Equal(0m, after.OriginalRefundableAmount);
        Assert.Equal(0m, after.DueAmount);
        Assert.Equal(PaymentStatus.Paid, after.PaymentStatus);

        var rows = await f.Db.PurchasePayments.AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync();
        Assert.Equal([PaymentEntryKind.Payment, PaymentEntryKind.Refund, PaymentEntryKind.Refund], rows.Select(r => r.Kind));
        Assert.Equal(after.PaidAmount, rows.Sum(r => r.Kind == PaymentEntryKind.Payment ? r.BaseAmount : -r.BaseAmount)); // the ledger nets to the total
        Assert.Equal(83.20m, rows[1].ExchangeRateToBase);

        var refundTxns = await f.Db.FinancialTransactions.AsNoTracking().Where(t => t.Type == FinancialTransactionType.SupplierRefund).ToListAsync();
        Assert.Equal(2, refundTxns.Count);
        Assert.All(refundTxns, t => { Assert.Equal(MoneyDirection.In, t.Direction); Assert.Equal(bank.Id, t.AccountId); });
        Assert.Equal(100000m - 83200m + refundTxns.Sum(t => t.BaseAmount), await Balance(f, bank.Id));
    }

    [Fact]
    public async Task SupplierRefund_OfACancelledPurchase_RefundsEverythingPaid()
    {
        var f = await SeedAsync(taxRate: 0m);
        var purchase = await Buy(f, f.InrSupplier, 10, 50m, paid: 500m);
        await f.Purchases.CancelPurchaseAsync(purchase.Id, f.UserId);

        var cancelled = await f.Db.PurchaseOrders.AsNoTracking().SingleAsync();
        Assert.Equal(500m, cancelled.OriginalRefundableAmount);
        Assert.Equal(0m, cancelled.DueAmount);

        var after = await f.Purchases.AddRefundAsync(purchase.Id, new AddPurchaseRefundInput(500m, PaymentMethod.Cash, 500m), f.UserId);
        Assert.Equal(0m, after!.PaidAmount);
        Assert.Equal(0m, after.OriginalPaidAmount);
        Assert.Null(await f.Purchases.AddRefundAsync(Guid.NewGuid(), new AddPurchaseRefundInput(1m, PaymentMethod.Cash, 1m), f.UserId));
    }

    [Fact]
    public async Task PurchaseReturn_WithARefund_RecordsItInTheSameStep_LinkedToTheReturn()
    {
        var f = await SeedAsync(taxRate: 0m);
        var purchase = await Buy(f, f.InrSupplier, 100, 50m, paid: 5000m);

        var ret = await ReturnPurchase(f, purchase, 20, new RefundDetails(1000m, PaymentMethod.Cash));

        var refund = await f.Db.PurchasePayments.AsNoTracking().SingleAsync(p => p.Kind == PaymentEntryKind.Refund);
        Assert.Equal(ret.Id, refund.PurchaseReturnId);
        Assert.Equal(1000m, refund.Amount);
        Assert.Equal(1000m, refund.BaseAmount);
        var p = await f.Db.PurchaseOrders.AsNoTracking().SingleAsync();
        Assert.Equal(4000m, p.PaidAmount);
        Assert.Equal(0m, p.OriginalRefundableAmount);
        Assert.Equal(PaymentStatus.Paid, p.PaymentStatus);

        // A refund larger than what is refundable rolls the whole return back.
        await Fails(() => ReturnPurchase(f, purchase, 10, new RefundDetails(9999m, PaymentMethod.Cash)), "Amount");
        Assert.Single(await f.Db.PurchaseReturns.AsNoTracking().ToListAsync());
        Assert.Equal(80m, (await f.Db.StockItems.AsNoTracking().SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public async Task PurchasePaymentReversal_CopiesTheOriginalAmountAndRate_AndOnlyOnce()
    {
        var f = await SeedAsync(taxRate: 0m);
        var bank = await f.Accounts.CreateAsync(new CreateAccountInput("SBI", FinancialAccountKind.Bank, null, 100000m, D), f.UserId);
        var purchase = await Buy(f, f.UsdSupplier, 10, 100m, paid: 400m, account: bank.Id);
        await f.Purchases.AddPaymentAsync(purchase.Id, new AddPurchasePaymentInput(250m, PaymentMethod.Upi, 600m), f.UserId);
        // The rate master moves; the reversal must still use what the payment was booked at.
        (await f.Db.ExchangeRates.SingleAsync()).RateToBase = 99m;
        await f.Db.SaveChangesAsync();
        var wrong = await f.Db.PurchasePayments.AsNoTracking().SingleAsync(p => p.Amount == 400m);

        var after = await f.Purchases.ReversePaymentAsync(purchase.Id, wrong.Id, new ReversePaymentInput("wrong purchase"), f.UserId);

        Assert.Equal(250m, after!.OriginalPaidAmount);
        Assert.Equal(20800m, after.PaidAmount); // 250 × 83.20
        Assert.Equal(750m, after.OriginalDueAmount);
        var reversal = await f.Db.PurchasePayments.AsNoTracking().SingleAsync(p => p.Kind == PaymentEntryKind.Reversal);
        Assert.Equal(400m, reversal.Amount);
        Assert.Equal(83.20m, reversal.ExchangeRateToBase);
        Assert.Equal(wrong.BaseAmount, reversal.BaseAmount);
        var txn = await f.Db.FinancialTransactions.AsNoTracking().SingleAsync(t => t.PurchasePaymentId == reversal.Id);
        Assert.Equal(FinancialTransactionType.PaymentReversal, txn.Type);
        Assert.Equal(MoneyDirection.In, txn.Direction);
        Assert.Equal(bank.Id, txn.AccountId);
        Assert.Equal(100000m - 33280m + 33280m, await Balance(f, bank.Id)); // the ₹33,280 that left is back

        await Fails(() => f.Purchases.ReversePaymentAsync(purchase.Id, wrong.Id, new ReversePaymentInput(null), f.UserId), "PaymentId");
        Assert.Null(await f.Purchases.ReversePaymentAsync(Guid.NewGuid(), wrong.Id, new ReversePaymentInput(null), f.UserId));
    }

    // ---------------------------------------------------------------- reports

    [Fact]
    public async Task EveryRefundOrReversal_WritesExactlyOneTransaction_AndARejectedOneWritesNone()
    {
        var f = await SeedAsync(taxRate: 0m);
        await Buy(f, f.InrSupplier, 100, 50m);
        var sale = await Sell(f, 10, 100m, paid: 1000m);
        var before = await f.Db.FinancialTransactions.CountAsync();

        await Fails(() => f.Sales.AddRefundAsync(sale.Id, new AddSaleRefundInput(5m, PaymentMethod.Cash, 0m), f.UserId), "Amount");
        Assert.Equal(before, await f.Db.FinancialTransactions.CountAsync());

        await ReturnSale(f, sale, 2, new RefundDetails(200m, PaymentMethod.Cash));
        Assert.Equal(before + 1, await f.Db.FinancialTransactions.CountAsync());
        var payments = await f.Db.SalesPayments.CountAsync();
        Assert.Equal(await f.Db.FinancialTransactions.CountAsync(t => t.SalesPaymentId != null), payments);
    }
}
