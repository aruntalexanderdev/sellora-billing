using Microsoft.AspNetCore.Mvc;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Accounts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Services;
using Sellora.Tests.Branches;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Accounts;

/// <summary>
/// Phase 09 (FI-D43 e) guard for the ledger list / totals / balances read rewrite: a branch-limited caller must see exactly the same rows,
/// totals and balances as before (source-document visibility; company-level accounts always visible; hidden branches never leak into aggregates).
/// </summary>
public class FinancialLedgerVisibilityTests
{
    private sealed record Seeded(BranchWorld World, Guid MainAccount, Guid BlrAccount, Guid SharedAccount);

    private static T Ok<T>(ActionResult<T> r) => (T)((OkObjectResult)r.Result!).Value!;

    private static async Task<Seeded> SeedAsync()
    {
        var w = await BranchWorld.CreateAsync();
        Guid main, blr, shared;
        await using (var db = w.Db())
        {
            var accounts = new FinancialAccountService(db);
            main = (await accounts.CreateAsync(new CreateAccountInput("Main till", FinancialAccountKind.Cash, null, 1000m, BranchWorld.Day) { BranchId = w.Main }, w.UserId)).Id;
            blr = (await accounts.CreateAsync(new CreateAccountInput("BLR till", FinancialAccountKind.Cash, null, 500m, BranchWorld.Day) { BranchId = w.Blr }, w.UserId)).Id;
            shared = (await accounts.CreateAsync(new CreateAccountInput("HQ bank", FinancialAccountKind.Bank, null, 300m, BranchWorld.Day), w.UserId)).Id;
        }
        await w.PurchaseAsync(w.Main, paid: 50m, account: main);   // out 50 (hidden from BLR)
        await w.PurchaseAsync(w.Blr, paid: 70m, account: blr);     // out 70
        await w.SaleAsync(w.Main, paid: 30m, account: main);       // in 30 (hidden from BLR)
        await w.SaleAsync(w.Blr, paid: 40m, account: blr);         // in 40
        return new Seeded(w, main, blr, shared);
    }

    private static (FinancialTransactionsController Tx, FinancialAccountsController Accounts) Controllers(BranchWorld w, bool restricted)
    {
        var db = restricted ? w.Db(allowed: new HashSet<Guid> { w.Blr }, unrestricted: false) : w.Db();
        var service = new FinancialAccountService(db);
        return (new FinancialTransactionsController(db, service), new FinancialAccountsController(db, service, new FakeEffectivePermissions("CashBank.View")));
    }

    [Fact]
    public async Task List_BranchLimitedCaller_SeesOnlyOwnBranchDocumentsAndVisibleAccounts_WithMatchingTotals()
    {
        var s = await SeedAsync();
        var (tx, _) = Controllers(s.World, restricted: true);

        var list = Ok(await tx.List(null, null, null, null, null, null, null, 1, 100, default));

        // BLR purchase payment + BLR sale payment + BLR opening + HQ (company-level) opening; Main's three entries stay hidden.
        Assert.Equal(4, list.Total);
        Assert.Equal(4, list.Items.Count);
        Assert.Equal(40m, list.MoneyIn);
        Assert.Equal(70m, list.MoneyOut);
        Assert.DoesNotContain(list.Items, i => i.AccountId == s.MainAccount);
    }

    [Fact]
    public async Task List_UnrestrictedCaller_SeesEverything_AndTotalsExcludeOpeningBalances()
    {
        var s = await SeedAsync();
        var (tx, _) = Controllers(s.World, restricted: false);

        var list = Ok(await tx.List(null, null, null, null, null, null, null, 1, 100, default));

        Assert.Equal(7, list.Total);
        Assert.Equal(70m, list.MoneyIn);
        Assert.Equal(120m, list.MoneyOut);
    }

    [Fact]
    public async Task List_EmptyResult_ReturnsZeroTotalsInsteadOfFailing()
    {
        var s = await SeedAsync();
        var (tx, _) = Controllers(s.World, restricted: false);

        var list = Ok(await tx.List("no-such-counterparty-xyz", null, null, null, null, null, null, 1, 20, default));

        Assert.Equal(0, list.Total);
        Assert.Empty(list.Items);
        Assert.Equal(0m, list.MoneyIn);
        Assert.Equal(0m, list.MoneyOut);
    }

    [Fact]
    public async Task List_PagesAreStable_AndCoverEveryRowExactlyOnce()
    {
        var s = await SeedAsync();
        var (tx, _) = Controllers(s.World, restricted: false);

        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++) seen.AddRange(Ok(await tx.List(null, null, null, null, null, null, null, page, 2, default)).Items.Select(i => i.Id));

        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }

    [Fact]
    public async Task Balances_BranchLimitedCaller_AggregateOnlyVisibleAccounts()
    {
        var s = await SeedAsync();
        var (_, accounts) = Controllers(s.World, restricted: true);

        var list = Ok(await accounts.List(null, default));

        Assert.DoesNotContain(list.Items, a => a.Id == s.MainAccount);
        Assert.Equal(470m, list.Items.Single(a => a.Id == s.BlrAccount).Balance);      // 500 - 70 + 40
        Assert.Null(list.Items.Single(a => a.Id == s.SharedAccount).Balance);            // shared balances hidden from a branch-limited caller
        Assert.Equal(470m, list.CashBalance);
        Assert.Equal(0m, list.BankBalance);
    }

    [Fact]
    public async Task Balances_UnrestrictedCaller_SeeEveryAccount_AndGetReturnsTheSingleAccountBalance()
    {
        var s = await SeedAsync();
        var (_, accounts) = Controllers(s.World, restricted: false);

        var list = Ok(await accounts.List(null, default));

        Assert.Equal(980m, list.Items.Single(a => a.Id == s.MainAccount).Balance);       // 1000 - 50 + 30
        Assert.Equal(300m, list.Items.Single(a => a.Id == s.SharedAccount).Balance) ;
        Assert.Equal(1450m, list.CashBalance);
        Assert.Equal(300m, list.BankBalance);
        Assert.Equal(470m, Ok(await accounts.Get(s.BlrAccount, default)).Balance);
    }

    [Fact]
    public async Task Balances_BranchFilter_ReturnsOnlyThatBranchsAccounts()
    {
        var s = await SeedAsync();
        var (_, accounts) = Controllers(s.World, restricted: false);

        var list = Ok(await accounts.List(s.World.Blr, default));

        Assert.Equal([s.BlrAccount], list.Items.Select(a => a.Id).ToArray());
        Assert.Equal(470m, list.Items[0].Balance);
    }
}
