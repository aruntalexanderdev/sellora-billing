using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Accounts;
using Sellora.Domain.Entities;
using Sellora.Domain.Expenses;
using Sellora.Domain.Purchasing;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Branches;

/// <summary>
/// Phase 5 T1/T2 (InMemory): the working branch (header, else primary, else default) and the "Branch" query filter on the five document
/// headers plus the optional-branch Expenses/FinancialAccounts (NULL = company level, always visible); AuditEvents are append-only.
/// </summary>
public class BranchScopeTests
{
    private static HashSet<Guid> Set(params Guid[] ids) => ids.ToHashSet();

    private static async Task<Guid> PurchaseBranchAsync(BranchWorld w, Guid? header, IReadOnlySet<Guid>? allowed, Guid? primary)
    {
        await using var db = w.Db(header, allowed, unrestricted: false, primary: primary);
        var purchase = await new PurchaseService(db, new ExchangeRateService(db)).CreatePurchaseAsync(
            new CreatePurchaseInput(w.SupplierId, BranchWorld.Day, null, null, PaymentMethod.Cash, 0, [new PurchaseLineInput(w.ProductId, w.ProductUnitId, 1m, 10m, 0, 0)]), w.UserId);
        return purchase.BranchId;
    }

    // ---- T1 working branch ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WorkingBranch_HeaderWinsOverPrimary()
    {
        var w = await BranchWorld.CreateAsync();
        Assert.Equal(w.Blr, await PurchaseBranchAsync(w, w.Blr, Set(w.Main, w.Blr), primary: w.Main));
    }

    [Fact]
    public async Task WorkingBranch_NoHeader_UsesThePrimaryBranch()
    {
        var w = await BranchWorld.CreateAsync();
        Assert.Equal(w.Blr, await PurchaseBranchAsync(w, null, Set(w.Main, w.Blr), primary: w.Blr));
    }

    [Fact]
    public async Task WorkingBranch_NoHeaderNoPrimary_CompanyWide_UsesTheDefaultBranch()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        var purchase = await new PurchaseService(db, new ExchangeRateService(db)).CreatePurchaseAsync(
            new CreatePurchaseInput(w.SupplierId, BranchWorld.Day, null, null, PaymentMethod.Cash, 0, [new PurchaseLineInput(w.ProductId, w.ProductUnitId, 1m, 10m, 0, 0)]), w.UserId);
        Assert.Equal(w.Main, purchase.BranchId);
    }

    [Fact]
    public async Task WorkingBranch_RestrictedWithoutPrimaryAndWithoutTheDefaultBranch_IsRefused()
    {
        var w = await BranchWorld.CreateAsync();
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => PurchaseBranchAsync(w, null, Set(w.Blr), primary: null));
        Assert.Equal("branchId", ex.Field);
    }

    [Fact]
    public async Task WorkingBranch_InactivePrimary_IsRefusedWith400BranchId()
    {
        var w = await BranchWorld.CreateAsync();
        await using (var db = w.Db()) await new BranchService(db, new StockLocationUsageService(db)).SetStatusAsync(w.Blr, false, w.UserId);
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => PurchaseBranchAsync(w, null, Set(w.Blr), primary: w.Blr));
        Assert.Equal("branchId", ex.Field);
    }

    [Fact]
    public async Task WorkingBranch_HeaderOfANonAllocatedBranch_IsRefused()
    {
        var w = await BranchWorld.CreateAsync();
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => PurchaseBranchAsync(w, w.Blr, Set(w.Main), primary: w.Main));
        Assert.Equal("branchId", ex.Field);
    }

    // ---- T2 Branch filter -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Filter_RestrictedUser_SeesOnlyAllocatedBranchDocuments_CompanyWideSeesAll()
    {
        var w = await BranchWorld.CreateAsync();
        var atMain = await w.PurchaseAsync(w.Main);
        var atBlr = await w.PurchaseAsync(w.Blr);
        var saleBlr = await w.SaleAsync(w.Blr);
        var saleMain = await w.SaleAsync(w.Main);
        await using var restricted = w.Db(null, Set(w.Main), unrestricted: false, primary: w.Main);
        await using var wide = w.Db();

        Assert.Contains(atMain.Id, await restricted.PurchaseOrders.Select(p => p.Id).ToListAsync());
        Assert.DoesNotContain(atBlr.Id, await restricted.PurchaseOrders.Select(p => p.Id).ToListAsync());
        Assert.DoesNotContain(saleBlr.Id, await restricted.SalesInvoices.Select(p => p.Id).ToListAsync());
        Assert.Contains(saleMain.Id, await restricted.SalesInvoices.Select(p => p.Id).ToListAsync());
        Assert.Contains(atBlr.Id, await wide.PurchaseOrders.Select(p => p.Id).ToListAsync());
        Assert.Contains(saleBlr.Id, await wide.SalesInvoices.Select(p => p.Id).ToListAsync());
    }

    [Fact]
    public async Task Filter_ChildLinesOfAHiddenInvoice_AreNotReturned()
    {
        var w = await BranchWorld.CreateAsync();
        var hidden = await w.SaleAsync(w.Blr);
        var visible = await w.SaleAsync(w.Main);
        await using var restricted = w.Db(null, Set(w.Main), unrestricted: false, primary: w.Main);

        var lineInvoiceIds = await restricted.SalesInvoiceLines.Select(l => l.SalesInvoiceId).Distinct().ToListAsync();

        Assert.Contains(visible.Id, lineInvoiceIds);
        Assert.DoesNotContain(hidden.Id, lineInvoiceIds);
    }

    [Fact]
    public async Task Filter_ExpensesAndAccounts_CompanyLevelAlwaysVisible_OtherBranchHidden()
    {
        var w = await BranchWorld.CreateAsync();
        Guid companyExpense, blrExpense, mainExpense, companyAccount, blrAccount;
        await using (var db = w.Db())
        {
            var expenses = new ExpenseService(db, new ExchangeRateService(db));
            CreateExpenseInput Input(Guid? branch) => new(w.CategoryId, BranchWorld.Day, w.CurrencyId, 10m, PaymentMethod.Cash, null, null) { BranchId = branch };
            companyExpense = (await expenses.CreateAsync(Input(null), w.UserId)).Id;
            blrExpense = (await expenses.CreateAsync(Input(w.Blr), w.UserId)).Id;
            mainExpense = (await expenses.CreateAsync(Input(w.Main), w.UserId)).Id;
            var accounts = new FinancialAccountService(db);
            companyAccount = (await accounts.CreateAsync(new CreateAccountInput("Company till", FinancialAccountKind.Cash, null, 0m, BranchWorld.Day), w.UserId)).Id;
            blrAccount = (await accounts.CreateAsync(new CreateAccountInput("Blr till", FinancialAccountKind.Cash, null, 0m, BranchWorld.Day) { BranchId = w.Blr }, w.UserId)).Id;
        }
        await using var restricted = w.Db(null, Set(w.Main), unrestricted: false, primary: w.Main);

        var expenseIds = await restricted.Expenses.Select(e => e.Id).ToListAsync();
        var accountIds = await restricted.FinancialAccounts.Select(a => a.Id).ToListAsync();

        Assert.Contains(companyExpense, expenseIds);
        Assert.Contains(mainExpense, expenseIds);
        Assert.DoesNotContain(blrExpense, expenseIds);
        Assert.Contains(companyAccount, accountIds);
        Assert.DoesNotContain(blrAccount, accountIds);
    }

    [Fact]
    public async Task Filter_BypassedReads_StillSeeEveryBranch_ButStayInTheCompany()
    {
        var w = await BranchWorld.CreateAsync();
        var hidden = await w.SaleAsync(w.Blr);
        await using var restricted = w.Db(null, Set(w.Main), unrestricted: false, primary: w.Main);

        var ids = await restricted.SalesInvoices.IgnoreQueryFilters([BranchQueryFilter.Name]).Select(s => s.Id).ToListAsync();

        Assert.Contains(hidden.Id, ids);
    }

    // ---- audit append-only ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AuditEvents_ModifyOrDelete_Throws()
    {
        var w = await BranchWorld.CreateAsync();
        var id = Guid.NewGuid();
        await using (var db = w.Db())
        {
            db.AuditEvents.Add(new AuditEvent { Id = id, Action = AuditActions.MembershipCreated, TargetType = AuditTargets.Membership, TargetId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }
        await using (var db = w.Db())
        {
            (await db.AuditEvents.SingleAsync(a => a.Id == id)).Reason = "tamper";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = w.Db())
        {
            db.AuditEvents.Remove(await db.AuditEvents.SingleAsync(a => a.Id == id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using var check = w.Db();
        Assert.Equal(1, await check.AuditEvents.CountAsync(a => a.Id == id));
    }

    [Fact]
    public async Task AuditEvents_AreStampedWithTheCompany_AndFilteredByIt()
    {
        var w = await BranchWorld.CreateAsync();
        await using (var db = w.Db())
        {
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), Action = AuditActions.MembershipCreated, TargetType = AuditTargets.Membership, TargetId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }
        await using var other = new SelloraDbContext(w.Options, TestCompany.For(TestCompany.OtherId), BranchContext.Unrestricted());
        Assert.Equal(0, await other.AuditEvents.CountAsync());
        await using var own = w.Db();
        Assert.Equal(Sellora.Domain.Companies.DefaultTenant.CompanyId, (await own.AuditEvents.SingleAsync()).CompanyId);
    }
}
