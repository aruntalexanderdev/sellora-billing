using System.Net;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Companies;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;

namespace Sellora.IntegrationTests.Api;

/// <summary>Phase 3 T6 — read-only stock-locations API: list ordering and filters, branch columns, capability flags, branch-driven store lifecycle, 404/403 rules, the StockLocations.View policy.</summary>
[Collection(PostgresCollection.Name)]
public class StockLocationsApiTests(PostgresFixture postgres)
{
    private const string Route = "/api/v1/stock-locations";

    private static async Task<List<JsonElement>> ListAsync(BranchApiWorld w, string query = "", HttpClient? client = null)
    {
        var response = await w.SendAsync(HttpMethod.Get, Route + query, client: client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).EnumerateArray().ToList();
    }

    private static JsonElement For(List<JsonElement> list, Guid branchId) => list.Single(l => l.TryGetProperty("branchId", out var b) && b.ValueKind == JsonValueKind.String && b.GetGuid() == branchId);

    [Fact]
    public async Task List_Default_ShowsMainStoreFirstThenTransitWithBranchColumnsAndFlags()
    {
        var w = await GetAsync(postgres);

        var list = await ListAsync(w);

        var main = list[0];
        Assert.Equal(DefaultTenant.MainStoreLocationId, main.GetProperty("id").GetGuid());
        Assert.Equal(("BranchStore", "MAIN", w.MainId), (main.GetProperty("locationType").GetString(), main.GetProperty("code").GetString(), main.GetProperty("branchId").GetGuid()));
        Assert.Equal(("MAIN", "Main"), (main.GetProperty("branchCode").GetString(), main.GetProperty("branchName").GetString()));
        Assert.Equal((true, true, true, true), (main.GetProperty("isActive").GetBoolean(), main.GetProperty("canSell").GetBoolean(), main.GetProperty("canReceivePurchase").GetBoolean(), main.GetProperty("isSystemManaged").GetBoolean()));
        var transit = list.Single(l => l.GetProperty("locationType").GetString() == "Transit");
        Assert.Equal(DefaultTenant.TransitLocationId, transit.GetProperty("id").GetGuid());
        Assert.Equal(("TRANSIT", JsonValueKind.Null, JsonValueKind.Null), (transit.GetProperty("code").GetString(), transit.GetProperty("branchId").ValueKind, transit.GetProperty("branchCode").ValueKind));
        Assert.Equal((false, false, true), (transit.GetProperty("canSell").GetBoolean(), transit.GetProperty("canReceivePurchase").GetBoolean(), transit.GetProperty("isSystemManaged").GetBoolean()));
        Assert.True(list.IndexOf(transit) > 0, "Transit lists after the BranchStores (type enum order).");
    }

    [Fact]
    public async Task CreateBranch_AddsItsStoreWithTheBranchCodeAndName_VisibleInListAndDetail()
    {
        var w = await GetAsync(postgres);
        var (branchId, code) = await w.NewBranchAsync();

        var store = For(await ListAsync(w), branchId);

        Assert.Equal((code, "Test " + code, "BranchStore", code, true), (store.GetProperty("code").GetString(), store.GetProperty("name").GetString(), store.GetProperty("locationType").GetString(), store.GetProperty("branchCode").GetString(), store.GetProperty("isActive").GetBoolean()));
        var detail = await w.SendAsync(HttpMethod.Get, $"{Route}/{store.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(code, (await JsonAsync(detail)).GetProperty("branchCode").GetString());
    }

    [Fact]
    public async Task RenameAndDeactivateBranch_MirrorToTheStore_InactiveHiddenUnlessRequested_ThenReactivated()
    {
        var w = await GetAsync(postgres);
        var (branchId, code) = await w.NewBranchAsync();
        Assert.True((await w.SendAsync(HttpMethod.Put, $"/api/v1/branches/{branchId}", new UpdateBranchRequest("Renamed " + code, null, null, null, null, null, null, null))).IsSuccessStatusCode);
        Assert.True((await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{branchId}/status", new SetActiveRequest(false))).IsSuccessStatusCode);

        Assert.DoesNotContain(await ListAsync(w), l => l.TryGetProperty("branchId", out var b) && b.ValueKind == JsonValueKind.String && b.GetGuid() == branchId);
        var inactive = For(await ListAsync(w, "?includeInactive=true"), branchId);
        Assert.Equal(("Renamed " + code, code, false), (inactive.GetProperty("name").GetString(), inactive.GetProperty("code").GetString(), inactive.GetProperty("isActive").GetBoolean()));

        Assert.True((await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{branchId}/status", new SetActiveRequest(true))).IsSuccessStatusCode);
        Assert.True(For(await ListAsync(w), branchId).GetProperty("isActive").GetBoolean());
    }

    [Theory]
    [InlineData("Transit")]
    [InlineData("transit")]
    [InlineData("TRANSIT")]
    public async Task List_TypeFilter_IsCaseInsensitive(string type)
    {
        var w = await GetAsync(postgres);

        var list = await ListAsync(w, "?type=" + type);

        Assert.Equal("Transit", Assert.Single(list).GetProperty("locationType").GetString());
    }

    [Fact]
    public async Task List_TypeBranchStore_ReturnsOnlyStores_AndAnEmptyTypeSetIsNotAnError()
    {
        var w = await GetAsync(postgres);
        await w.NewBranchAsync();

        var stores = await ListAsync(w, "?type=BranchStore");

        Assert.All(stores, s => Assert.Equal("BranchStore", s.GetProperty("locationType").GetString()));
        Assert.True(stores.Count >= 2);
        Assert.Empty(await ListAsync(w, "?type=Vehicle"));
    }

    [Fact]
    public async Task List_UnknownType_Returns400OnTheTypeField()
    {
        var w = await GetAsync(postgres);

        var response = await w.SendAsync(HttpMethod.Get, Route + "?type=Spaceship");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("type", await ErrorFieldsAsync(response));
    }

    [Fact]
    public async Task List_BranchFilter_ReturnsThatBranchsLocationsOnly()
    {
        var w = await GetAsync(postgres);
        var (branchId, _) = await w.NewBranchAsync();

        var list = await ListAsync(w, $"?branchId={branchId}");

        Assert.Equal(branchId, Assert.Single(list).GetProperty("branchId").GetGuid());
    }

    [Fact]
    public async Task List_BranchFilterOnAnInactiveBranch_IsAllowedWhenIncludingInactive()
    {
        var w = await GetAsync(postgres);
        var (branchId, _) = await w.NewBranchAsync();
        await w.SendAsync(HttpMethod.Post, $"/api/v1/branches/{branchId}/status", new SetActiveRequest(false));

        var list = await ListAsync(w, $"?branchId={branchId}&includeInactive=true");

        Assert.False(Assert.Single(list).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task List_BranchIdUnknown_Returns403BranchAccessDenied()
    {
        var w = await GetAsync(postgres);

        var response = await w.SendAsync(HttpMethod.Get, $"{Route}?branchId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("branch_access_denied", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Get_UnknownId_Returns404()
    {
        var w = await GetAsync(postgres);

        Assert.Equal(HttpStatusCode.NotFound, (await w.SendAsync(HttpMethod.Get, $"{Route}/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Get_MainStore_ReturnsTheDto()
    {
        var w = await GetAsync(postgres);

        var json = await JsonAsync(await w.SendAsync(HttpMethod.Get, $"{Route}/{DefaultTenant.MainStoreLocationId}"));

        Assert.Equal(("MAIN", "BranchStore"), (json.GetProperty("code").GetString(), json.GetProperty("locationType").GetString()));
    }

    [Fact]
    public async Task Endpoints_WithoutStockLocationsView_Answer403_WithItAnswer200()
    {
        var w = await GetAsync(postgres);
        using var nobody = w.Host.NewClient(ApiHost.SignToken(w.AdminId, w.CompanyId, ["Masters.View", "Company.Branches.View", "Inventory.View"]));
        using var viewer = w.Host.NewClient(ApiHost.SignToken(w.AdminId, w.CompanyId, ["StockLocations.View"]));

        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendAsync(HttpMethod.Get, Route, client: nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.SendAsync(HttpMethod.Get, $"{Route}/{DefaultTenant.MainStoreLocationId}", client: nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, Route, client: viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.SendAsync(HttpMethod.Get, $"{Route}/{DefaultTenant.MainStoreLocationId}", client: viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.SendAsync(HttpMethod.Get, Route, client: w.Host.NewClient())).StatusCode);
    }

    [Fact]
    public async Task Endpoints_NoWriteVerbsExist()
    {
        var w = await GetAsync(postgres);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            var status = (await w.SendAsync(method, $"{Route}/{DefaultTenant.MainStoreLocationId}", new { name = "x" })).StatusCode;
            Assert.True(status is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, $"{method} must not be routed, got {status}");
        }
        Assert.True((await w.SendAsync(HttpMethod.Post, Route, new { code = "X" })).StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AuthMe_Administrator_HasTheStockLocationsViewCode()
    {
        var w = await GetAsync(postgres);

        var me = (await JsonAsync(await w.SendAsync(HttpMethod.Get, "/api/v1/auth/me"))).ToString();

        Assert.Contains("StockLocations.View", me);
    }
}
