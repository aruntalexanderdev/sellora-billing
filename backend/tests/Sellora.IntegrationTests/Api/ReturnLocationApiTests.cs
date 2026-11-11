using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Phase 10 over real HTTP (Postgres, real pipeline): a purchase return naming ANOTHER company's stock location is refused on <c>stockLocationId</c> (own location accepted,
/// so the refusal is not vacuous), the purchase detail at a foreign location is the one 403 <c>branch_access_denied</c>, and an invalid <c>Idempotency-Key</c> is 400 on both
/// return POSTs. (The cross-company write is also in the <see cref="IsolationCases"/> table.)
/// </summary>
[Collection(PostgresCollection.Name)]
public class ReturnLocationApiTests(PostgresFixture postgres)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task PurchaseReturn_ForeignCompanyStockLocation_Is400OnStockLocationId_OwnLocationIsAccepted()
    {
        var w = await IsolationWorld.BuildAsync(postgres);
        using var a = w.Host.NewClient(w.A.Token);

        var foreign = await a.PostAsJsonAsync("/api/v1/purchase-returns",
            new CreatePurchaseReturnRequest(w.A.PurchaseId, Today, null, [new PurchaseReturnLineRequest(w.A.PurchaseLineId, null, 1m)], StockLocationId: w.B.LocationId), ApiHost.Json);
        var foreignBody = await foreign.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Contains("stocklocationid", foreignBody.ToLowerInvariant());

        var own = await a.PostAsJsonAsync("/api/v1/purchase-returns",
            new CreatePurchaseReturnRequest(w.A.PurchaseId, Today, null, [new PurchaseReturnLineRequest(w.A.PurchaseLineId, null, 1m)], StockLocationId: w.A.LocationId), ApiHost.Json);
        var ownBody = await own.Content.ReadAsStringAsync();
        Assert.True(own.IsSuccessStatusCode, $"own location refused: {(int)own.StatusCode} {ownBody}");
        Assert.Equal(w.A.LocationId, JsonDocument.Parse(ownBody).RootElement.GetProperty("stockLocationId").GetGuid());
    }

    [Fact]
    public async Task PurchaseDetail_ForeignCompanyStockLocation_Is403BranchAccessDenied()
    {
        var w = await IsolationWorld.BuildAsync(postgres);
        using var a = w.Host.NewClient(w.A.Token);

        var response = await a.GetAsync($"/api/v1/purchases/{w.A.PurchaseId}?stockLocationId={w.B.LocationId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("branch_access_denied", await response.Content.ReadAsStringAsync());
        var own = await a.GetAsync($"/api/v1/purchases/{w.A.PurchaseId}?stockLocationId={w.A.LocationId}");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.True(JsonDocument.Parse(await own.Content.ReadAsStringAsync()).RootElement.GetProperty("lines")[0].TryGetProperty("locationBalances", out _));
    }

    [Fact]
    public async Task CreateReturns_InvalidIdempotencyKeyHeader_Is400OnBothEndpoints()
    {
        var w = await IsolationWorld.BuildAsync(postgres);
        using var a = w.Host.NewClient(w.A.Token);
        var tooLong = new string('k', 101);

        async Task<HttpStatusCode> PostAsync(string route, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body, options: ApiHost.Json) };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", tooLong);
            return (await a.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync("/api/v1/sales-returns", new CreateSaleReturnRequest(w.A.SaleId, Today, null, [new SaleReturnLineRequest(w.A.SaleLineId, null, 1m)])));
        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync("/api/v1/purchase-returns", new CreatePurchaseReturnRequest(w.A.PurchaseId, Today, null, [new PurchaseReturnLineRequest(w.A.PurchaseLineId, null, 1m)])));
    }
}
