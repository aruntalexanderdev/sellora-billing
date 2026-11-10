using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Accounts;
using Sellora.Domain.Entities;
using Sellora.Domain.Expenses;
using Sellora.Domain.Payments;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Branches;

/// <summary>
/// T4/T5 (InMemory) — documents are stamped with the working branch (requested, else Main), numbered per branch in the agreed shapes,
/// EXP stays company-level, an inactive/inaccessible branch takes no new documents but existing ones still take payments, returns follow
/// the original's branch, and branch-tagged accounts only serve their own branch.
/// </summary>
public class BranchDocumentTests
{
    private static async Task<string> FieldAsync(Func<Task> action) => (await Assert.ThrowsAsync<DomainValidationException>(action)).Field;

    // ---- stamping & numbering ------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreatePurchase_NoRequestedBranch_GoesToMainAndKeepsTheLegacyNumberShape()
    {
        var w = await BranchWorld.CreateAsync();

        var purchase = await w.PurchaseAsync(null);

        Assert.Equal(w.Main, purchase.BranchId);
        Assert.Equal("PUR-0002", purchase.PurchaseNo); // PUR-0001 is the seeding purchase
    }

    [Fact]
    public async Task CreateDocuments_AtAnotherBranch_AreStampedWithItAndNumberedPerBranchIndependentlyOfMain()
    {
        var w = await BranchWorld.CreateAsync();

        var first = await w.PurchaseAsync(w.Blr);
        var second = await w.PurchaseAsync(w.Blr);
        var main = await w.PurchaseAsync(w.Main);

        Assert.Equal((w.Blr, "PUR-BLR-0001"), (first.BranchId, first.PurchaseNo));
        Assert.Equal("PUR-BLR-0002", second.PurchaseNo);
        Assert.Equal((w.Main, "PUR-0002"), (main.BranchId, main.PurchaseNo));
    }

    [Fact]
    public async Task CreateSale_InvoiceNumbers_UseCompanyPrefixAtMain_CompanyPrefixPlusCodeAtABranch_AndOwnPrefixWhenSet()
    {
        var w = await BranchWorld.CreateAsync();

        var main = await w.SaleAsync(null);
        var blr = await w.SaleAsync(w.Blr);
        await using (var db = w.Db())
        {
            await new BranchService(db).UpdateAsync(w.Blr, new Sellora.Domain.Companies.UpdateBranchInput("Bengaluru", null, null, null, null, null, null, "KAR"), w.UserId);
        }
        var own = await w.SaleAsync(w.Blr);

        Assert.Equal("INV-0001", main.InvoiceNo);
        Assert.Equal("INV-BLR-0001", blr.InvoiceNo);
        Assert.Equal("KAR-0002", own.InvoiceNo); // the counter belongs to (branch, series): a prefix change keeps counting, so numbers never repeat
        Assert.Equal(new[] { w.Main, w.Blr, w.Blr }, new[] { main.BranchId, blr.BranchId, own.BranchId });
    }

    [Fact]
    public async Task CreateAdjustment_AtABranch_IsNumberedAdjBranchAndStamped()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db(w.Blr);

        var adjustment = await new StockAdjustmentService(db).CreateAdjustmentAsync(
            new CreateAdjustmentInput(w.ProductId, w.ProductUnitId, AdjustmentType.Correction, AdjustmentDirection.Increase, 1m, 5m, "x"), w.UserId);

        Assert.Equal((w.Blr, "ADJ-BLR-0001"), (adjustment.BranchId, adjustment.AdjustmentNo));
    }

    [Fact]
    public async Task CreateExpense_IsCompanyLevelNumberedExpRegardlessOfTheBranchTag()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db(w.Blr); // the header's branch does not tag expenses
        var service = new ExpenseService(db, new ExchangeRateService(db));

        var plain = await service.CreateAsync(new CreateExpenseInput(w.CategoryId, BranchWorld.Day, w.CurrencyId, 10m, PaymentMethod.Cash, null, null), w.UserId);
        var tagged = await service.CreateAsync(new CreateExpenseInput(w.CategoryId, BranchWorld.Day, w.CurrencyId, 10m, PaymentMethod.Cash, null, null) { BranchId = w.Blr }, w.UserId);

        Assert.Null(plain.BranchId);
        Assert.Equal(w.Blr, tagged.BranchId);
        Assert.Equal(["EXP-0001", "EXP-0002"], new[] { plain.ExpenseNo, tagged.ExpenseNo });
    }

    // ---- inactive / inaccessible branches -----------------------------------------------------------------------------------

    [Fact]
    public async Task CreateDocument_InactiveInUnknownOrInaccessibleBranch_IsRefusedOnBranchId()
    {
        var w = await BranchWorld.CreateAsync();
        await using (var db = w.Db()) await new BranchService(db).SetStatusAsync(w.Blr, false, w.UserId);

        Assert.Equal("branchId", await FieldAsync(() => w.PurchaseAsync(w.Blr)));
        Assert.Equal("branchId", await FieldAsync(() => w.SaleAsync(w.Blr)));
        Assert.Equal("branchId", await FieldAsync(() => w.PurchaseAsync(Guid.NewGuid())));
        // Active but outside the caller's allowed set.
        await using (var db = w.Db()) await new BranchService(db).SetStatusAsync(w.Blr, true, w.UserId);
        var restricted = w.Db(w.Blr, new HashSet<Guid> { w.Main }, unrestricted: false);
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new PurchaseService(restricted, new ExchangeRateService(restricted)).CreatePurchaseAsync(
            new CreatePurchaseInput(w.SupplierId, BranchWorld.Day, null, null, PaymentMethod.Cash, 0, [new PurchaseLineInput(w.ProductId, w.ProductUnitId, 1m, 1m, 0, 0)]), w.UserId));
        Assert.Equal("branchId", ex.Field);
        Assert.Equal(0, await w.Db().PurchaseOrders.CountAsync(p => p.BranchId == w.Blr));
    }

    [Fact]
    public async Task AddPayment_OnAnInvoiceOfAnInactiveBranch_StillWorks()
    {
        var w = await BranchWorld.CreateAsync();
        var sale = await w.SaleAsync(w.Blr);
        await using (var db = w.Db()) await new BranchService(db).SetStatusAsync(w.Blr, false, w.UserId);
        await using var pay = w.Db();

        var updated = await ListPricedSales.For(pay).AddPaymentAsync(sale.Id, new AddSalePaymentInput(sale.GrandTotal, PaymentMethod.Cash, sale.GrandTotal), w.UserId);

        Assert.Equal(sale.GrandTotal, updated!.PaidAmount);
        Assert.Equal(SalesStatus.Completed, updated.Status);
    }

    [Fact]
    public async Task AddPayment_OnAnInvoiceOfABranchTheCallerCannotAccess_IsNotFound()
    {
        var w = await BranchWorld.CreateAsync();
        var sale = await w.SaleAsync(w.Blr);
        await using var restricted = w.Db(null, new HashSet<Guid> { w.Main }, unrestricted: false);

        // Phase 5 CHANGED ASSERTION: the Branch filter hides the invoice, so the payment target is "not found" (null -> 404), not a branchId 400.
        Assert.Null(await ListPricedSales.For(restricted).AddPaymentAsync(sale.Id, new AddSalePaymentInput(1m, PaymentMethod.Cash, sale.GrandTotal), w.UserId));
    }

    // ---- returns follow the original's branch --------------------------------------------------------------------------

    [Fact]
    public async Task CreateSalesReturn_HeaderlessOrMatching_TakesTheInvoicesBranch_Mismatch_IsRefused()
    {
        var w = await BranchWorld.CreateAsync();
        var sale = await w.SaleAsync(w.Blr);
        var lineId = sale.Lines.Single().Id;
        CreateSaleReturnInput Input() => new(sale.Id, BranchWorld.Day, null, [new SaleReturnLineInput(lineId, null, 1m)]);

        await using (var db = w.Db(w.Main))
        {
            Assert.Equal("branchId", await FieldAsync(() => ListPricedSales.For(db).CreateReturnAsync(Input(), w.UserId)));
        }
        await using (var db = w.Db())
        {
            var headerless = await ListPricedSales.For(db).CreateReturnAsync(Input(), w.UserId);
            Assert.Equal((w.Blr, "SRT-BLR-0001"), (headerless.BranchId, headerless.ReturnNo));
        }
        await using (var db = w.Db(w.Blr))
        {
            var same = await ListPricedSales.For(db).CreateReturnAsync(Input(), w.UserId);
            Assert.Equal((w.Blr, "SRT-BLR-0002"), (same.BranchId, same.ReturnNo));
        }
    }

    [Fact]
    public async Task CreatePurchaseReturn_FollowsThePurchasesBranch_AndRefusesAHeaderForAnotherBranch()
    {
        var w = await BranchWorld.CreateAsync();
        var purchase = await w.PurchaseAsync(w.Blr);
        var input = new CreatePurchaseReturnInput(purchase.Id, BranchWorld.Day, null, [new PurchaseReturnLineInput(purchase.Lines.Single().Id, null, 1m)]);

        await using (var db = w.Db(w.Main))
        {
            Assert.Equal("branchId", await FieldAsync(() => new PurchaseService(db, new ExchangeRateService(db)).CreateReturnAsync(input, w.UserId)));
        }
        await using (var db = w.Db())
        {
            var created = await new PurchaseService(db, new ExchangeRateService(db)).CreateReturnAsync(input, w.UserId);
            Assert.Equal((w.Blr, "PRT-BLR-0001"), (created.BranchId, created.ReturnNo));
        }
    }

    [Fact]
    public async Task CreateReturn_OriginalOnAnInactiveBranch_IsRefused()
    {
        var w = await BranchWorld.CreateAsync();
        var sale = await w.SaleAsync(w.Blr);
        await using (var db = w.Db()) await new BranchService(db).SetStatusAsync(w.Blr, false, w.UserId);
        await using var returns = w.Db();

        Assert.Equal("branchId", await FieldAsync(() => ListPricedSales.For(returns).CreateReturnAsync(
            new CreateSaleReturnInput(sale.Id, BranchWorld.Day, null, [new SaleReturnLineInput(sale.Lines.Single().Id, null, 1m)]), w.UserId)));
    }

    // ---- account branch rule -------------------------------------------------------------------------------------------

    private static async Task<FinancialAccount> AccountAsync(BranchWorld w, string name, Guid? branch)
    {
        await using var db = w.Db();
        return await new FinancialAccountService(db).CreateAsync(new CreateAccountInput(name, FinancialAccountKind.Cash, null, 0m, BranchWorld.Day) { BranchId = branch }, w.UserId);
    }

    [Fact]
    public async Task Sale_PaidIntoAnAccountTaggedToAnotherBranch_IsRefusedOnAccountId_CompanyLevelAndOwnBranchAccountsWork()
    {
        var w = await BranchWorld.CreateAsync();
        var blrTill = await AccountAsync(w, "BLR till", w.Blr);
        var shared = await AccountAsync(w, "Company bank", null);

        Assert.Equal("AccountId", await FieldAsync(() => w.SaleAsync(w.Main, paid: 10m, account: blrTill.Id)));
        Assert.Equal("AccountId", await FieldAsync(() => w.PurchaseAsync(w.Main, paid: 10m, account: blrTill.Id)));
        await w.SaleAsync(w.Blr, paid: 10m, account: blrTill.Id);
        await using (var check = w.Db()) Assert.True(await check.FinancialTransactions.AnyAsync(t => t.AccountId == blrTill.Id));
        Assert.NotNull(await w.SaleAsync(w.Main, paid: 10m, account: shared.Id));
        Assert.NotNull(await w.SaleAsync(w.Blr, paid: 10m, account: shared.Id));
    }

    [Fact]
    public async Task AddPayment_ToAnAccountOfAnotherBranchThanTheInvoices_IsRefused()
    {
        var w = await BranchWorld.CreateAsync();
        var blrTill = await AccountAsync(w, "BLR till", w.Blr);
        var sale = await w.SaleAsync(w.Main);
        await using var db = w.Db();

        Assert.Equal("AccountId", await FieldAsync(() => ListPricedSales.For(db).AddPaymentAsync(sale.Id, new AddSalePaymentInput(5m, PaymentMethod.Cash, sale.GrandTotal, blrTill.Id), w.UserId)));
    }

    [Fact]
    public async Task Expense_CompanyLevelMayUseAnyAccount_ButABranchTaggedExpenseMayNotUseAnotherBranchsAccount()
    {
        var w = await BranchWorld.CreateAsync();
        var blrTill = await AccountAsync(w, "BLR till", w.Blr);
        await using var db = w.Db();
        var service = new ExpenseService(db, new ExchangeRateService(db));

        var companyLevel = await service.CreateAsync(new CreateExpenseInput(w.CategoryId, BranchWorld.Day, w.CurrencyId, 10m, PaymentMethod.Cash, null, null, blrTill.Id), w.UserId);
        Assert.NotNull(companyLevel);
        Assert.Equal("AccountId", await FieldAsync(() => service.CreateAsync(
            new CreateExpenseInput(w.CategoryId, BranchWorld.Day, w.CurrencyId, 10m, PaymentMethod.Cash, null, null, blrTill.Id) { BranchId = w.Main }, w.UserId)));
    }

    // ---- account / expense tagging --------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAccountAndExpense_TaggedWithAnInactiveOrUnknownBranch_IsRefusedOnBranchId()
    {
        var w = await BranchWorld.CreateAsync();
        await using (var db = w.Db()) await new BranchService(db).SetStatusAsync(w.Blr, false, w.UserId);

        Assert.Equal("BranchId", await FieldAsync(() => AccountAsync(w, "x", w.Blr)));
        Assert.Equal("BranchId", await FieldAsync(() => AccountAsync(w, "y", Guid.NewGuid())));
        await using var db2 = w.Db();
        Assert.Equal("BranchId", await FieldAsync(() => new ExpenseService(db2, new ExchangeRateService(db2)).CreateAsync(
            new CreateExpenseInput(w.CategoryId, BranchWorld.Day, w.CurrencyId, 1m, PaymentMethod.Cash, null, null) { BranchId = w.Blr }, w.UserId)));
    }

    [Fact]
    public async Task CreateSale_AfterOtherBranchesDisappear_HeaderlessDocumentsStillGoToMain()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        db.Branches.RemoveRange(db.Branches.Where(b => b.Id == w.Blr));
        await db.SaveChangesAsync();

        // Main is still there: header-less documents keep working after other branches disappear.
        Assert.Equal(w.Main, (await w.SaleAsync(null)).BranchId);
    }
}
