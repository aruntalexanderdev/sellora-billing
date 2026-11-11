using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.Api.BranchApiWorld;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Phase 8b reads on PostgreSQL through the real API host (the EF translation of the per-location aggregates, the visibility predicate and
/// the dictionary lookups is what the InMemory tests cannot prove): one product stocked at several locations - the default store (the
/// shared world's 1000 kg), a new branch's own store (30 @ 20) and a company warehouse (20 @ 50) - read as the admin and as a holder of
/// the stock codes at ONE branch only. The world is shared, so assertions are on the new locations, on explicit location filters and on
/// internal consistency (total = sum of the per-location rows), never on company totals.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StockLocationReadsApiTests(PostgresFixture postgres)
{
    private const string Pw = "Passw0rd!99";
    private static int _seq;
    private static string N(string prefix) => prefix + Interlocked.Increment(ref _seq) + Guid.NewGuid().ToString("N")[..4];

    private sealed record Stocked(BranchApiWorld W, Guid Branch, Guid BranchStore, Guid CompanyWarehouse);

    /// <summary>A new branch whose store holds 30 kg @ 20, and a new company warehouse holding 20 kg @ 50 (hand-seeded, dual-write consistent).</summary>
    private async Task<Stocked> SeedAsync()
    {
        var w = await GetAsync(postgres);
        var (branch, _) = await w.NewBranchAsync();
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        var store = await StockSeed.StoreOfAsync(db, branch);
        var warehouse = await new WarehouseService(db, new StockLocationUsageService(db)).CreateAsync(new CreateWarehouseInput(N("CW"), N("Central warehouse "), null), w.AdminId);
        await StockSeed.AddAsync(db, store, w.ProductId, 30m, 20m, w.AdminId);
        await StockSeed.AddAsync(db, warehouse.StockLocationId, w.ProductId, 20m, 50m, w.AdminId);
        return new Stocked(w, branch, store, warehouse.StockLocationId);
    }

    private static async Task<JsonElement> GetOkAsync(BranchApiWorld w, string route, HttpClient? client = null, Guid? branch = null)
    {
        var response = await w.SendAsync(HttpMethod.Get, route, null, branch, client);
        Assert.True(response.IsSuccessStatusCode, $"{route}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await JsonAsync(response);
    }

    private static JsonElement Row(JsonElement paged, Guid productId) => paged.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("productId").GetGuid() == productId);

    private static Dictionary<Guid, decimal> LocationQty(JsonElement locations) =>
        locations.EnumerateArray().ToDictionary(l => l.GetProperty("locationId").GetGuid(), l => l.GetProperty("qty").GetDecimal());

    // ---- admin (company-level) ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AdminReads_ProductAtSeveralLocations_NeverFail_AndBreakDownPerLocation()
    {
        var s = await SeedAsync();
        var w = s.W;
        var pid = w.ProductId;

        var overview = Row(await GetOkAsync(w, "/api/v1/stock/overview?search=STN-PEN&pageSize=100"), pid);
        var byLocation = LocationQty(overview.GetProperty("locations"));
        Assert.Equal((30m, 20m), (byLocation[s.BranchStore], byLocation[s.CompanyWarehouse]));
        Assert.True(byLocation.Count >= 3);
        Assert.Equal(byLocation.Values.Sum(), overview.GetProperty("quantityOnHand").GetDecimal()); // one product's quantity summed over its own locations

        var detail = await GetOkAsync(w, $"/api/v1/stock/{pid}");
        Assert.Equal(byLocation.Values.Sum(), detail.GetProperty("quantityOnHand").GetDecimal());
        Assert.Equal(30m, LocationQty(detail.GetProperty("locations"))[s.BranchStore]);
        var batchLocations = detail.GetProperty("batches").EnumerateArray().SelectMany(b => b.GetProperty("locations").EnumerateArray()).Select(l => l.GetProperty("locationId").GetGuid()).ToHashSet();
        Assert.Contains(s.BranchStore, batchLocations);
        Assert.Contains(s.CompanyWarehouse, batchLocations);
        Assert.All(detail.GetProperty("movements").EnumerateArray(), m => Assert.False(string.IsNullOrEmpty(m.GetProperty("stockLocationCode").GetString())));

        var report = Row(await GetOkAsync(w, "/api/v1/reports/current-stock?search=STN-PEN&pageSize=100"), pid);
        Assert.Equal(overview.GetProperty("quantityOnHand").GetDecimal(), report.GetProperty("quantityOnHand").GetDecimal());
        await GetOkAsync(w, "/api/v1/reports/current-stock/categories");

        var movements = await GetOkAsync(w, $"/api/v1/reports/stock-movement?stockLocationId={s.CompanyWarehouse}");
        Assert.Equal(1, movements.GetProperty("total").GetInt32());
        Assert.Equal(s.CompanyWarehouse, movements.GetProperty("items")[0].GetProperty("stockLocationId").GetGuid());

        await GetOkAsync(w, "/api/v1/dashboard");
        await GetOkAsync(w, "/api/v1/pricing");
    }

    [Fact]
    public async Task AdminReads_ExplicitLocation_ScopesQuantityAndValueToIt()
    {
        var s = await SeedAsync();

        var row = Row(await GetOkAsync(s.W, $"/api/v1/stock/overview?search=STN-PEN&stockLocationId={s.BranchStore}"), s.W.ProductId);
        Assert.Equal((30m, 20m, 600m), (row.GetProperty("quantityOnHand").GetDecimal(), row.GetProperty("averageCost").GetDecimal(), row.GetProperty("stockValue").GetDecimal()));

        var report = Row(await GetOkAsync(s.W, $"/api/v1/reports/current-stock?search=STN-PEN&stockLocationId={s.CompanyWarehouse}"), s.W.ProductId);
        Assert.Equal((20m, 1000m), (report.GetProperty("quantityOnHand").GetDecimal(), report.GetProperty("stockValue").GetDecimal()));
    }

    [Fact]
    public async Task SalesCatalog_ReportsAvailabilityAtTheSellingLocation()
    {
        var s = await SeedAsync();

        var atBranch = await GetOkAsync(s.W, "/api/v1/sales/catalog?search=STN-PEN", branch: s.Branch);   // working branch's own store
        var item = atBranch.EnumerateArray().Single(p => p.GetProperty("productId").GetGuid() == s.W.ProductId);
        Assert.Equal((30m, s.BranchStore), (item.GetProperty("quantityOnHand").GetDecimal(), item.GetProperty("stockLocationId").GetGuid()));

        // A warehouse cannot sell (CanSell): the catalog accepts only a location the caller could sell from, so it is one 403.
        var atWarehouse = await s.W.SendAsync(HttpMethod.Get, $"/api/v1/sales/catalog?search=STN-PEN&stockLocationId={s.CompanyWarehouse}", null, null, null);
        Assert.Equal(HttpStatusCode.Forbidden, atWarehouse.StatusCode);
        Assert.Equal("branch_access_denied", (await JsonAsync(atWarehouse)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PurchaseDetail_ReturnableQuantity_FollowsThePurchasesLocation()
    {
        var s = await SeedAsync();
        var response = await s.W.PurchaseAsync(s.Branch, qty: 12m);
        Check(response, "purchase at the new branch");
        var purchase = await JsonAsync(response);
        Check(await s.W.SaleAsync(s.Branch, qty: 2m), "sale at the new branch");

        var detail = await GetOkAsync(s.W, $"/api/v1/purchases/{purchase.GetProperty("id").GetGuid()}");

        // The branch store holds 30 + 12 and the sale drew FIFO from the older seeded batch: the purchase's batch is intact at its own location.
        Assert.Equal(12m, detail.GetProperty("lines")[0].GetProperty("returnableBaseQty").GetDecimal());
    }

    // ---- branch-only holder ---------------------------------------------------------------------------------------------------------

    private static async Task<Guid> RoleAsync(BranchApiWorld w, string name, string[] codes, RoleScope? scope = null, Guid? branch = null)
    {
        var response = await w.SendAsync(HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(name, null, codes, scope, branch));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>A user with an EMPTY company role, access to the given branches, and a BRANCH-scoped role holding <paramref name="codes"/> at <paramref name="roleBranch"/> only.</summary>
    private static async Task<HttpClient> BranchHolderAsync(BranchApiWorld w, Guid[] branches, Guid roleBranch, params string[] codes)
    {
        var username = N("u");
        var empty = await RoleAsync(w, N("Empty"), []);
        var response = await w.SendAsync(HttpMethod.Post, "/api/v1/users", new CreateUserRequest("T", "U", username, username + "@selloramart.in", null, Pw, Pw, empty, CompanyAccessScope.AssignedBranches, branches, roleBranch));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await JsonAsync(response)).GetProperty("id").GetGuid();
        var branchRole = await RoleAsync(w, N("BranchStock"), codes, RoleScope.Branch, roleBranch);
        var assign = await w.SendAsync(HttpMethod.Post, $"/api/v1/users/{id}/roles", new AssignRoleRequest(branchRole, null));
        Assert.Equal(HttpStatusCode.Created, assign.StatusCode);
        return w.Host.NewClient((await w.Host.LoginAsync(username, Pw)).AccessToken);
    }

    [Fact]
    public async Task BranchOnlyHolder_SeesOwnStoreAndCompanyLevelLocations_NotTheDefaultStoreOrOtherBranches()
    {
        var s = await SeedAsync();
        var (other, _) = await s.W.NewBranchAsync();
        await using (var db = s.W.Host.Database.CreateDbContext(s.W.CompanyId))
        {
            await StockSeed.AddAsync(db, await StockSeed.StoreOfAsync(db, other), s.W.ProductId, 7m, 9m, s.W.AdminId); // another branch's store
        }
        var user = await BranchHolderAsync(s.W, [s.Branch, other], s.Branch, "Inventory.View", "Reports.Stock", "Reports.Movement");

        var overview = Row(await GetOkAsync(s.W, "/api/v1/stock/overview?search=STN-PEN", user, s.Branch), s.W.ProductId);
        var locations = LocationQty(overview.GetProperty("locations"));

        Assert.Equal(30m, locations[s.BranchStore]);
        Assert.Equal(20m, locations[s.CompanyWarehouse]);       // company-level: visible
        Assert.False(locations.ContainsKey(DefaultTenantStore));  // the default store (1000 kg) is another branch's
        Assert.False(locations.Keys.Any(k => k != s.BranchStore && k != s.CompanyWarehouse && !IsCompanyLevel(s, k)), "no other branch's store may be visible");
        Assert.Equal(locations.Values.Sum(), overview.GetProperty("quantityOnHand").GetDecimal());

        var detail = await GetOkAsync(s.W, $"/api/v1/stock/{s.W.ProductId}", user, s.Branch);
        Assert.DoesNotContain(detail.GetProperty("movements").EnumerateArray(), m => m.GetProperty("stockLocationId").GetGuid() == DefaultTenantStore);
        var report = await GetOkAsync(s.W, "/api/v1/reports/stock-movement?pageSize=100", user, s.Branch);
        Assert.DoesNotContain(report.GetProperty("items").EnumerateArray(), r => r.GetProperty("stockLocationId").GetGuid() == DefaultTenantStore);
        Assert.Equal(s.BranchStore, (await GetOkAsync(s.W, $"/api/v1/reports/stock-movement?stockLocationId={s.BranchStore}", user, s.Branch)).GetProperty("items")[0].GetProperty("stockLocationId").GetGuid());
        await GetOkAsync(s.W, $"/api/v1/stock/overview?stockLocationId={s.CompanyWarehouse}", user, s.Branch); // explicit company-level: visible
        await GetOkAsync(s.W, "/api/v1/dashboard", user, s.Branch);
    }

    private static readonly Guid DefaultTenantStore = Sellora.Domain.Companies.DefaultTenant.MainStoreLocationId;

    private static bool IsCompanyLevel(Stocked s, Guid locationId)
    {
        using var db = s.W.Host.Database.CreateDbContext(s.W.CompanyId);
        return db.StockLocations.IgnoreQueryFilters().Where(l => l.Id == locationId).Select(l => l.BranchId == null).Single();
    }

    [Fact]
    public async Task BranchOnlyHolder_ExplicitHiddenLocation_Is403BranchAccessDenied_OnEveryRead_AndNothingLeaks()
    {
        var s = await SeedAsync();
        var user = await BranchHolderAsync(s.W, [s.Branch], s.Branch, "Inventory.View", "Reports.Stock", "Reports.Movement", "Sales.Create");
        var hidden = new[] { DefaultTenantStore, Guid.NewGuid() }; // another branch's store; an unknown id answers the same

        foreach (var id in hidden)
        {
            foreach (var route in new[]
            {
                $"/api/v1/stock/overview?stockLocationId={id}", $"/api/v1/stock/{s.W.ProductId}?stockLocationId={id}",
                $"/api/v1/reports/current-stock?stockLocationId={id}", $"/api/v1/reports/stock-movement?stockLocationId={id}",
                $"/api/v1/sales/catalog?stockLocationId={id}",
            })
            {
                var response = await s.W.SendAsync(HttpMethod.Get, route, null, s.Branch, user);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("branch_access_denied", (await JsonAsync(response)).GetProperty("code").GetString());
            }
        }
    }

    [Fact]
    public async Task BranchOnlyHolder_StockCodesHeldAtAnotherBranchOnly_StillSeesNothingOfTheDefaultStore()
    {
        var s = await SeedAsync();
        var (other, _) = await s.W.NewBranchAsync();
        // Access to both branches, but the stock codes only at `other`: PermittedBranches = {other}; the user's own branch store is not visible.
        var user = await BranchHolderAsync(s.W, [s.Branch, other], other, "Inventory.View");

        var overview = Row(await GetOkAsync(s.W, "/api/v1/stock/overview?search=STN-PEN", user, other), s.W.ProductId);
        var locations = LocationQty(overview.GetProperty("locations"));

        Assert.False(locations.ContainsKey(s.BranchStore));
        Assert.False(locations.ContainsKey(DefaultTenantStore));
        Assert.Equal(20m, locations[s.CompanyWarehouse]);
    }

    // ---- posting through the API: request mapping and 400 field ----------------------------------------------------------------------

    private static async Task<string[]> ErrorFieldsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToArray();
    }

    [Fact]
    public async Task CreateDocuments_WithAStockLocationId_PostThereAndEchoIt_AndInvalidChoicesAre400OnStockLocationId()
    {
        var s = await SeedAsync();
        var w = s.W;

        // Purchase into the company warehouse (admin holds Purchase.Create company-wide) and a sale from the branch's own store.
        var purchase = await w.SendAsync(HttpMethod.Post, "/api/v1/purchases", new CreatePurchaseRequest(w.SupplierId, Today, null, null, PaymentMethod.Cash, 0m,
            [new CreatePurchaseLineRequest(w.ProductId, w.ProductUnitId, 3m, 5m, 0, 0)], null, s.CompanyWarehouse), s.Branch);
        Check(purchase, "purchase into the company warehouse");
        Assert.Equal(s.CompanyWarehouse, await DocumentLocationAsync(w, "PurchaseOrders", (await JsonAsync(purchase)).GetProperty("id").GetGuid()));

        var sale = await w.SendAsync(HttpMethod.Post, "/api/v1/sales", new CreateSaleRequest(w.CustomerId, Today, PaymentMethod.Cash, 0m,
            [new CreateSaleLineRequest(w.ProductId, w.ProductUnitId, 1m, 20m, 0)], null, s.BranchStore), s.Branch);
        Check(sale, "sale at the branch store");
        Assert.Equal(s.BranchStore, await DocumentLocationAsync(w, "SalesInvoices", (await JsonAsync(sale)).GetProperty("id").GetGuid()));

        // A warehouse cannot sell; an unknown id and another branch's store are the same 400 on stockLocationId.
        foreach (var bad in new[] { s.CompanyWarehouse, Guid.NewGuid(), DefaultTenantStore })
        {
            var refused = await w.SendAsync(HttpMethod.Post, "/api/v1/sales", new CreateSaleRequest(w.CustomerId, Today, PaymentMethod.Cash, 0m,
                [new CreateSaleLineRequest(w.ProductId, w.ProductUnitId, 1m, 20m, 0)], null, bad), s.Branch);
            Assert.Contains("stocklocationid", await ErrorFieldsAsync(refused));
        }
        var badPurchase = await w.SendAsync(HttpMethod.Post, "/api/v1/purchases", new CreatePurchaseRequest(w.SupplierId, Today, null, null, PaymentMethod.Cash, 0m,
            [new CreatePurchaseLineRequest(w.ProductId, w.ProductUnitId, 1m, 5m, 0, 0)], null, DefaultTenantStore), s.Branch);
        Assert.Contains("stocklocationid", await ErrorFieldsAsync(badPurchase));
        var transit = await ScalarTransitAsync(w);
        var badAdjustment = await w.SendAsync(HttpMethod.Post, "/api/v1/stock-adjustments", new CreateStockAdjustmentRequest(w.ProductId, w.ProductUnitId, AdjustmentType.Correction, AdjustmentDirection.Increase, 1m, 5m, "x", transit), s.Branch);
        Assert.Contains("stocklocationid", await ErrorFieldsAsync(badAdjustment));
    }

    private static async Task<Guid> DocumentLocationAsync(BranchApiWorld w, string table, Guid id) =>
        await CompanyMigrations.ScalarAsync<Guid>(w.Host.Database, $"""SELECT "StockLocationId" FROM "{table}" WHERE "Id" = '{id}'""");

    /// <summary>Design section 6: list/detail of the documents expose StockLocationId / Code / Name (additive). Currently only stock movements do.</summary>
    [Fact]
    public async Task DocumentResponses_ExposeTheStockLocation_PerDesignSection6()
    {
        var s = await SeedAsync();
        var w = s.W;
        var purchase = await w.PurchaseAsync(s.Branch, qty: 2m);
        Check(purchase, "purchase");
        var sale = await w.SaleAsync(s.Branch, qty: 1m);
        Check(sale, "sale");

        var purchaseDetail = await JsonAsync(purchase);
        var saleDetail = await JsonAsync(sale);

        Assert.Equal(s.BranchStore, purchaseDetail.GetProperty("stockLocationId").GetGuid());
        Assert.False(string.IsNullOrEmpty(purchaseDetail.GetProperty("stockLocationCode").GetString()));
        Assert.Equal(s.BranchStore, saleDetail.GetProperty("stockLocationId").GetGuid());
        Assert.False(string.IsNullOrEmpty(saleDetail.GetProperty("stockLocationName").GetString()));
    }

    private static async Task<Guid> ScalarTransitAsync(BranchApiWorld w)
    {
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        return await db.StockLocations.Where(l => l.LocationType == StockLocationType.Transit).Select(l => l.Id).SingleAsync();
    }
}
