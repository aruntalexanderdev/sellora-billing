using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Phase 5 review fixes: the Accounts reports and the financial-transaction list narrow to the caller's branches (a transaction is visible iff its
/// source document, or for account-only rows its account, is visible); the expense/account branch invariant still sees hidden-branch accounts;
/// an idempotent replay whose result is no longer visible is a 400, not a 500.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RestrictedUserReportsApiTests(PostgresFixture postgres)
{
    // FI-D11: reads cover the active branch only; branch = the X-Branch-Id to read at (null = headerless = primary/default branch).
    private static async Task<JsonElement> GetAsync(HttpClient client, string route, Guid? branch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        if (branch is { } b) request.Headers.Add("X-Branch-Id", b.ToString());
        return await RestrictedUserWorld.JsonOkAsync(await client.SendAsync(request));
    }

    private static decimal Num(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

    private sealed record Totals(decimal PurchasePayments, decimal SalesPayments, decimal CashIn, decimal CashOut, decimal Received, decimal Paid, decimal Expenses,
        decimal MethodReceived, decimal MethodPaid, decimal TxIn, decimal TxOut, int TxTotal);

    private static async Task<Totals> SnapshotAsync(HttpClient c, Guid? branch = null)
    {
        var pp = await GetAsync(c, "/api/v1/reports/accounts/purchase-payments", branch);
        var sp = await GetAsync(c, "/api/v1/reports/accounts/sales-payments", branch);
        var cf = await GetAsync(c, "/api/v1/reports/accounts/cash-flow", branch);
        var pm = await GetAsync(c, "/api/v1/reports/accounts/payment-methods", branch);
        var tx = await GetAsync(c, "/api/v1/financial-transactions?pageSize=100", branch);
        return new Totals(Num(pp, "payments"), Num(sp, "payments"), Num(cf, "moneyIn"), Num(cf, "moneyOut"), Num(cf, "receivedFromCustomers"),
            Num(cf, "paidToSuppliers"), Num(cf, "expenses"), Num(pm, "totalReceived"), Num(pm, "totalPaid"), Num(tx, "moneyIn"), Num(tx, "moneyOut"), tx.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task AccountsReportsAndTransactions_ExcludeHiddenBranchPaymentsAndExpenses()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);
        var adminBefore = await SnapshotAsync(w.Admin, y); // FI-D11: the admin reads at Y, where the hidden-branch money lands
        var userBefore = await SnapshotAsync(user.Client);

        // Hidden branch Y: null-account sales payment, purchase payment and a branch-tagged expense (no account).
        Check(await w.SaleAsync(y, qty: 2m, paid: 40m), "sale y");
        Check(await w.PurchaseAsync(y, qty: 2m, paid: 10m), "purchase y");
        Check(await w.ExpenseAsync(y), "expense y");

        var adminAfter = await SnapshotAsync(w.Admin, y);
        var userAfter = await SnapshotAsync(user.Client);

        Assert.Equal(adminBefore.SalesPayments + 40m, adminAfter.SalesPayments);
        Assert.Equal(adminBefore.PurchasePayments + 10m, adminAfter.PurchasePayments);
        Assert.Equal(adminBefore.Expenses + 10m, adminAfter.Expenses);
        Assert.Equal(adminBefore.TxIn + 40m, adminAfter.TxIn);
        Assert.Equal(adminBefore.TxOut + 20m, adminAfter.TxOut);
        Assert.Equal(userBefore, userAfter);
    }

    [Fact]
    public async Task RestrictedUser_SeesOwnBranchAndCompanyLevelMoney_ButNotHiddenBranchMoney()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);
        var before = await SnapshotAsync(user.Client);

        Check(await w.SaleAsync(x, qty: 1m, paid: 20m), "sale x");
        Check(await w.ExpenseAsync(null), "company-level expense");
        Check(await w.SaleAsync(y, qty: 1m, paid: 20m), "sale y");
        var after = await SnapshotAsync(user.Client);

        Assert.Equal(before.SalesPayments + 20m, after.SalesPayments);
        Assert.Equal(before.Received + 20m, after.Received);
        Assert.Equal(before.Expenses + 10m, after.Expenses);
        Assert.Equal(before.TxTotal + 2, after.TxTotal);
    }

    [Fact]
    public async Task ExpenseRetag_ToAllowedBranch_WhilePaidFromHiddenBranchAccount_IsRejected()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var hiddenAccount = await w.NewAccountAsync("Till Y " + RestrictedUserWorld.NewName(""), y);
        var created = await w.SendAsync(HttpMethod.Post, "/api/v1/expenses",
            new CreateExpenseRequest(w.CategoryId, Today, w.CurrencyId, 10m, PaymentMethod.Cash, null, null, hiddenAccount, null));
        Check(created, "expense from hidden account");
        var expenseId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var user = await r.CreateAtAsync(x);

        var retag = await w.SendAsync(HttpMethod.Put, $"/api/v1/expenses/{expenseId}", new UpdateExpenseRequest(w.CategoryId, null, null, x), client: user.Client);

        Assert.Equal(HttpStatusCode.BadRequest, retag.StatusCode);
        Assert.Contains("branchid", await ErrorFieldsAsync(retag));
    }

    [Fact]
    public async Task IdempotentReplay_AfterAllocationChange_IsNotAServerError()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        var user = await r.CreateAtAsync(x);
        await w.EnsureBranchStockAsync(x, 1m); // 8b: x sells from its own store
        var key = "replay-" + Guid.NewGuid().ToString("N");
        var body = new CreateSaleRequest(w.CustomerId, Today, PaymentMethod.Cash, 0m, [new CreateSaleLineRequest(w.ProductId, w.ProductUnitId, 1m, 20m, 0)]);

        async Task<HttpResponseMessage> PostAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales") { Content = JsonContent.Create(body, options: ApiHost.Json) };
            request.Headers.Add("Idempotency-Key", key);
            return await user.Client.SendAsync(request);
        }

        Check(await PostAsync(), "first sale");
        var replayVisible = await PostAsync();
        Assert.True(replayVisible.IsSuccessStatusCode, "replay while visible");

        var current = await r.GetAccessAsync(user.Id);
        var xAssignment = current.GetProperty("current").EnumerateArray().Single(a => a.GetProperty("branchId").GetGuid() == x).GetProperty("id").GetGuid();
        var move = await r.AccessAsync(HttpMethod.Post, user.Id, "/reassign", new ReassignRequest([xAssignment], y, true, "moved"));
        Check(move, "reallocate user");
        var replayHidden = await PostAsync();

        Assert.Equal(HttpStatusCode.BadRequest, replayHidden.StatusCode);
        Assert.Contains("idempotencykey", await ErrorFieldsAsync(replayHidden));
    }
}
