using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Accounts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Accounts;

/// <summary>Phase 07 (FI-D19..D22): funding, overdraft, source-account rule, reversal, statement, idempotency, audit and the reports' treatment of funding.</summary>
public class CashBankFundingTests
{
    private sealed record Fixture(
        SelloraDbContext Db, DbContextOptions<SelloraDbContext> Options, FinancialAccountService Accounts, FinancialAccountsController AccountsApi,
        FinancialTransactionsController TransactionsApi, AccountsReportsController Reports, Customer Customer, Guid UserId);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly DateOnly Past = new(2026, 9, 1);

    private static async Task<Fixture> SeedAsync()
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = TestCompany.NewDb(options);
        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User { Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin", Email = "admin@selloramart.in", PasswordHash = "x", RoleId = role.Id, Role = role };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Kavitha Stores" };
        db.AddRange(role, user, inr, settings, customer);
        await db.SaveChangesAsync();

        var accounts = new FinancialAccountService(db);
        var perms = new FakeEffectivePermissions("CashBank.View", "Reports.Accounts");
        return new Fixture(db, options, accounts, new FinancialAccountsController(db, accounts, perms), new FinancialTransactionsController(db, accounts),
            new AccountsReportsController(db, perms), customer, user.Id);
    }

    private static Task<FinancialAccount> Account(Fixture f, string name, FinancialAccountKind kind = FinancialAccountKind.Cash, decimal opening = 0m, decimal overdraft = 0m) =>
        f.Accounts.CreateAsync(new CreateAccountInput(name, kind, null, opening, Past) { OverdraftLimit = overdraft }, f.UserId);

    private static FundingInput Fund(Guid account, FundingSource source = FundingSource.OwnerCapital, decimal amount = 100m, DateOnly? date = null,
        string? reference = null, string? counterparty = null, Guid? customer = null, string? reason = null, string? key = null) =>
        new(account, source, amount, date ?? Past, reference, counterparty, customer, reason, null) { IdempotencyKey = key };

    private static async Task<decimal> Balance(Fixture f, Guid accountId) =>
        (await f.Db.FinancialTransactions.AsNoTracking().Where(t => t.AccountId == accountId && t.Status == FinancialTransactionStatus.Active).ToListAsync())
        .Sum(t => t.Direction == MoneyDirection.In ? t.BaseAmount : -t.BaseAmount);

    private static async Task<string> FieldOf(Func<Task> act) => (await Assert.ThrowsAsync<DomainValidationException>(act)).Field;

    private static T Ok<T>(ActionResult<T> r) => (T)((OkObjectResult)r.Result!).Value!;

    // ---------------------------------------------------------------- funding

    [Theory]
    [InlineData(FundingSource.OwnerCapital)]
    [InlineData(FundingSource.Loan)]
    [InlineData(FundingSource.CashDeposit)]
    [InlineData(FundingSource.CustomerReceipt)]
    [InlineData(FundingSource.Other)]
    public async Task FundAsync_EachSource_PostsOneInEntry_AndAuditsIt(FundingSource source)
    {
        var f = await SeedAsync();
        var till = await Account(f, "Till");
        var id = await f.Accounts.FundAsync(Fund(till.Id, source, 250.456m, reference: "REF-1", counterparty: "HDFC", reason: "approved by owner",
            customer: source == FundingSource.CustomerReceipt ? f.Customer.Id : null), f.UserId, "corr-1");

        var row = await f.Db.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.Equal((FinancialTransactionType.Funding, MoneyDirection.In, 250.46m, source), (row.Type, row.Direction, row.BaseAmount, row.FundingSource));
        Assert.Equal("REF-1", row.SourceReference);
        Assert.Equal(250.46m, await Balance(f, till.Id));
        var audit = await f.Db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == AuditActions.CashFunded);
        Assert.Equal((id, "corr-1", f.UserId), (audit.TargetId, audit.CorrelationId, audit.ActorUserId));
        Assert.Contains("balanceAfter", audit.After);
    }

    [Fact]
    public async Task FundAsync_InvalidRequests_AreRejectedWithTheRequestField_AndWriteNothing()
    {
        var f = await SeedAsync();
        var till = await Account(f, "Till");
        var closed = await Account(f, "Closed");
        closed.IsActive = false;
        var inactiveCustomer = new Customer { Id = Guid.NewGuid(), Name = "Old", IsActive = false };
        f.Db.Customers.Add(inactiveCustomer);
        await f.Db.SaveChangesAsync();
        var before = await f.Db.FinancialTransactions.CountAsync();

        Task Run(FundingInput input) => f.Accounts.FundAsync(input, f.UserId);
        Assert.Equal("amount", await FieldOf(() => Run(Fund(till.Id, amount: 0m))));
        Assert.Equal("amount", await FieldOf(() => Run(Fund(till.Id, amount: 0.004m)))); // rounds to zero at 2 dp
        Assert.Equal("fundingDate", await FieldOf(() => Run(Fund(till.Id, date: Today.AddDays(30)))));
        Assert.Equal("accountId", await FieldOf(() => Run(Fund(closed.Id))));
        Assert.Equal("accountId", await FieldOf(() => Run(Fund(Guid.NewGuid()))));
        Assert.Equal("customerId", await FieldOf(() => Run(Fund(till.Id, FundingSource.CustomerReceipt, reference: "R1"))));
        Assert.Equal("sourceReference", await FieldOf(() => Run(Fund(till.Id, FundingSource.CustomerReceipt, customer: f.Customer.Id))));
        Assert.Equal("customerId", await FieldOf(() => Run(Fund(till.Id, FundingSource.CustomerReceipt, reference: "R1", customer: Guid.NewGuid()))));
        Assert.Equal("customerId", await FieldOf(() => Run(Fund(till.Id, FundingSource.CustomerReceipt, reference: "R1", customer: inactiveCustomer.Id))));
        Assert.Equal("customerId", await FieldOf(() => Run(Fund(till.Id, FundingSource.Loan, customer: f.Customer.Id))));
        Assert.Equal("reason", await FieldOf(() => Run(Fund(till.Id, FundingSource.Other))));
        Assert.Equal("reason", await FieldOf(() => Run(Fund(till.Id, FundingSource.Other, reason: "  "))));
        Assert.Equal(before, await f.Db.FinancialTransactions.CountAsync());
        Assert.Equal(0, await f.Db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task FundAsync_ReplayWithTheSameKey_ReturnsTheSameTransaction_AndPostsOnce()
    {
        var f = await SeedAsync();
        var till = await Account(f, "Till");

        var first = await f.Accounts.FundAsync(Fund(till.Id, key: "k-1"), f.UserId);
        var second = await f.Accounts.FundAsync(Fund(till.Id, key: "k-1"), f.UserId);

        Assert.Equal(first, second);
        Assert.Equal(1, await f.Db.FinancialTransactions.CountAsync(t => t.Type == FinancialTransactionType.Funding));
        Assert.Equal(1, await f.Db.AuditEvents.CountAsync(a => a.Action == AuditActions.CashFunded));
        Assert.Equal(100m, await Balance(f, till.Id));
        Assert.NotEqual(first, await f.Accounts.FundAsync(Fund(till.Id, key: "k-2"), f.UserId));
    }

    // ---------------------------------------------------------------- overdraft

    [Fact]
    public async Task CreateAndUpdate_OverdraftLimit_IsValidated_CashMustBeZero()
    {
        var f = await SeedAsync();
        Assert.Equal("overdraftLimit", await FieldOf(() => Account(f, "Till", FinancialAccountKind.Cash, overdraft: 10m)));
        Assert.Equal("overdraftLimit", await FieldOf(() => Account(f, "Neg", FinancialAccountKind.Bank, overdraft: -1m)));
        var bank = await Account(f, "HDFC", FinancialAccountKind.Bank, overdraft: 500m);
        Assert.Equal(500m, bank.OverdraftLimit);

        var cash = await Account(f, "Till2");
        Assert.Equal("overdraftLimit", await FieldOf(() => f.Accounts.UpdateAsync(cash.Id, new UpdateAccountInput("Till2", null) { OverdraftLimit = 5m })));
        Assert.Equal(800m, (await f.Accounts.UpdateAsync(bank.Id, new UpdateAccountInput("HDFC", null) { OverdraftLimit = 800m }))!.OverdraftLimit);
        Assert.Equal(800m, (await f.Accounts.UpdateAsync(bank.Id, new UpdateAccountInput("HDFC", null)))!.OverdraftLimit); // null keeps it
    }

    [Fact]
    public async Task UpdateAsync_LoweringTheOverdraftBelowTheCurrentOverdraw_Is400_RaisingOrStayingAboveIsFine()
    {
        var f = await SeedAsync();
        var bank = await Account(f, "ICICI", FinancialAccountKind.Bank, overdraft: 500m);
        var till = await Account(f, "Till3");
        await f.Accounts.TransferAsync(new(bank.Id, till.Id, 300m, Past, null), f.UserId); // bank now at -300

        Assert.Equal("overdraftLimit", await FieldOf(() => f.Accounts.UpdateAsync(bank.Id, new UpdateAccountInput("ICICI", null) { OverdraftLimit = 299m })));
        Assert.Equal(500m, (await f.Db.FinancialAccounts.SingleAsync(a => a.Id == bank.Id)).OverdraftLimit); // unchanged on refusal
        Assert.Equal(300m, (await f.Accounts.UpdateAsync(bank.Id, new UpdateAccountInput("ICICI", null) { OverdraftLimit = 300m }))!.OverdraftLimit);
        Assert.Equal(900m, (await f.Accounts.UpdateAsync(bank.Id, new UpdateAccountInput("ICICI", null) { OverdraftLimit = 900m }))!.OverdraftLimit);
    }

    [Fact]
    public async Task TransferAsync_CashCannotGoBelowZero_BankMayUseItsOverdraft()
    {
        var f = await SeedAsync();
        var cash = await Account(f, "Till", opening: 100m);
        var bank = await Account(f, "HDFC", FinancialAccountKind.Bank, opening: 50m, overdraft: 200m);

        Assert.Equal("amount", await FieldOf(() => f.Accounts.TransferAsync(new(cash.Id, bank.Id, 100.01m, Past, null), f.UserId)));
        await f.Accounts.TransferAsync(new(cash.Id, bank.Id, 100m, Past, null), f.UserId); // exactly to zero is fine
        Assert.Equal(0m, await Balance(f, cash.Id));
    }

    [Fact]
    public async Task TransferAsync_BankOverdraft_IsEnforcedAtTheLimit()
    {
        var f = await SeedAsync();
        var cash = await Account(f, "Till", opening: 100m);
        var bank = await Account(f, "HDFC", FinancialAccountKind.Bank, opening: 50m, overdraft: 200m);

        Assert.Equal("amount", await FieldOf(() => f.Accounts.TransferAsync(new(bank.Id, cash.Id, 250.01m, Past, null), f.UserId)));
        var before = await f.Db.FinancialTransactions.CountAsync();
        await f.Accounts.TransferAsync(new(bank.Id, cash.Id, 250m, Past, null), f.UserId); // 50 - 250 = -200 = the limit
        Assert.Equal(-200m, await Balance(f, bank.Id));
        Assert.Equal(350m, await Balance(f, cash.Id));
        Assert.Equal(before + 2, await f.Db.FinancialTransactions.CountAsync());
    }

    [Fact]
    public async Task TransferAsync_PostsBothLegsOrNeither_AndAuditsBalances()
    {
        var f = await SeedAsync();
        var cash = await Account(f, "Till", opening: 100m);
        var bank = await Account(f, "HDFC", FinancialAccountKind.Bank);
        var before = await f.Db.FinancialTransactions.CountAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => f.Accounts.TransferAsync(new(cash.Id, bank.Id, 500m, Past, null), f.UserId));
        Assert.Equal(before, await f.Db.FinancialTransactions.CountAsync());
        Assert.Equal(0, await f.Db.AuditEvents.CountAsync());

        await f.Accounts.TransferAsync(new(cash.Id, bank.Id, 40m, Past, "n") { IdempotencyKey = "t-1" }, f.UserId, "corr-9");
        await f.Accounts.TransferAsync(new(cash.Id, bank.Id, 40m, Past, "n") { IdempotencyKey = "t-1" }, f.UserId, "corr-9"); // replay: nothing more

        var legs = await f.Db.FinancialTransactions.AsNoTracking().Where(t => t.Type == FinancialTransactionType.Transfer).ToListAsync();
        Assert.Equal(2, legs.Count);
        Assert.Single(legs.Select(l => l.TransferGroupId).Distinct());
        Assert.Equal(60m, await Balance(f, cash.Id));
        Assert.Equal(40m, await Balance(f, bank.Id));
        var audit = await f.Db.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(AuditActions.CashTransferred, audit.Action);
        Assert.Equal(legs[0].TransferGroupId, audit.TargetId);
    }

    // ---------------------------------------------------------------- FI-D22 source-account rule

    private static SelloraDbContext Scoped(Fixture f, Guid active, IReadOnlySet<Guid>? allowed)
    {
        var branch = new BranchContext();
        branch.Set(active, allowed, null, active);
        branch.UseWriteScope();
        return new SelloraDbContext(f.Options, TestCompany.Context, branch);
    }

    [Fact]
    public async Task SourceAccountRule_ActiveBranchOrCompanyLevelForAllBranchMembers()
    {
        var f = await SeedAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var accountA = new FinancialAccount { Id = Guid.NewGuid(), Name = "A till", Kind = FinancialAccountKind.Cash, BranchId = a, CreatedByUserId = f.UserId };
        var accountB = new FinancialAccount { Id = Guid.NewGuid(), Name = "B till", Kind = FinancialAccountKind.Cash, BranchId = b, CreatedByUserId = f.UserId };
        var shared = new FinancialAccount { Id = Guid.NewGuid(), Name = "Head office", Kind = FinancialAccountKind.Cash, CreatedByUserId = f.UserId };
        f.Db.FinancialAccounts.AddRange(accountA, accountB, shared);
        await f.Db.SaveChangesAsync();
        foreach (var account in new[] { accountA, accountB, shared })
        {
            await f.Accounts.FundAsync(Fund(account.Id, amount: 1000m), f.UserId);
        }

        // Fund every account above first (the all-branch context of the fixture); then act as members.
        var limited = new FinancialAccountService(Scoped(f, a, new HashSet<Guid> { a, b }));
        Assert.Equal("fromAccountId", await FieldOf(() => limited.TransferAsync(new(shared.Id, accountA.Id, 10m, Past, null), f.UserId)));  // company-level: not for a limited member
        Assert.Equal("fromAccountId", await FieldOf(() => limited.TransferAsync(new(accountB.Id, accountA.Id, 10m, Past, null), f.UserId))); // another branch than the active one
        Assert.Equal("accountId", await FieldOf(() => limited.FundAsync(Fund(shared.Id), f.UserId)));
        await limited.TransferAsync(new(accountA.Id, shared.Id, 10m, Past, null), f.UserId);   // active-branch source, company-level destination: fine
        await limited.TransferAsync(new(accountA.Id, accountB.Id, 10m, Past, null), f.UserId); // destination may be any accessible account
        await limited.FundAsync(Fund(accountA.Id), f.UserId);

        var everywhere = new FinancialAccountService(Scoped(f, a, null));
        await everywhere.TransferAsync(new(shared.Id, accountA.Id, 10m, Past, null), f.UserId);   // company-level for a member covering every branch
        Assert.Equal("fromAccountId", await FieldOf(() => everywhere.TransferAsync(new(accountB.Id, accountA.Id, 10m, Past, null), f.UserId)));

        var restricted = new FinancialAccountService(Scoped(f, a, new HashSet<Guid> { a }));
        Assert.Equal("fromAccountId", await FieldOf(() => restricted.TransferAsync(new(shared.Id, accountA.Id, 10m, Past, null), f.UserId)));
    }

    // ---------------------------------------------------------------- reversal

    [Fact]
    public async Task ReverseAsync_Funding_PostsTheOppositeEntry_LinkedAndAudited()
    {
        var f = await SeedAsync();
        var till = await Account(f, "Till");
        var funding = await f.Accounts.FundAsync(Fund(till.Id, amount: 300m), f.UserId);

        var ids = await f.Accounts.ReverseAsync(new(funding, " keyed twice "), f.UserId, "corr-r");

        var reversal = await f.Db.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Id == ids!.Single());
        Assert.Equal((FinancialTransactionType.Reversal, MoneyDirection.Out, 300m, (Guid?)funding, "keyed twice", Today, (Guid?)till.Id),
            (reversal.Type, reversal.Direction, reversal.BaseAmount, reversal.ReversalOfTransactionId, reversal.Reason, reversal.TransactionDate, reversal.AccountId));
        Assert.Equal(FinancialTransactionStatus.Active, (await f.Db.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Id == funding)).Status);
        Assert.Equal(0m, await Balance(f, till.Id));
        var audit = await f.Db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == AuditActions.CashReversed);
        Assert.Equal((funding, (string?)"keyed twice"), (audit.TargetId, audit.Reason));
    }

    [Fact]
    public async Task ReverseAsync_Rules_TypeOnceReconciledReasonAndOverdraft()
    {
        var f = await SeedAsync();
        var till = await Account(f, "Till", opening: 100m);
        var bank = await Account(f, "HDFC", FinancialAccountKind.Bank);
        var funding = await f.Accounts.FundAsync(Fund(till.Id, amount: 50m), f.UserId);
        var reconciledFunding = await f.Accounts.FundAsync(Fund(till.Id, amount: 5m), f.UserId);
        await f.Accounts.ReconcileAsync(till.Id, [reconciledFunding], f.UserId);

        Assert.Equal("reason", await FieldOf(() => f.Accounts.ReverseAsync(new(funding, "  "), f.UserId)));
        Assert.Equal("reason", await FieldOf(() => f.Accounts.ReverseAsync(new(funding, new string('x', 301)), f.UserId)));
        Assert.Equal("id", await FieldOf(() => f.Accounts.ReverseAsync(new(reconciledFunding, "oops"), f.UserId)));

        // A reversal cannot itself be reversed (not an allowed type), and nothing is reversed twice.
        var first = await f.Accounts.ReverseAsync(new(funding, "oops"), f.UserId);
        Assert.Equal("id", await FieldOf(() => f.Accounts.ReverseAsync(new(funding, "again"), f.UserId)));
        Assert.Equal("id", await FieldOf(() => f.Accounts.ReverseAsync(new(first!.Single(), "undo"), f.UserId)));

        // Payment-sourced entries are not reversed here.
        f.Db.FinancialTransactions.Add(new FinancialTransaction
        {
            Id = Guid.NewGuid(), Type = FinancialTransactionType.ExpensePayment, Direction = MoneyDirection.Out, TransactionDate = Past, AccountId = till.Id,
            CurrencyId = (await f.Db.Currencies.SingleAsync()).Id, Amount = 1m, BaseAmount = 1m, CreatedByUserId = f.UserId,
        });
        await f.Db.SaveChangesAsync();
        var payment = await f.Db.FinancialTransactions.Where(t => t.Type == FinancialTransactionType.ExpensePayment).Select(t => t.Id).SingleAsync();
        Assert.Equal("id", await FieldOf(() => f.Accounts.ReverseAsync(new(payment, "x"), f.UserId)));

        // Reversing a funding debits the account: refused when the money has been spent (cash floor 0).
        await f.Accounts.TransferAsync(new(till.Id, bank.Id, 104m, Past, null), f.UserId);
        Assert.Equal(0m, await Balance(f, till.Id));
        var late = await f.Accounts.FundAsync(Fund(till.Id, amount: 20m), f.UserId);
        await f.Accounts.TransferAsync(new(till.Id, bank.Id, 20m, Past, null), f.UserId);
        Assert.Equal("amount", await FieldOf(() => f.Accounts.ReverseAsync(new(late, "oops"), f.UserId)));
    }

    [Fact]
    public async Task ReverseAsync_OpeningBalance_IsReversible()
    {
        var f = await SeedAsync();
        var till = await Account(f, "Till", opening: 100m);
        var opening = await f.Db.FinancialTransactions.Where(t => t.Type == FinancialTransactionType.OpeningBalance).Select(t => t.Id).SingleAsync();

        await f.Accounts.ReverseAsync(new(opening, "wrong figure"), f.UserId);

        Assert.Equal(0m, await Balance(f, till.Id));
    }

    [Fact]
    public async Task ReverseAsync_Transfer_ReversesBothLegsAsOneNewGroup_AndOnlyOnce()
    {
        var f = await SeedAsync();
        var cash = await Account(f, "Till", opening: 100m);
        var bank = await Account(f, "HDFC", FinancialAccountKind.Bank);
        await f.Accounts.TransferAsync(new(cash.Id, bank.Id, 60m, Past, null), f.UserId);
        var legs = await f.Db.FinancialTransactions.AsNoTracking().Where(t => t.Type == FinancialTransactionType.Transfer).ToListAsync();

        var ids = await f.Accounts.ReverseAsync(new(legs[0].Id, "wrong account"), f.UserId);

        Assert.Equal(2, ids!.Count);
        var reversals = await f.Db.FinancialTransactions.AsNoTracking().Where(t => ids.Contains(t.Id)).ToListAsync();
        Assert.All(reversals, r => Assert.Equal(FinancialTransactionType.Reversal, r.Type));
        Assert.Equal(legs.Select(l => l.Id).Order(), reversals.Select(r => r.ReversalOfTransactionId!.Value).Order());
        Assert.Single(reversals.Select(r => r.TransferGroupId).Where(g => g != legs[0].TransferGroupId).Distinct());
        Assert.Equal(100m, await Balance(f, cash.Id));
        Assert.Equal(0m, await Balance(f, bank.Id));
        Assert.Equal("id", await FieldOf(() => f.Accounts.ReverseAsync(new(legs[1].Id, "again"), f.UserId))); // either leg: already reversed
    }

    [Fact]
    public async Task ReverseAsync_Transfer_RefusedWhenTheReceivingAccountHasSpentTheMoney()
    {
        var f = await SeedAsync();
        var cash = await Account(f, "Till", opening: 100m);
        var bank = await Account(f, "HDFC", FinancialAccountKind.Bank);
        var other = await Account(f, "Other", FinancialAccountKind.Bank);
        await f.Accounts.TransferAsync(new(cash.Id, bank.Id, 60m, Past, null), f.UserId);
        await f.Accounts.TransferAsync(new(bank.Id, other.Id, 50m, Past, null), f.UserId);
        var leg = await f.Db.FinancialTransactions.Where(t => t.Type == FinancialTransactionType.Transfer && t.AccountId == cash.Id).Select(t => t.Id).SingleAsync();
        var before = await f.Db.FinancialTransactions.CountAsync();

        Assert.Equal("amount", await FieldOf(() => f.Accounts.ReverseAsync(new(leg, "oops"), f.UserId)));

        Assert.Equal(before, await f.Db.FinancialTransactions.CountAsync()); // neither leg posted
    }

    [Fact]
    public async Task ReverseAsync_UnknownOrOtherCompanyTransaction_IsNotFound()
    {
        var f = await SeedAsync();
        await using var other = new SelloraDbContext(f.Options, TestCompany.For(TestCompany.OtherId), TestCompany.Branch);
        var account = new FinancialAccount { Id = Guid.NewGuid(), Name = "Theirs", Kind = FinancialAccountKind.Cash, CreatedByUserId = f.UserId };
        var theirs = new FinancialTransaction
        {
            Id = Guid.NewGuid(), Type = FinancialTransactionType.OpeningBalance, Direction = MoneyDirection.In, TransactionDate = Past, AccountId = account.Id,
            CurrencyId = Guid.NewGuid(), Amount = 10m, BaseAmount = 10m, CreatedByUserId = f.UserId,
        };
        other.AddRange(account, theirs);
        await other.SaveChangesAsync();

        Assert.Null(await f.Accounts.ReverseAsync(new(theirs.Id, "x"), f.UserId));
        Assert.Null(await f.Accounts.ReverseAsync(new(Guid.NewGuid(), "x"), f.UserId));
        Assert.Equal(0, await f.Db.FinancialTransactions.CountAsync(t => t.Type == FinancialTransactionType.Reversal));
    }

    [Fact]
    public async Task ReverseAsync_ReplayWithTheSameKey_ReturnsTheSameIds_AndPostsOnce()
    {
        var f = await SeedAsync();
        var cash = await Account(f, "Till", opening: 100m);
        var bank = await Account(f, "HDFC", FinancialAccountKind.Bank);
        await f.Accounts.TransferAsync(new(cash.Id, bank.Id, 60m, Past, null), f.UserId);
        var leg = await f.Db.FinancialTransactions.Where(t => t.Type == FinancialTransactionType.Transfer).Select(t => t.Id).FirstAsync();
        var funding = await f.Accounts.FundAsync(Fund(cash.Id, amount: 5m), f.UserId);

        var first = await f.Accounts.ReverseAsync(new(leg, "oops") { IdempotencyKey = "r-1" }, f.UserId);
        var second = await f.Accounts.ReverseAsync(new(leg, "oops") { IdempotencyKey = "r-1" }, f.UserId);
        var single1 = await f.Accounts.ReverseAsync(new(funding, "oops") { IdempotencyKey = "r-2" }, f.UserId);
        var single2 = await f.Accounts.ReverseAsync(new(funding, "oops") { IdempotencyKey = "r-2" }, f.UserId);

        Assert.Equal(first!.Order(), second!.Order());
        Assert.Equal(single1, single2);
        Assert.Equal(3, await f.Db.FinancialTransactions.CountAsync(t => t.Type == FinancialTransactionType.Reversal));
        Assert.Equal(2, await f.Db.AuditEvents.CountAsync(a => a.Action == AuditActions.CashReversed));
    }

    // ---------------------------------------------------------------- statement and list

    [Fact]
    public async Task Statement_OpeningRunningAndClosingBalances_AddUp()
    {
        var f = await SeedAsync();
        var till = await Account(f, "Till", opening: 100m);                       // 1 Sep
        var bank = await Account(f, "HDFC", FinancialAccountKind.Bank);
        await f.Accounts.FundAsync(Fund(till.Id, FundingSource.Loan, 50m, new DateOnly(2026, 9, 10), counterparty: "SBI"), f.UserId);   // 150
        await f.Accounts.TransferAsync(new(till.Id, bank.Id, 30m, new DateOnly(2026, 9, 12), null), f.UserId);                          // 120
        await f.Accounts.FundAsync(Fund(till.Id, FundingSource.OwnerCapital, 999m, new DateOnly(2026, 10, 5)), f.UserId);               // after the window
        var funding = await f.Db.FinancialTransactions.Where(t => t.FundingSource == FundingSource.Loan).Select(t => t.Id).SingleAsync();
        await f.Accounts.ReverseAsync(new(funding, "oops"), f.UserId); // dated today, outside the window

        var s = Ok(await f.AccountsApi.Statement(till.Id, new DateOnly(2026, 9, 5), new DateOnly(2026, 9, 30), default));

        Assert.Equal((100m, 120m, 50m, 30m), (s.OpeningBalance, s.ClosingBalance, s.TotalIn, s.TotalOut));
        Assert.Equal([150m, 120m], s.Rows.Select(r => r.RunningBalance));
        Assert.Equal(FinancialTransactionType.Funding, s.Rows[0].Type);
        Assert.Equal("Loan", s.Rows[0].FundingSource);
        Assert.NotNull(s.Rows[0].ReversedByTransactionId);
        Assert.Null(s.Rows[1].ReversedByTransactionId);
        Assert.Equal("Transfer to HDFC", s.Rows[1].Description);

        var wide = Ok(await f.AccountsApi.Statement(till.Id, new DateOnly(2026, 1, 1), new DateOnly(2099, 1, 1), default));
        Assert.Equal(0m, wide.OpeningBalance);
        Assert.Equal(await Balance(f, till.Id), wide.ClosingBalance);
        Assert.Contains(wide.Rows, r => r.IsReversal);
        Assert.Equal(wide.Rows.Last().RunningBalance, wide.ClosingBalance);
    }

    [Fact]
    public async Task Statement_Rules_BadRangeIsRejected_UnknownIs404_HiddenSharedAccountIs403()
    {
        var f = await SeedAsync();
        var shared = await Account(f, "Till", opening: 10m);

        var bad = await f.AccountsApi.Statement(shared.Id, new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 1), default);
        Assert.IsType<ObjectResult>(bad.Result); // ValidationProblem
        Assert.IsType<NotFoundResult>((await f.AccountsApi.Statement(Guid.NewGuid(), null, null, default)).Result);

        var limited = Scoped(f, Guid.NewGuid(), new HashSet<Guid> { Guid.NewGuid() });
        var hidden = new FinancialAccountsController(limited, new FinancialAccountService(limited), new FakeEffectivePermissions("CashBank.View"));
        var forbidden = Assert.IsType<ObjectResult>((await hidden.Statement(shared.Id, null, null, default)).Result);
        Assert.Equal(403, forbidden.StatusCode);
        var defaults = Ok(await f.AccountsApi.Statement(shared.Id, null, null, default));
        Assert.Equal(1, defaults.From.Day);
        Assert.Equal(DateTime.DaysInMonth(defaults.To.Year, defaults.To.Month), defaults.To.Day);
    }

    [Fact]
    public async Task TransactionList_AlwaysCarriesFundingFields_AndReversalLinks()
    {
        var f = await SeedAsync();
        var till = await Account(f, "Till", opening: 10m);
        var funding = await f.Accounts.FundAsync(Fund(till.Id, FundingSource.Loan, 40m, reference: "L-1", counterparty: "SBI"), f.UserId);
        var reversal = (await f.Accounts.ReverseAsync(new(funding, "oops"), f.UserId))!.Single();

        var list = Ok(await f.TransactionsApi.List(null, null, null, null, null, null, null, 1, 50, default));

        var fundingRow = list.Items.Single(i => i.Id == funding);
        Assert.Equal(((FundingSource?)FundingSource.Loan, (string?)"L-1", (string?)"SBI", (Guid?)reversal, (Guid?)null), (fundingRow.FundingSource, fundingRow.SourceReference, fundingRow.Counterparty, fundingRow.ReversedByTransactionId, fundingRow.ReversalOfTransactionId));
        var reversalRow = list.Items.Single(i => i.Id == reversal);
        Assert.Equal(((Guid?)funding, (string?)"oops", (Guid?)null), (reversalRow.ReversalOfTransactionId, reversalRow.Reason, reversalRow.ReversedByTransactionId));
        var opening = list.Items.Single(i => i.Type == FinancialTransactionType.OpeningBalance);
        Assert.Null(opening.FundingSource);
        Assert.Null(opening.ReversedByTransactionId);
        // Review fix: funding and its reversal are capital movements, kept out of the business money-in/out totals (as in the cash-flow report).
        Assert.Equal((0m, 0m), (list.MoneyIn, list.MoneyOut));
    }

    // ---------------------------------------------------------------- reports

    [Fact]
    public async Task CashFlow_FundingHasItsOwnLines_NeverReceivedFromCustomers_AndReversalsNet()
    {
        var f = await SeedAsync();
        var till = await Account(f, "Till", opening: 500m);
        var bank = await Account(f, "HDFC", FinancialAccountKind.Bank);
        await f.Accounts.FundAsync(Fund(till.Id, FundingSource.OwnerCapital, 1000m, Today), f.UserId);
        var loan = await f.Accounts.FundAsync(Fund(till.Id, FundingSource.Loan, 400m, Today), f.UserId);
        await f.Accounts.FundAsync(Fund(till.Id, FundingSource.CustomerReceipt, 70m, Today, reference: "R1", customer: f.Customer.Id), f.UserId);
        await f.Accounts.ReverseAsync(new(loan, "wrong lender"), f.UserId);
        await f.Accounts.TransferAsync(new(till.Id, bank.Id, 100m, Today, null), f.UserId);
        var transfer = await f.Db.FinancialTransactions.Where(t => t.Type == FinancialTransactionType.Transfer).Select(t => t.Id).FirstAsync();
        await f.Accounts.ReverseAsync(new(transfer, "wrong bank"), f.UserId);

        var report = Ok(await f.Reports.CashFlow(null, null, default));

        Assert.Equal(0m, report.ReceivedFromCustomers);
        Assert.Equal((0m, 0m, 0m), (report.MoneyIn, report.MoneyOut, report.Net));
        Assert.Equal(1070m, report.FundingTotal);
        Assert.Equal(
            new[] { (FundingSource.OwnerCapital, 1000m), (FundingSource.Loan, 0m), (FundingSource.CustomerReceipt, 70m) }.OrderBy(x => x.Item1),
            report.FundingBySource.Select(x => (x.Source, x.Amount)).OrderBy(x => x.Source));
        Assert.Equal((0m, 0m), (report.TransfersIn, report.TransfersOut)); // the reversed transfer nets to nothing
        Assert.Empty(report.Months);

        var empty = Ok(await f.Reports.CashFlow(new DateOnly(2000, 1, 1), new DateOnly(2000, 1, 2), default));
        Assert.Empty(empty.FundingBySource);
        Assert.Equal(0m, empty.FundingTotal);
    }

    [Fact]
    public async Task CashBankAndPaymentMethodReports_HandleFundingAndReversals()
    {
        var f = await SeedAsync();
        var till = await Account(f, "Till", opening: 100m);
        var bank = await Account(f, "HDFC", FinancialAccountKind.Bank);
        await f.Accounts.FundAsync(Fund(till.Id, amount: 50m, date: Today), f.UserId);
        await f.Accounts.TransferAsync(new(till.Id, bank.Id, 60m, Today, null), f.UserId);
        var leg = await f.Db.FinancialTransactions.Where(t => t.Type == FinancialTransactionType.Transfer).Select(t => t.Id).FirstAsync();
        await f.Accounts.ReverseAsync(new(leg, "oops"), f.UserId);

        var report = Ok(await f.Reports.CashBank(null, null, default));
        var tillRow = report.Items.Single(r => r.AccountName == "Till");
        var bankRow = report.Items.Single(r => r.AccountName == "HDFC");

        Assert.Equal(150m, tillRow.Closing);
        Assert.Equal((0m, 0m), (bankRow.Closing, bankRow.MoneyIn));
        Assert.Equal(tillRow.Opening + tillRow.MoneyIn - tillRow.MoneyOut + tillRow.TransfersIn - tillRow.TransfersOut, tillRow.Closing);
        Assert.Equal(bankRow.Opening + bankRow.MoneyIn - bankRow.MoneyOut + bankRow.TransfersIn - bankRow.TransfersOut, bankRow.Closing);
        Assert.Equal((0m, 0m), (tillRow.TransfersIn, tillRow.TransfersOut));

        var methods = Ok(await f.Reports.PaymentMethods(null, null, default));
        Assert.Empty(methods.Items); // funding and reversals have no payment method
    }
}
