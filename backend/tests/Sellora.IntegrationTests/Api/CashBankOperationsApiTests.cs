using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// FI Phase 07 (FI-D19..FI-D22, FI-D11/12) over the real pipeline and PostgreSQL: funding per source and its validation, overdraft limits, concurrent
/// transfers / reversals / idempotent replays under the account row locks, transfer atomicity, reversal rules, the source-account rule with a real
/// <c>X-Branch-Id</c>, the account statement, "no fictitious revenue" and audit events.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CashBankOperationsApiTests(PostgresFixture postgres)
{
    private const string Accounts = "/api/v1/financial-accounts";
    private const string Transactions = "/api/v1/financial-transactions";
    private static DateOnly Day(int daysAgo) => Today.AddDays(-daysAgo);

    // ---- helpers ---------------------------------------------------------------------------------------------------------------

    private static async Task<(RestrictedUserWorld R, BranchApiWorld W)> WorldAsync(PostgresFixture postgres)
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        return (r, r.World);
    }

    private static Task<HttpResponseMessage> SendAsync(BranchApiWorld w, HttpMethod method, string route, object? body = null, HttpClient? client = null, Guid? branch = null, string? key = null)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: ApiHost.Json);
        if (branch is { } b) request.Headers.Add("X-Branch-Id", b.ToString());
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return (client ?? w.Admin).SendAsync(request);
    }

    private static string NewKey() => Guid.NewGuid().ToString("N");

    private static async Task<Guid> NewAccountAsync(BranchApiWorld w, FinancialAccountKind kind = FinancialAccountKind.Bank, decimal opening = 0m, decimal overdraft = 0m, Guid? branch = null, DateOnly? openingDate = null)
    {
        var response = await SendAsync(w, HttpMethod.Post, Accounts, new CreateFinancialAccountRequest("CB " + Guid.NewGuid().ToString("N")[..12], kind, null, opening, openingDate, branch, overdraft));
        Check(response, "create account");
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<decimal> BalanceAsync(BranchApiWorld w, Guid account, Guid? branch = null) =>
        (await RestrictedUserWorld.JsonOkAsync(await SendAsync(w, HttpMethod.Get, $"{Accounts}/{account}", branch: branch))).GetProperty("balance").GetDecimal();

    private static FundingRequest Funding(FundingSource source, decimal amount = 100m, DateOnly? date = null, string? reference = null, string? counterparty = null,
        Guid? customer = null, string? reason = null) => new(source, amount, date ?? Today, reference, counterparty, customer, reason, null);

    private static TransferRequest Transfer(Guid from, Guid to, decimal amount, DateOnly? date = null) => new(from, to, amount, date ?? Today, null);

    private static Task<HttpResponseMessage> FundAsync(BranchApiWorld w, Guid account, FundingRequest body, HttpClient? client = null, Guid? branch = null, string? key = null) =>
        SendAsync(w, HttpMethod.Post, $"{Accounts}/{account}/fundings", body, client, branch, key);

    private static Task<HttpResponseMessage> TransferAsync(BranchApiWorld w, Guid from, Guid to, decimal amount, HttpClient? client = null, Guid? branch = null, string? key = null, DateOnly? date = null) =>
        SendAsync(w, HttpMethod.Post, $"{Accounts}/transfers", Transfer(from, to, amount, date), client, branch, key);

    private static Task<HttpResponseMessage> ReverseAsync(BranchApiWorld w, Guid transaction, string reason = "posted in error", HttpClient? client = null, string? key = null) =>
        SendAsync(w, HttpMethod.Post, $"{Transactions}/{transaction}/reverse", new ReverseTransactionRequest(reason), client, null, key);

    private static async Task<Guid> FundedAsync(BranchApiWorld w, Guid account, decimal amount = 100m, FundingSource source = FundingSource.OwnerCapital)
    {
        var response = await FundAsync(w, account, Funding(source, amount));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("transactionId").GetGuid();
    }

    private static async Task<List<JsonElement>> LedgerAsync(BranchApiWorld w, Guid account, HttpClient? client = null) =>
        (await RestrictedUserWorld.JsonOkAsync(await SendAsync(w, HttpMethod.Get, $"{Transactions}?accountId={account}&pageSize=100", client: client)))
            .GetProperty("items").EnumerateArray().ToList();

    private static async Task<JsonElement> LedgerRowAsync(BranchApiWorld w, Guid account, Guid id) =>
        (await LedgerAsync(w, account)).Single(t => t.GetProperty("id").GetGuid() == id);

    private static async Task<Guid> OpeningTxAsync(BranchApiWorld w, Guid account) =>
        (await LedgerAsync(w, account)).Single(t => t.GetProperty("type").GetString() == "OpeningBalance").GetProperty("id").GetGuid();

    private static async Task<List<string>> FieldsAsync(HttpResponseMessage response) => await ErrorFieldsAsync(response);

    private static async Task AssertBadAsync(HttpResponseMessage response, string field)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400 on '{field}', got {(int)response.StatusCode} {body}");
        using var doc = JsonDocument.Parse(body);
        var fields = doc.RootElement.TryGetProperty("errors", out var e) ? e.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList() : [];
        Assert.Contains(field.ToLowerInvariant(), fields);
    }

    private static Task<long> CountAsync(BranchApiWorld w, string sql) => ScalarAsync<long>(w.Host.Database, sql);

    private static async Task<RestrictedUser> MemberAsync(RestrictedUserWorld r, params Guid[] branches) => await r.CreateAtAsync(branches);

    // ---- 1. funding per source --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(FundingSource.OwnerCapital)]
    [InlineData(FundingSource.Loan)]
    [InlineData(FundingSource.CashDeposit)]
    [InlineData(FundingSource.Other)]
    [InlineData(FundingSource.CustomerReceipt)]
    public async Task Fund_EachSource_Returns201_AndWritesTheLedgerRow(FundingSource source)
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w, FinancialAccountKind.Bank, 10m);
        var request = Funding(source, 125.5m, Day(1), reference: "REF-" + source, counterparty: "Acme Finance",
            customer: source == FundingSource.CustomerReceipt ? w.CustomerId : null, reason: source == FundingSource.Other ? "Insurance payout" : null);

        var response = await FundAsync(w, account, request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await JsonAsync(response)).GetProperty("transactionId").GetGuid();
        var row = await LedgerRowAsync(w, account, id);
        Assert.Equal("Funding", row.GetProperty("type").GetString());
        Assert.Equal("In", row.GetProperty("direction").GetString());
        Assert.Equal("Active", row.GetProperty("status").GetString());
        Assert.Equal(source.ToString(), row.GetProperty("fundingSource").GetString());
        Assert.Equal(125.5m, row.GetProperty("amount").GetDecimal());
        Assert.Equal(125.5m, row.GetProperty("baseAmount").GetDecimal());
        Assert.Equal("REF-" + source, row.GetProperty("sourceReference").GetString());
        Assert.Equal("Acme Finance", row.GetProperty("counterparty").GetString());
        Assert.Equal(Day(1).ToString("yyyy-MM-dd"), row.GetProperty("transactionDate").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("reversalOfTransactionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("reversedByTransactionId").ValueKind);
        if (source == FundingSource.Other) Assert.Equal("Insurance payout", row.GetProperty("reason").GetString());
        Assert.Equal(135.5m, await BalanceAsync(w, account));
    }

    [Fact]
    public async Task Fund_CustomerReceipt_WithoutCustomerOrReference_Returns400WithBothKeys()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w);

        await AssertBadAsync(await FundAsync(w, account, Funding(FundingSource.CustomerReceipt, reference: "R-1")), "customerId");
        await AssertBadAsync(await FundAsync(w, account, Funding(FundingSource.CustomerReceipt, customer: w.CustomerId)), "sourceReference");
        Assert.Equal(0m, await BalanceAsync(w, account));
    }

    [Fact]
    public async Task Fund_CustomerOfAnotherCompany_Returns400CustomerId()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w);
        var foreignCustomer = await ForeignCustomerAsync(w);

        var response = await FundAsync(w, account, Funding(FundingSource.CustomerReceipt, reference: "R-2", customer: foreignCustomer));

        await AssertBadAsync(response, "customerId");
        Assert.Equal(0L, await CountAsync(w, $"""SELECT COUNT(*) FROM "FinancialTransactions" WHERE "AccountId" = '{account}'"""));
    }

    [Fact]
    public async Task Fund_NonReceiptSourceNamingACustomer_Returns400CustomerId()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w);

        await AssertBadAsync(await FundAsync(w, account, Funding(FundingSource.Loan, customer: w.CustomerId)), "customerId");
    }

    [Fact]
    public async Task Fund_OtherWithoutReason_Returns400Reason()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w);

        await AssertBadAsync(await FundAsync(w, account, Funding(FundingSource.Other)), "reason");
        await AssertBadAsync(await FundAsync(w, account, Funding(FundingSource.Other, reason: "   ")), "reason");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Fund_AmountZeroOrNegative_Returns400Amount(int amount)
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w);

        await AssertBadAsync(await FundAsync(w, account, Funding(FundingSource.Loan, amount)), "amount");
    }

    [Fact]
    public async Task Fund_FutureDate_Returns400FundingDate()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w);

        await AssertBadAsync(await FundAsync(w, account, Funding(FundingSource.Loan, date: Today.AddDays(5))), "fundingDate");
        Assert.Equal(0m, await BalanceAsync(w, account));
    }

    [Fact]
    public async Task Fund_InactiveAccount_Returns400AccountId()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(w, HttpMethod.Post, $"{Accounts}/{account}/status", new SetActiveRequest(false))).StatusCode);

        await AssertBadAsync(await FundAsync(w, account, Funding(FundingSource.Loan)), "accountId");
    }

    [Fact]
    public async Task Fund_UnknownAccount_Returns400AccountId()
    {
        var (_, w) = await WorldAsync(postgres);

        await AssertBadAsync(await FundAsync(w, Guid.NewGuid(), Funding(FundingSource.Loan)), "accountId");
    }

    [Fact]
    public async Task Fund_WithoutCashBankFund_Returns403_AndNothingIsPosted()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w);
        var limited = await w.Host.LimitedClientAsync(w.Admin, "CashBank.View", "CashBank.Transfer", "CashBank.Manage", "CashBank.Reverse");

        var response = await FundAsync(w, account, Funding(FundingSource.Loan), limited);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0m, await BalanceAsync(w, account));
    }

    // ---- 2. overdraft -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Transfer_FromCash_CannotGoBelowZero_ButMayDrainToZero()
    {
        var (_, w) = await WorldAsync(postgres);
        var cash = await NewAccountAsync(w, FinancialAccountKind.Cash, 100m);
        var bank = await NewAccountAsync(w);

        await AssertBadAsync(await TransferAsync(w, cash, bank, 100.01m), "amount");
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, cash, bank, 100m)).StatusCode);

        Assert.Equal(0m, await BalanceAsync(w, cash));
        Assert.Equal(100m, await BalanceAsync(w, bank));
        await AssertBadAsync(await TransferAsync(w, cash, bank, 0.01m), "amount");
    }

    [Fact]
    public async Task Transfer_FromBank_MayGoDownToMinusOverdraftLimit_AndNoFurther()
    {
        var (_, w) = await WorldAsync(postgres);
        var bank = await NewAccountAsync(w, FinancialAccountKind.Bank, 100m, overdraft: 50m);
        var other = await NewAccountAsync(w);

        await AssertBadAsync(await TransferAsync(w, bank, other, 150.01m), "amount");
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, bank, other, 150m)).StatusCode);
        Assert.Equal(-50m, await BalanceAsync(w, bank));
        await AssertBadAsync(await TransferAsync(w, bank, other, 0.01m), "amount");
        Assert.Equal(150m, await BalanceAsync(w, other));
    }

    [Fact]
    public async Task Transfer_FromBankWithNoOverdraftLimit_CannotGoBelowZero()
    {
        var (_, w) = await WorldAsync(postgres);
        var bank = await NewAccountAsync(w, FinancialAccountKind.Bank, 20m);
        var other = await NewAccountAsync(w);

        await AssertBadAsync(await TransferAsync(w, bank, other, 20.01m), "amount");
    }

    [Fact]
    public async Task CreateAndUpdate_CashAccountWithOverdraftLimit_Returns400OverdraftLimit()
    {
        var (_, w) = await WorldAsync(postgres);

        var create = await SendAsync(w, HttpMethod.Post, Accounts, new CreateFinancialAccountRequest("CB " + NewKey()[..10], FinancialAccountKind.Cash, null, 0m, null, null, 25m));
        await AssertBadAsync(create, "overdraftLimit");

        var cash = await NewAccountAsync(w, FinancialAccountKind.Cash);
        var update = await SendAsync(w, HttpMethod.Put, $"{Accounts}/{cash}", new UpdateFinancialAccountRequest("CB " + NewKey()[..10], null, null, 25m));
        await AssertBadAsync(update, "overdraftLimit");
    }

    [Fact]
    public async Task CreateAndUpdate_BankAccountOverdraftLimit_IsStoredReturned_AndNegativeIsRejected()
    {
        var (_, w) = await WorldAsync(postgres);
        var bank = await NewAccountAsync(w, FinancialAccountKind.Bank, overdraft: 75m);
        Assert.Equal(75m, (await RestrictedUserWorld.JsonOkAsync(await SendAsync(w, HttpMethod.Get, $"{Accounts}/{bank}"))).GetProperty("overdraftLimit").GetDecimal());

        var update = await SendAsync(w, HttpMethod.Put, $"{Accounts}/{bank}", new UpdateFinancialAccountRequest("CB " + NewKey()[..10], null, null, 200m));
        Assert.Equal(200m, (await RestrictedUserWorld.JsonOkAsync(update)).GetProperty("overdraftLimit").GetDecimal());

        await AssertBadAsync(await SendAsync(w, HttpMethod.Put, $"{Accounts}/{bank}", new UpdateFinancialAccountRequest("CB " + NewKey()[..10], null, null, -1m)), "overdraftLimit");
    }

    // ---- 3. concurrency ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentTransfers_DrainingOneBankAccount_NeverExceedBalancePlusLimit()
    {
        var (_, w) = await WorldAsync(postgres);
        var source = await NewAccountAsync(w, FinancialAccountKind.Bank, 100m, overdraft: 50m);
        var destination = await NewAccountAsync(w);

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => TransferAsync(w, source, destination, 40m)));

        var ok = responses.Count(r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.BadRequest }));
        Assert.True(ok * 40m <= 100m + 50m, $"{ok} transfers of 40 exceed balance + limit");
        Assert.Equal(3, ok); // 3 x 40 = 120 <= 150 < 160
        Assert.Equal(100m - ok * 40m, await BalanceAsync(w, source));
        Assert.True(await BalanceAsync(w, source) >= -50m);
        Assert.Equal(ok * 40m, await BalanceAsync(w, destination));
    }

    [Fact]
    public async Task ConcurrentTransfers_DrainingOneCashAccount_NeverGoNegative()
    {
        var (_, w) = await WorldAsync(postgres);
        var cash = await NewAccountAsync(w, FinancialAccountKind.Cash, 100m);
        var destination = await NewAccountAsync(w);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => TransferAsync(w, cash, destination, 30m)));

        var ok = responses.Count(r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.BadRequest }));
        Assert.Equal(3, ok);
        Assert.Equal(10m, await BalanceAsync(w, cash));
    }

    [Fact]
    public async Task ConcurrentOppositeTransfers_BetweenTwoAccounts_DoNotDeadlock_AndConserveMoney()
    {
        var (_, w) = await WorldAsync(postgres);
        var a = await NewAccountAsync(w, FinancialAccountKind.Bank, 500m);
        var b = await NewAccountAsync(w, FinancialAccountKind.Bank, 500m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? TransferAsync(w, a, b, 10m) : TransferAsync(w, b, a, 10m)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal(1000m, await BalanceAsync(w, a) + await BalanceAsync(w, b));
        Assert.Equal(500m, await BalanceAsync(w, a)); // 10 each way
    }

    [Fact]
    public async Task ConcurrentDuplicateReversals_OfAFunding_ExactlyOneIs201()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w);
        var funding = await FundedAsync(w, account, 100m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ReverseAsync(w, funding)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var rejected in responses.Where(r => r.StatusCode != HttpStatusCode.Created)) await AssertBadAsync(rejected, "id");
        Assert.Equal(1L, await CountAsync(w, $"""SELECT COUNT(*) FROM "FinancialTransactions" WHERE "ReversalOfTransactionId" = '{funding}'"""));
        Assert.Equal(0m, await BalanceAsync(w, account));
    }

    [Fact]
    public async Task ConcurrentDuplicateReversals_OfATransfer_ExactlyOneReversesBothLegs()
    {
        var (_, w) = await WorldAsync(postgres);
        var from = await NewAccountAsync(w, FinancialAccountKind.Bank, 200m);
        var to = await NewAccountAsync(w);
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, from, to, 80m)).StatusCode);
        var legs = (await LedgerAsync(w, from)).Concat(await LedgerAsync(w, to)).Where(t => t.GetProperty("type").GetString() == "Transfer").Select(t => t.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(2, legs.Count);

        // Half of the requests name the out leg, half the in leg: the two accounts are locked in id order, so no deadlock and one winner.
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => ReverseAsync(w, legs[i % 2])));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal(2L, await CountAsync(w, $"""SELECT COUNT(*) FROM "FinancialTransactions" WHERE "Type" = 'Reversal' AND "ReversalOfTransactionId" IN ('{legs[0]}','{legs[1]}')"""));
        Assert.Equal(200m, await BalanceAsync(w, from));
        Assert.Equal(0m, await BalanceAsync(w, to));
    }

    [Fact]
    public async Task ConcurrentSameIdempotencyKey_OnFunding_PostsOnce_AndEveryReplayReturnsTheSameTransaction()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w);
        var key = NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => FundAsync(w, account, Funding(FundingSource.Loan, 60m), key: key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = (await Task.WhenAll(responses.Select(async r => (await JsonAsync(r)).GetProperty("transactionId").GetGuid()))).Distinct().ToList();
        Assert.Single(ids);
        Assert.Equal(60m, await BalanceAsync(w, account));
        var replay = await FundAsync(w, account, Funding(FundingSource.Loan, 60m), key: key);
        Assert.Equal(ids[0], (await JsonAsync(replay)).GetProperty("transactionId").GetGuid());
        Assert.Equal(60m, await BalanceAsync(w, account));
        Assert.Equal(1L, await CountAsync(w, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'cash.funded' AND "TargetId" = '{ids[0]}'"""));
    }

    [Fact]
    public async Task ConcurrentSameIdempotencyKey_OnTransfer_PostsOnce()
    {
        var (_, w) = await WorldAsync(postgres);
        var from = await NewAccountAsync(w, FinancialAccountKind.Bank, 500m);
        var to = await NewAccountAsync(w);
        var key = NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => TransferAsync(w, from, to, 70m, key: key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal(430m, await BalanceAsync(w, from));
        Assert.Equal(70m, await BalanceAsync(w, to));
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, from, to, 70m, key: key)).StatusCode); // sequential replay too
        Assert.Equal(430m, await BalanceAsync(w, from));
        Assert.Single(await LedgerAsync(w, from), t => t.GetProperty("type").GetString() == "Transfer");
    }

    [Fact]
    public async Task ConcurrentSameIdempotencyKey_OnReverse_PostsOnce_AndReplaysReturnTheSameIds()
    {
        var (_, w) = await WorldAsync(postgres);
        var from = await NewAccountAsync(w, FinancialAccountKind.Bank, 300m);
        var to = await NewAccountAsync(w);
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, from, to, 120m)).StatusCode);
        var leg = (await LedgerAsync(w, from)).Single(t => t.GetProperty("type").GetString() == "Transfer").GetProperty("id").GetGuid();
        var key = NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ReverseAsync(w, leg, key: key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var results = await Task.WhenAll(responses.Select(async r => string.Join(",", (await JsonAsync(r)).GetProperty("reversalTransactionIds").EnumerateArray().Select(x => x.GetGuid()).Order())));
        Assert.Single(results.Distinct());
        Assert.Equal(2, results[0].Split(',').Length);
        Assert.Equal(300m, await BalanceAsync(w, from));
        Assert.Equal(0m, await BalanceAsync(w, to));
        Assert.Equal(2L, await CountAsync(w, $"""SELECT COUNT(*) FROM "FinancialTransactions" WHERE "Type" = 'Reversal' AND "AccountId" IN ('{from}','{to}')"""));
        var replay = await ReverseAsync(w, leg, key: key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(results[0], string.Join(",", (await JsonAsync(replay)).GetProperty("reversalTransactionIds").EnumerateArray().Select(x => x.GetGuid()).Order()));
    }

    [Fact]
    public async Task SameIdempotencyKey_OnDifferentOperations_DoesNotCollide()
    {
        var (_, w) = await WorldAsync(postgres);
        var from = await NewAccountAsync(w, FinancialAccountKind.Bank, 100m);
        var to = await NewAccountAsync(w);
        var key = NewKey();

        var fund = await FundAsync(w, to, Funding(FundingSource.Loan, 10m), key: key);
        var transfer = await TransferAsync(w, from, to, 10m, key: key);

        Assert.Equal(HttpStatusCode.Created, fund.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, transfer.StatusCode);
        Assert.Equal(20m, await BalanceAsync(w, to));
    }

    // ---- 4. transfer atomicity --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Transfer_Succeeds_WithBothLegsInOneGroup_AndBothBalancesMoveByTheAmount()
    {
        var (_, w) = await WorldAsync(postgres);
        var from = await NewAccountAsync(w, FinancialAccountKind.Bank, 300m);
        var to = await NewAccountAsync(w, FinancialAccountKind.Cash, 25m);

        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, from, to, 120.25m)).StatusCode);

        Assert.Equal(179.75m, await BalanceAsync(w, from));
        Assert.Equal(145.25m, await BalanceAsync(w, to));
        Assert.Equal(1L, await CountAsync(w, $"""
            SELECT COUNT(*) FROM (SELECT "TransferGroupId" FROM "FinancialTransactions" WHERE "AccountId" IN ('{from}','{to}') AND "Type" = 'Transfer'
              GROUP BY "TransferGroupId" HAVING COUNT(*) = 2 AND SUM(CASE WHEN "Direction" = 'Out' THEN 1 ELSE 0 END) = 1) g
            """));
    }

    [Theory]
    [InlineData("overdraw")]
    [InlineData("inactive-destination")]
    [InlineData("same-account")]
    [InlineData("future-date")]
    [InlineData("unknown-destination")]
    public async Task Transfer_Refused_LeavesNeitherLegBehind_AndWritesNoAudit(string scenario)
    {
        var (_, w) = await WorldAsync(postgres);
        var from = await NewAccountAsync(w, FinancialAccountKind.Bank, 100m);
        var to = await NewAccountAsync(w);
        if (scenario == "inactive-destination") Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(w, HttpMethod.Post, $"{Accounts}/{to}/status", new SetActiveRequest(false))).StatusCode);
        var auditBefore = await CountAsync(w, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'cash.transferred'""");

        var response = scenario switch
        {
            "overdraw" => await TransferAsync(w, from, to, 100.01m),
            "same-account" => await TransferAsync(w, from, from, 10m),
            "future-date" => await TransferAsync(w, from, to, 10m, date: Today.AddDays(5)),
            "unknown-destination" => await TransferAsync(w, from, Guid.NewGuid(), 10m),
            _ => await TransferAsync(w, from, to, 10m),
        };

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(100m, await BalanceAsync(w, from));
        Assert.Equal(0L, await CountAsync(w, $"""SELECT COUNT(*) FROM "FinancialTransactions" WHERE "AccountId" = '{from}' AND "Type" = 'Transfer'"""));
        Assert.Equal(0L, await CountAsync(w, $"""SELECT COUNT(*) FROM "FinancialTransactions" WHERE "AccountId" = '{to}'"""));
        Assert.Equal(auditBefore, await CountAsync(w, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'cash.transferred'"""));
    }

    [Fact]
    public async Task Transfer_ValidationKeys_KeepTheirExistingCasing()
    {
        var (_, w) = await WorldAsync(postgres);
        var from = await NewAccountAsync(w, FinancialAccountKind.Bank, 100m);

        var same = await FieldsAsync(await TransferAsync(w, from, from, 10m));
        var zero = await FieldsAsync(await TransferAsync(w, from, await NewAccountAsync(w), 0m));

        Assert.Contains("toaccountid", same);
        Assert.Contains("amount", zero);
    }

    // ---- 5. reversal ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reverse_Funding_PostsOneOppositeEntry_LinkedBoth_OriginalStaysActive()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w, FinancialAccountKind.Bank, 50m);
        var funding = await FundedAsync(w, account, 100m, FundingSource.Loan);

        var response = await ReverseAsync(w, funding, "wrong account");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var reversalIds = (await JsonAsync(response)).GetProperty("reversalTransactionIds").EnumerateArray().Select(x => x.GetGuid()).ToList();
        Assert.Single(reversalIds);
        var original = await LedgerRowAsync(w, account, funding);
        var reversal = await LedgerRowAsync(w, account, reversalIds[0]);
        Assert.Equal("Active", original.GetProperty("status").GetString());
        Assert.Equal(reversalIds[0], original.GetProperty("reversedByTransactionId").GetGuid());
        Assert.Equal("Reversal", reversal.GetProperty("type").GetString());
        Assert.Equal("Out", reversal.GetProperty("direction").GetString());
        Assert.Equal(100m, reversal.GetProperty("baseAmount").GetDecimal());
        Assert.Equal(funding, reversal.GetProperty("reversalOfTransactionId").GetGuid());
        Assert.Equal("wrong account", reversal.GetProperty("reason").GetString());
        Assert.Equal(50m, await BalanceAsync(w, account));
    }

    [Fact]
    public async Task Reverse_OpeningBalance_RemovesItFromTheBalance()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w, FinancialAccountKind.Cash, 500m);

        var response = await ReverseAsync(w, await OpeningTxAsync(w, account), "entered twice");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0m, await BalanceAsync(w, account));
    }

    [Fact]
    public async Task Reverse_Transfer_ByEitherLeg_PostsBothLegsAndRestoresBothBalances()
    {
        var (_, w) = await WorldAsync(postgres);
        var from = await NewAccountAsync(w, FinancialAccountKind.Bank, 200m);
        var to = await NewAccountAsync(w, FinancialAccountKind.Cash);
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, from, to, 75m)).StatusCode);
        var inLeg = (await LedgerAsync(w, to)).Single(t => t.GetProperty("type").GetString() == "Transfer").GetProperty("id").GetGuid();

        var response = await ReverseAsync(w, inLeg, "mistaken transfer");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, (await JsonAsync(response)).GetProperty("reversalTransactionIds").GetArrayLength());
        Assert.Equal(200m, await BalanceAsync(w, from));
        Assert.Equal(0m, await BalanceAsync(w, to));
        Assert.Equal(2L, await CountAsync(w, $"""SELECT COUNT(*) FROM "FinancialTransactions" WHERE "Type" = 'Reversal' AND "AccountId" IN ('{from}','{to}') AND "Status" = 'Active'"""));
        Assert.Equal(0L, await CountAsync(w, $"""SELECT COUNT(*) FROM "FinancialTransactions" WHERE "AccountId" IN ('{from}','{to}') AND "Type" = 'Transfer' AND "Status" <> 'Active'"""));
    }

    [Fact]
    public async Task Reverse_AlreadyReversed_Returns400Id_ForFundingAndForTheOtherLegOfATransfer()
    {
        var (_, w) = await WorldAsync(postgres);
        var from = await NewAccountAsync(w, FinancialAccountKind.Bank, 200m);
        var to = await NewAccountAsync(w);
        var funding = await FundedAsync(w, to, 40m);
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, from, to, 60m)).StatusCode);
        var outLeg = (await LedgerAsync(w, from)).Single(t => t.GetProperty("type").GetString() == "Transfer").GetProperty("id").GetGuid();
        var inLeg = (await LedgerAsync(w, to)).Single(t => t.GetProperty("type").GetString() == "Transfer").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Created, (await ReverseAsync(w, funding)).StatusCode);
        await AssertBadAsync(await ReverseAsync(w, funding), "id");
        Assert.Equal(HttpStatusCode.Created, (await ReverseAsync(w, outLeg)).StatusCode);
        await AssertBadAsync(await ReverseAsync(w, inLeg), "id");
        await AssertBadAsync(await ReverseAsync(w, outLeg), "id");
        Assert.Equal(200m, await BalanceAsync(w, from));
        Assert.Equal(0m, await BalanceAsync(w, to));
    }

    [Fact]
    public async Task Reverse_ReversalEntryItself_Returns400Id()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w);
        var funding = await FundedAsync(w, account, 30m);
        var reversal = (await JsonAsync(await ReverseAsync(w, funding))).GetProperty("reversalTransactionIds")[0].GetGuid();

        await AssertBadAsync(await ReverseAsync(w, reversal), "id");
        Assert.Equal(0m, await BalanceAsync(w, account));
    }

    [Fact]
    public async Task Reverse_ReconciledEntry_Returns400Id_UntilItIsUnreconciled()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w);
        var funding = await FundedAsync(w, account, 90m);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(w, HttpMethod.Post, $"{Accounts}/{account}/reconcile", new ReconcileRequest([funding]))).StatusCode);

        await AssertBadAsync(await ReverseAsync(w, funding), "id");
        Assert.Equal(90m, await BalanceAsync(w, account));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(w, HttpMethod.Post, $"{Accounts}/{account}/unreconcile", new ReconcileRequest([funding]))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ReverseAsync(w, funding)).StatusCode);
    }

    [Fact]
    public async Task Reverse_TransferWithOneReconciledLeg_Returns400Id_AndNeitherLegIsReversed()
    {
        var (_, w) = await WorldAsync(postgres);
        var from = await NewAccountAsync(w, FinancialAccountKind.Bank, 100m);
        var to = await NewAccountAsync(w);
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, from, to, 40m)).StatusCode);
        var inLeg = (await LedgerAsync(w, to)).Single(t => t.GetProperty("type").GetString() == "Transfer").GetProperty("id").GetGuid();
        var outLeg = (await LedgerAsync(w, from)).Single(t => t.GetProperty("type").GetString() == "Transfer").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(w, HttpMethod.Post, $"{Accounts}/{to}/reconcile", new ReconcileRequest([inLeg]))).StatusCode);

        await AssertBadAsync(await ReverseAsync(w, outLeg), "id");

        Assert.Equal(60m, await BalanceAsync(w, from));
        Assert.Equal(40m, await BalanceAsync(w, to));
    }

    [Fact]
    public async Task Reverse_PaymentSourcedEntry_Returns400Id()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w, FinancialAccountKind.Bank);
        var sale = await w.SaleAsync(null, qty: 1m, paid: 5m, account: account);
        Check(sale, "sale with payment");
        var payment = (await LedgerAsync(w, account)).Single(t => t.GetProperty("type").GetString() == "CustomerPayment").GetProperty("id").GetGuid();

        await AssertBadAsync(await ReverseAsync(w, payment), "id");
        Assert.Equal(5m, await BalanceAsync(w, account));
    }

    [Fact]
    public async Task Reverse_AnotherCompanysTransaction_IsNotFound_AndUnknownIdIsNotFound()
    {
        var (_, w) = await WorldAsync(postgres);
        var foreign = await ForeignTransactionAsync(w);

        Assert.Equal(HttpStatusCode.NotFound, (await ReverseAsync(w, foreign)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ReverseAsync(w, Guid.NewGuid())).StatusCode);
        Assert.Equal(0L, await CountAsync(w, $"""SELECT COUNT(*) FROM "FinancialTransactions" WHERE "ReversalOfTransactionId" = '{foreign}'"""));
    }

    [Fact]
    public async Task Reverse_WithoutCashBankReverse_Returns403_AndWithoutReason_Returns400()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w);
        var funding = await FundedAsync(w, account, 20m);
        var limited = await w.Host.LimitedClientAsync(w.Admin, "CashBank.View", "CashBank.Fund", "CashBank.Transfer", "CashBank.Manage", "CashBank.Reconcile");

        Assert.Equal(HttpStatusCode.Forbidden, (await ReverseAsync(w, funding, client: limited)).StatusCode);
        Assert.Equal(20m, await BalanceAsync(w, account));
        await AssertBadAsync(await ReverseAsync(w, funding, reason: "   "), "reason");
        Assert.Equal(0L, await CountAsync(w, $"""SELECT COUNT(*) FROM "FinancialTransactions" WHERE "ReversalOfTransactionId" = '{funding}'"""));
    }

    [Fact]
    public async Task Reverse_ThatWouldOverdrawTheAccount_Returns400Amount_AndLeavesEverythingUnchanged()
    {
        var (_, w) = await WorldAsync(postgres);
        var from = await NewAccountAsync(w, FinancialAccountKind.Bank, 100m);
        var to = await NewAccountAsync(w, FinancialAccountKind.Cash);
        var other = await NewAccountAsync(w, FinancialAccountKind.Bank);
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, from, to, 100m)).StatusCode);
        var leg = (await LedgerAsync(w, from)).Single(t => t.GetProperty("type").GetString() == "Transfer").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, to, other, 60m)).StatusCode); // the destination has spent part of it

        await AssertBadAsync(await ReverseAsync(w, leg), "amount");

        Assert.Equal(0m, await BalanceAsync(w, from));
        Assert.Equal(40m, await BalanceAsync(w, to));
        Assert.Equal(0L, await CountAsync(w, $"""SELECT COUNT(*) FROM "FinancialTransactions" WHERE "Type" = 'Reversal' AND "AccountId" IN ('{from}','{to}')"""));
    }

    [Fact]
    public async Task Reverse_FundingAlreadySpentOnACashAccount_Returns400Amount_ButABankOverdraftLimitAllowsIt()
    {
        var (_, w) = await WorldAsync(postgres);
        var cash = await NewAccountAsync(w, FinancialAccountKind.Cash);
        var bank = await NewAccountAsync(w, FinancialAccountKind.Bank, overdraft: 100m);
        var sink = await NewAccountAsync(w);
        var cashFunding = await FundedAsync(w, cash, 50m);
        var bankFunding = await FundedAsync(w, bank, 50m);
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, cash, sink, 50m)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, bank, sink, 50m)).StatusCode);

        await AssertBadAsync(await ReverseAsync(w, cashFunding), "amount");
        Assert.Equal(HttpStatusCode.Created, (await ReverseAsync(w, bankFunding)).StatusCode);
        Assert.Equal(-50m, await BalanceAsync(w, bank));
    }

    // ---- 6. FI-D22 source-account rule with a real X-Branch-Id ------------------------------------------------------------------

    private sealed record BranchAccounts(Guid A, Guid B, Guid AccountA, Guid AccountA2, Guid AccountB, Guid Shared);

    private static async Task<BranchAccounts> BranchAccountsAsync(BranchApiWorld w)
    {
        var (a, _) = await w.NewBranchAsync(); var (b, _) = await w.NewBranchAsync();
        return new BranchAccounts(a, b,
            await NewAccountAsync(w, FinancialAccountKind.Bank, 500m, branch: a), await NewAccountAsync(w, FinancialAccountKind.Bank, 0m, branch: a),
            await NewAccountAsync(w, FinancialAccountKind.Bank, 500m, branch: b), await NewAccountAsync(w, FinancialAccountKind.Bank, 500m));
    }

    [Fact]
    public async Task BranchLimitedMember_CannotTransferFromAnotherBranchesAccountOrASharedAccount()
    {
        var (r, w) = await WorldAsync(postgres);
        var x = await BranchAccountsAsync(w);
        var member = await MemberAsync(r, x.A);

        await AssertBadAsync(await TransferAsync(w, x.AccountB, x.AccountA2, 10m, member.Client, x.A), "fromAccountId");
        await AssertBadAsync(await TransferAsync(w, x.Shared, x.AccountA2, 10m, member.Client, x.A), "fromAccountId");

        Assert.Equal(500m, await BalanceAsync(w, x.AccountB, x.B));
        Assert.Equal(500m, await BalanceAsync(w, x.Shared));
        Assert.Equal(0m, await BalanceAsync(w, x.AccountA2, x.A));
    }

    [Fact]
    public async Task BranchLimitedMember_CanTransferFromTheActiveBranchAccount_ToAnotherAccountOfTheBranch_AndToASharedAccount()
    {
        var (r, w) = await WorldAsync(postgres);
        var x = await BranchAccountsAsync(w);
        var member = await MemberAsync(r, x.A);

        var toBranch = await TransferAsync(w, x.AccountA, x.AccountA2, 100m, member.Client, x.A);
        var toShared = await TransferAsync(w, x.AccountA, x.Shared, 50m, member.Client, x.A);

        Assert.Equal(HttpStatusCode.NoContent, toBranch.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, toShared.StatusCode);
        Assert.Equal(350m, await BalanceAsync(w, x.AccountA, x.A));
        Assert.Equal(100m, await BalanceAsync(w, x.AccountA2, x.A));
        Assert.Equal(550m, await BalanceAsync(w, x.Shared));
    }

    [Fact]
    public async Task BranchLimitedMember_CannotFundAnotherBranchesAccountOrASharedAccount_ButCanFundTheActiveBranchs()
    {
        var (r, w) = await WorldAsync(postgres);
        var x = await BranchAccountsAsync(w);
        var member = await MemberAsync(r, x.A);

        await AssertBadAsync(await FundAsync(w, x.AccountB, Funding(FundingSource.OwnerCapital), member.Client, x.A), "accountId");
        await AssertBadAsync(await FundAsync(w, x.Shared, Funding(FundingSource.OwnerCapital), member.Client, x.A), "accountId");
        Assert.Equal(HttpStatusCode.Created, (await FundAsync(w, x.AccountA, Funding(FundingSource.OwnerCapital, 25m), member.Client, x.A)).StatusCode);

        Assert.Equal(500m, await BalanceAsync(w, x.AccountB, x.B));
        Assert.Equal(500m, await BalanceAsync(w, x.Shared));
        Assert.Equal(525m, await BalanceAsync(w, x.AccountA, x.A));
    }

    [Fact]
    public async Task MemberOfTwoBranches_UsesOnlyTheActiveBranchAccountAsSource()
    {
        var (r, w) = await WorldAsync(postgres);
        var x = await BranchAccountsAsync(w);
        var member = await MemberAsync(r, x.A, x.B);

        await AssertBadAsync(await TransferAsync(w, x.AccountB, x.AccountA2, 10m, member.Client, x.A), "fromAccountId");
        await AssertBadAsync(await FundAsync(w, x.AccountB, Funding(FundingSource.Loan), member.Client, x.A), "accountId");
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, x.AccountB, x.AccountA2, 10m, member.Client, x.B)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await FundAsync(w, x.AccountB, Funding(FundingSource.Loan), member.Client, x.B)).StatusCode);
    }

    [Fact]
    public async Task CompanyWideAdmin_CanUseSharedAccounts_AndABranchAccountOnlyWhileThatBranchIsActive()
    {
        var (_, w) = await WorldAsync(postgres);
        var x = await BranchAccountsAsync(w);

        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, x.Shared, x.AccountA2, 10m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await FundAsync(w, x.Shared, Funding(FundingSource.OwnerCapital, 10m))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, x.Shared, x.AccountA2, 10m, branch: x.A)).StatusCode); // a header does not stop shared use by a company-wide member

        await AssertBadAsync(await TransferAsync(w, x.AccountA, x.AccountA2, 10m), "fromAccountId"); // headerless = the default branch
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, x.AccountA, x.AccountA2, 10m, branch: x.A)).StatusCode);
        await AssertBadAsync(await FundAsync(w, x.AccountB, Funding(FundingSource.Loan), branch: x.A), "accountId");
    }

    // ---- 7. statement -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Statement_OpeningRunningAndClosing_AcrossADateRange_IncludingReversals()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w, FinancialAccountKind.Bank, 1000m, openingDate: Day(20));
        var sink = await NewAccountAsync(w, FinancialAccountKind.Bank, 0m, overdraft: 0m);
        Check(await FundAsync(w, account, Funding(FundingSource.OwnerCapital, 200m, Day(10))), "fund d-10");
        Check(await TransferAsync(w, account, sink, 150m, date: Day(5)), "transfer d-5");
        Check(await FundAsync(w, account, Funding(FundingSource.Loan, 50m, Day(2), counterparty: "Bank X")), "fund d-2");
        Check(await TransferAsync(w, account, sink, 100m, date: Day(1)), "transfer d-1");
        var transferLeg = (await LedgerAsync(w, account)).Single(t => t.GetProperty("type").GetString() == "Transfer" && t.GetProperty("transactionDate").GetString() == Day(5).ToString("yyyy-MM-dd")).GetProperty("id").GetGuid();
        var reversal = (await JsonAsync(await ReverseAsync(w, transferLeg))).GetProperty("reversalTransactionIds")[0].GetGuid();

        var statement = await RestrictedUserWorld.JsonOkAsync(await SendAsync(w, HttpMethod.Get, $"{Accounts}/{account}/statement?from={Day(7):yyyy-MM-dd}&to={Today:yyyy-MM-dd}"));

        Assert.Equal(1200m, statement.GetProperty("openingBalance").GetDecimal()); // 1000 + 200 before d-7
        var rows = statement.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(new[] { -150m, 50m, -100m, 150m }, rows.Select(x => x.GetProperty("moneyIn").GetDecimal() - x.GetProperty("moneyOut").GetDecimal()).ToArray());
        Assert.Equal(new[] { 1050m, 1100m, 1000m, 1150m }, rows.Select(x => x.GetProperty("runningBalance").GetDecimal()).ToArray());
        Assert.Equal(1150m, statement.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(200m, statement.GetProperty("totalIn").GetDecimal());
        Assert.Equal(250m, statement.GetProperty("totalOut").GetDecimal());
        Assert.Equal(statement.GetProperty("openingBalance").GetDecimal() + 200m - 250m, statement.GetProperty("closingBalance").GetDecimal());
        var reversalRow = rows[^1];
        Assert.True(reversalRow.GetProperty("isReversal").GetBoolean());
        Assert.Equal(reversal, reversalRow.GetProperty("transactionId").GetGuid());
        Assert.Equal(reversal, rows[0].GetProperty("reversedByTransactionId").GetGuid());
        Assert.Equal("Funding", rows[1].GetProperty("type").GetString());
        Assert.Equal("Loan", rows[1].GetProperty("fundingSource").GetString());

        var full = await RestrictedUserWorld.JsonOkAsync(await SendAsync(w, HttpMethod.Get, $"{Accounts}/{account}/statement?from={Day(30):yyyy-MM-dd}&to={Today:yyyy-MM-dd}"));
        Assert.Equal(0m, full.GetProperty("openingBalance").GetDecimal());
        Assert.Equal(await BalanceAsync(w, account), full.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(1150m, full.GetProperty("closingBalance").GetDecimal());
    }

    [Fact]
    public async Task Statement_EmptyRange_HasNoRows_AndOpeningEqualsClosing()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w, FinancialAccountKind.Bank, 300m, openingDate: Day(20));

        var statement = await RestrictedUserWorld.JsonOkAsync(await SendAsync(w, HttpMethod.Get, $"{Accounts}/{account}/statement?from={Day(10):yyyy-MM-dd}&to={Day(5):yyyy-MM-dd}"));

        Assert.Equal(0, statement.GetProperty("rows").GetArrayLength());
        Assert.Equal(300m, statement.GetProperty("openingBalance").GetDecimal());
        Assert.Equal(300m, statement.GetProperty("closingBalance").GetDecimal());
    }

    [Fact]
    public async Task Statement_FromAfterTo_Returns400From_AndUnknownAccountIs404()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w);

        await AssertBadAsync(await SendAsync(w, HttpMethod.Get, $"{Accounts}/{account}/statement?from={Today:yyyy-MM-dd}&to={Day(3):yyyy-MM-dd}"), "from");
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(w, HttpMethod.Get, $"{Accounts}/{Guid.NewGuid()}/statement")).StatusCode);
    }

    [Fact]
    public async Task Statement_OfASharedAccount_IsForbiddenToABranchLimitedMember_ButVisibleToTheCompanyWideAdmin()
    {
        var (r, w) = await WorldAsync(postgres);
        var x = await BranchAccountsAsync(w);
        var member = await MemberAsync(r, x.A);

        var hidden = await SendAsync(w, HttpMethod.Get, $"{Accounts}/{x.Shared}/statement", client: member.Client, branch: x.A);

        Assert.Equal(HttpStatusCode.Forbidden, hidden.StatusCode);
        Assert.Equal("branch_access_denied", await hidden.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(w, HttpMethod.Get, $"{Accounts}/{x.Shared}/statement")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(w, HttpMethod.Get, $"{Accounts}/{x.AccountA}/statement", client: member.Client, branch: x.A)).StatusCode);
    }

    // ---- 8. no fictitious revenue -----------------------------------------------------------------------------------------------

    private sealed record Snapshot(string Sales, string Profit, string Receivables, decimal Received, decimal MoneyIn, decimal MoneyOut);

    private static async Task<Snapshot> SnapshotAsync(BranchApiWorld w)
    {
        var sales = await RestrictedUserWorld.JsonOkAsync(await SendAsync(w, HttpMethod.Get, "/api/v1/reports/sales?pageSize=100"));
        var profit = await RestrictedUserWorld.JsonOkAsync(await SendAsync(w, HttpMethod.Get, "/api/v1/reports/profit-summary"));
        var receivables = await RestrictedUserWorld.JsonOkAsync(await SendAsync(w, HttpMethod.Get, "/api/v1/reports/accounts/customer-outstanding?pageSize=100"));
        var flow = await RestrictedUserWorld.JsonOkAsync(await SendAsync(w, HttpMethod.Get, "/api/v1/reports/accounts/cash-flow"));
        return new Snapshot(
            sales.GetProperty("total").GetInt32() + "|" + string.Join(",", sales.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid() + ":" + i.GetProperty("grandTotal").GetDecimal() + ":" + i.GetProperty("paidAmount").GetDecimal())),
            profit.GetProperty("invoiceCount").GetInt32() + "|" + profit.GetProperty("salesRevenue").GetDecimal() + "|" + profit.GetProperty("costOfGoodsSold").GetDecimal() + "|" + profit.GetProperty("grossProfit").GetDecimal(),
            receivables.GetProperty("totalNet").GetDecimal() + "|" + receivables.GetProperty("totalPaid").GetDecimal() + "|" + receivables.GetProperty("totalOutstanding").GetDecimal() + "|" + receivables.GetProperty("totalCredit").GetDecimal(),
            flow.GetProperty("receivedFromCustomers").GetDecimal(), flow.GetProperty("moneyIn").GetDecimal(), flow.GetProperty("moneyOut").GetDecimal());
    }

    private static decimal FundingOf(JsonElement flow, string source) =>
        flow.GetProperty("fundingBySource").EnumerateArray().Where(f => f.GetProperty("source").GetString() == source).Select(f => f.GetProperty("amount").GetDecimal()).SingleOrDefault();

    [Fact]
    public async Task FundingTransfersAndReversals_DoNotChangeSalesProfitReceivablesOrReceivedFromCustomers_AndFundingBySourceIsCorrect()
    {
        var (_, w) = await WorldAsync(postgres);
        Check(await w.SaleAsync(null, qty: 2m), "unpaid sale for the snapshot"); // a receivable exists, so "unchanged" is not vacuous
        var flowBefore = await RestrictedUserWorld.JsonOkAsync(await SendAsync(w, HttpMethod.Get, "/api/v1/reports/accounts/cash-flow"));
        var before = await SnapshotAsync(w);
        var bank = await NewAccountAsync(w, FinancialAccountKind.Bank, 100m, overdraft: 100m);
        var cash = await NewAccountAsync(w, FinancialAccountKind.Cash);

        Check(await FundAsync(w, bank, Funding(FundingSource.OwnerCapital, 1000m)), "capital");
        var loan = await FundedAsync(w, bank, 400m, FundingSource.Loan);
        Check(await FundAsync(w, bank, Funding(FundingSource.CustomerReceipt, 250m, reference: "RCT-9", customer: w.CustomerId)), "receipt");
        Check(await FundAsync(w, cash, Funding(FundingSource.CashDeposit, 60m)), "cash deposit");
        Check(await FundAsync(w, cash, Funding(FundingSource.Other, 15m, reason: "refund of deposit")), "other");
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, bank, cash, 300m)).StatusCode);
        var transferLeg = (await LedgerAsync(w, cash)).Where(t => t.GetProperty("type").GetString() == "Transfer").Select(t => t.GetProperty("id").GetGuid()).Last();
        Assert.Equal(HttpStatusCode.Created, (await ReverseAsync(w, loan)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ReverseAsync(w, transferLeg)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ReverseAsync(w, await OpeningTxAsync(w, bank))).StatusCode);

        var after = await SnapshotAsync(w);
        var flow = await RestrictedUserWorld.JsonOkAsync(await SendAsync(w, HttpMethod.Get, "/api/v1/reports/accounts/cash-flow"));

        Assert.Equal(before, after);
        Assert.Equal(before.Received, after.Received);
        Assert.Equal(FundingOf(flowBefore, "OwnerCapital") + 1000m, FundingOf(flow, "OwnerCapital"));
        Assert.Equal(FundingOf(flowBefore, "Loan") + 400m - 400m, FundingOf(flow, "Loan")); // reversed
        Assert.Equal(FundingOf(flowBefore, "CustomerReceipt") + 250m, FundingOf(flow, "CustomerReceipt"));
        Assert.Equal(FundingOf(flowBefore, "CashDeposit") + 60m, FundingOf(flow, "CashDeposit"));
        Assert.Equal(FundingOf(flowBefore, "Other") + 15m, FundingOf(flow, "Other"));
        Assert.Equal(flowBefore.GetProperty("fundingTotal").GetDecimal() + 1000m + 250m + 60m + 15m, flow.GetProperty("fundingTotal").GetDecimal());
        Assert.Equal(flowBefore.GetProperty("transfersIn").GetDecimal(), flow.GetProperty("transfersIn").GetDecimal()); // the reversed transfer nets to zero
        Assert.Equal(flowBefore.GetProperty("transfersOut").GetDecimal(), flow.GetProperty("transfersOut").GetDecimal());
        Assert.Equal(1250m, await BalanceAsync(w, bank)); // opening and loan reversed, transfer out and its reversal net to zero
    }

    [Fact]
    public async Task CustomerReceiptFunding_DoesNotReduceTheCustomersOutstanding_NorTheInvoiceDue()
    {
        var (_, w) = await WorldAsync(postgres);
        var sale = await JsonAsync(await w.SaleAsync(null, qty: 3m));
        var invoice = sale.GetProperty("id").GetGuid();
        var due = sale.GetProperty("dueAmount").GetDecimal();
        var account = await NewAccountAsync(w);

        Check(await FundAsync(w, account, Funding(FundingSource.CustomerReceipt, due, reference: "RCT-1", customer: w.CustomerId)), "receipt");

        var detail = await RestrictedUserWorld.JsonOkAsync(await SendAsync(w, HttpMethod.Get, $"/api/v1/sales/{invoice}"));
        Assert.Equal(due, detail.GetProperty("dueAmount").GetDecimal());
        Assert.Equal(0m, detail.GetProperty("paidAmount").GetDecimal());
        Assert.Equal(due, await BalanceAsync(w, account));
    }

    [Fact]
    public async Task TransactionList_MoneyInOut_ExcludeTransfersAndOpeningBalances_AndFundingIsOnlyItsOwnFlow()
    {
        var (_, w) = await WorldAsync(postgres);
        var from = await NewAccountAsync(w, FinancialAccountKind.Bank, 400m);
        var to = await NewAccountAsync(w);
        Check(await TransferAsync(w, from, to, 100m), "transfer");

        var list = await RestrictedUserWorld.JsonOkAsync(await SendAsync(w, HttpMethod.Get, $"{Transactions}?accountId={from}&pageSize=100"));

        Assert.Equal(0m, list.GetProperty("moneyIn").GetDecimal());
        Assert.Equal(0m, list.GetProperty("moneyOut").GetDecimal());
    }

    // ---- 9. audit ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FundTransferAndReverse_WriteOneAuditEventEach_WithActorAndBalances()
    {
        var (_, w) = await WorldAsync(postgres);
        var from = await NewAccountAsync(w, FinancialAccountKind.Bank, 100m);
        var to = await NewAccountAsync(w);

        var funding = await FundedAsync(w, from, 50m, FundingSource.Loan);
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(w, from, to, 30m)).StatusCode);
        var leg = (await LedgerAsync(w, from)).Single(t => t.GetProperty("type").GetString() == "Transfer").GetProperty("id").GetGuid();
        var group = await ScalarAsync<Guid>(w.Host.Database, $"""SELECT "TransferGroupId" FROM "FinancialTransactions" WHERE "Id" = '{leg}'""");
        Assert.Equal(HttpStatusCode.Created, (await ReverseAsync(w, leg, "audit me")).StatusCode);

        Assert.Equal(1L, await CountAsync(w, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'cash.funded' AND "TargetId" = '{funding}' AND "ActorUserId" = '{w.AdminId}' AND "TargetType" = 'FinancialTransaction'"""));
        Assert.Equal(1L, await CountAsync(w, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'cash.transferred' AND "TargetId" = '{group}' AND "ActorUserId" = '{w.AdminId}'"""));
        Assert.Equal(1L, await CountAsync(w, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'cash.reversed' AND "TargetId" = '{leg}' AND "ActorUserId" = '{w.AdminId}' AND "Reason" = 'audit me'"""));
        Assert.True(await CountAsync(w, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" IN ('cash.funded','cash.transferred','cash.reversed') AND "CorrelationId" IS NOT NULL AND "TargetId" IN ('{funding}','{group}','{leg}')""") == 3);
    }

    [Fact]
    public async Task IdempotentReplayAndRefusedPostings_WriteNoExtraAuditEvents()
    {
        var (_, w) = await WorldAsync(postgres);
        var account = await NewAccountAsync(w, FinancialAccountKind.Cash, 10m);
        var other = await NewAccountAsync(w);
        var key = NewKey();
        var funding = (await JsonAsync(await FundAsync(w, account, Funding(FundingSource.Loan, 5m), key: key))).GetProperty("transactionId").GetGuid();
        var before = await CountAsync(w, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" LIKE 'cash.%'""");

        Check(await FundAsync(w, account, Funding(FundingSource.Loan, 5m), key: key), "replay");
        Assert.Equal(HttpStatusCode.BadRequest, (await TransferAsync(w, account, other, 9999m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await FundAsync(w, account, Funding(FundingSource.Other))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ReverseAsync(w, Guid.NewGuid())).StatusCode);

        Assert.Equal(before, await CountAsync(w, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" LIKE 'cash.%'"""));
        Assert.Equal(1L, await CountAsync(w, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'cash.funded' AND "TargetId" = '{funding}'"""));
    }

    // ---- other company fixtures -------------------------------------------------------------------------------------------------

    private static readonly Guid ForeignCompanyId = Guid.Parse("0c0a1f00-0000-4000-8000-0000000000c7");

    private static async Task EnsureForeignCompanyAsync(BranchApiWorld w)
    {
        await using var db = w.Host.Database.CreateDbContext();
        if (!db.Companies.Any(c => c.Id == ForeignCompanyId))
        {
            db.Companies.Add(new Company { Id = ForeignCompanyId, Code = "CBX", Name = "Cash Bank Foreign Co" });
            await db.SaveChangesAsync();
        }
    }

    private static async Task<Guid> ForeignCustomerAsync(BranchApiWorld w)
    {
        await EnsureForeignCompanyAsync(w);
        await using var db = w.Host.Database.CreateDbContext(ForeignCompanyId);
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Foreign " + NewKey()[..6] };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<Guid> ForeignTransactionAsync(BranchApiWorld w)
    {
        await EnsureForeignCompanyAsync(w);
        await using var db = w.Host.Database.CreateDbContext(ForeignCompanyId);
        var account = new FinancialAccount { Id = Guid.NewGuid(), Name = "Foreign " + NewKey()[..8], Kind = FinancialAccountKind.Bank, CreatedByUserId = w.AdminId };
        var tx = new FinancialTransaction
        {
            Id = Guid.NewGuid(), Type = FinancialTransactionType.Funding, Direction = MoneyDirection.In, TransactionDate = Today, AccountId = account.Id,
            CurrencyId = w.CurrencyId, Amount = 10m, ExchangeRateToBase = 1m, BaseAmount = 10m, PaymentMethod = PaymentMethod.Cash,
            FundingSource = FundingSource.Loan, CreatedByUserId = w.AdminId,
        };
        db.FinancialAccounts.Add(account);
        db.FinancialTransactions.Add(tx);
        await db.SaveChangesAsync();
        return tx.Id;
    }
}
