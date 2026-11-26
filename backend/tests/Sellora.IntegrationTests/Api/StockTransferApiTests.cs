using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.Api.BranchApiWorld;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Phase 9 WP3 over HTTP on PostgreSQL (design section 6): every endpoint behind its own StockTransfers.* policy (403 without the code), the Branch filter
/// (a hidden transfer is a 404, list narrows, direction relative to the working branch), nested field names on 400, idempotent replay of create / dispatch /
/// receipt, the lifecycle through the real pipeline, Transit hidden from branch-scoped stock reads, and another company's ids (404 / 400, nothing changed).
/// </summary>
[Collection(PostgresCollection.Name)]
public class StockTransferApiTests(PostgresFixture postgres)
{
    private static readonly SemaphoreSlim RoleGate = new(1, 1);
    private static readonly Dictionary<string, Guid> RoleByCode = [];

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, object? body = null, Guid? branch = null, string? key = null)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: ApiHost.Json);
        if (branch is { } b) request.Headers.Add("X-Branch-Id", b.ToString());
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response) => await RestrictedUserWorld.JsonOkAsync(response);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await JsonAsync(response);

    private static Guid IdOf(JsonElement e) => e.GetProperty("id").GetGuid();

    private async Task<(Guid Pc, Guid Box, Guid Half)> UnitsAsync(BranchApiWorld w)
    {
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        var rows = await db.ProductUnits.AsNoTracking().Where(u => u.ProductId == w.ProductId).Include(u => u.Unit).ToListAsync();
        return (rows.Single(r => r.Unit.ShortCode == "pc").Id, rows.Single(r => r.Unit.ShortCode == "box").Id, rows.Single(r => r.Unit.ShortCode == "hctn").Id);
    }

    private static async Task<Guid> StoreOfAsync(BranchApiWorld w, Guid branch)
    {
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        return await Sellora.IntegrationTests.TestData.StockSeed.StoreOfAsync(db, branch);
    }

    private static async Task<decimal> QohAsync(BranchApiWorld w, Guid location)
    {
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        return await db.StockItems.Where(s => s.StockLocationId == location && s.ProductId == w.ProductId).Select(s => s.QuantityOnHand).SingleOrDefaultAsync();
    }

    private async Task<Guid> NewWarehouseLocationAsync(BranchApiWorld w, Guid? branch)
    {
        var code = "TW" + Guid.NewGuid().ToString("N")[..6];
        var response = await w.SendAsync(HttpMethod.Post, "/api/v1/warehouses", new CreateWarehouseRequest(code, "Transfer test " + code, branch));
        Check(response, "create warehouse");
        return (await BodyAsync(response)).GetProperty("stockLocationId").GetGuid();
    }

    private static object CreateBody(Guid source, Guid destination, Guid productId, Guid unit, decimal qty, bool submit = true) =>
        new CreateStockTransferRequest(source, destination, "api test", submit, [new StockTransferLineRequest(productId, unit, qty)]);

    private static async Task<JsonElement> CreateOkAsync(BranchApiWorld w, HttpClient client, Guid source, Guid destination, Guid unit, decimal qty, Guid? branch, bool submit = true, string? key = null)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/v1/stock-transfers", CreateBody(source, destination, w.ProductId, unit, qty, submit), branch, key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await BodyAsync(response);
    }

    private static object DispatchBody(JsonElement transfer, Guid unit, decimal qty) =>
        new DispatchStockTransferRequest(transfer.GetProperty("lines").EnumerateArray().Select(l => new DispatchStockTransferLineRequest(l.GetProperty("id").GetGuid(), unit, qty)).ToList());

    /// <summary>FI-D3 (Phase 05): production no longer dispatches a request directly, so the legacy-dispatched state (what transfers dispatched before the cut-over look like) is seeded
    /// with the fixture, and the transfer is read back as the API shows it.</summary>
    private static async Task<JsonElement> SeedDispatchedAsync(BranchApiWorld w, JsonElement transfer, Guid unit, decimal qty, Guid branch)
    {
        await using (var db = w.Host.Database.CreateDbContext(w.CompanyId))
        {
            var lines = transfer.GetProperty("lines").EnumerateArray().Select(l => new DispatchStockTransferLineInput(l.GetProperty("id").GetGuid(), unit, qty)).ToList();
            await LegacyTransferSeed.DispatchAsync(db, w.AdminId, IdIn(transfer), lines);
        }
        return await OkAsync(await SendAsync(w.Admin, HttpMethod.Get, $"/api/v1/stock-transfers/{IdIn(transfer)}", null, branch));
    }

    private static Guid IdIn(JsonElement transfer) => transfer.GetProperty("id").GetGuid();

    private static object ReceiveBody(JsonElement transfer, Guid unit, decimal accepted, decimal damaged = 0m) =>
        new ReceiveStockTransferRequest(null, transfer.GetProperty("lines").EnumerateArray().Select(l => new ReceiveStockTransferLineRequest(l.GetProperty("id").GetGuid(), unit, accepted, damaged)).ToList());

    // ---- lifecycle through the real pipeline ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Lifecycle_CreateApproveDispatchReceiveResolve_ThroughTheApi_ReconcilesAndShapesTheDto()
    {
        var w = await BranchApiWorld.GetAsync(postgres);
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        await w.EnsureBranchStockAsync(y, 500m);
        var (_, box, _) = await UnitsAsync(w);
        var (xStore, yStore) = (await StoreOfAsync(w, x), await StoreOfAsync(w, y));
        var transit = await TransitAsync(w);
        var transitBefore = await QohAsync(w, transit); // Transit is company-wide and shared with the other tests of this host

        var created = await CreateOkAsync(w, w.Admin, yStore, xStore, box, 10m, y);
        var id = IdOf(created);
        Assert.Equal("Requested", created.GetProperty("status").GetString());
        Assert.StartsWith("TRF", created.GetProperty("transferNo").GetString());
        Assert.Equal((y, "BranchStore"), (created.GetProperty("branch").GetProperty("id").GetGuid(), created.GetProperty("from").GetProperty("type").GetString()));
        var line = created.GetProperty("lines")[0];
        Assert.Equal((box, "box", 10m, 100m), (line.GetProperty("productUnitId").GetGuid(), line.GetProperty("unitCode").GetString(), line.GetProperty("requestedQty").GetDecimal(), line.GetProperty("requestedBaseQty").GetDecimal()));

        var approved = await OkAsync(await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{id}/approve", null, y));
        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        var dispatched = await SeedDispatchedAsync(w, approved, box, 10m, y);
        Assert.Equal("Dispatched", dispatched.GetProperty("status").GetString());
        var dispatchedLine = dispatched.GetProperty("lines")[0];
        Assert.Equal((100m, "box"), (dispatchedLine.GetProperty("dispatchedBaseQty").GetDecimal(), dispatchedLine.GetProperty("dispatchUnitCode").GetString()));
        var batch = Assert.Single(dispatchedLine.GetProperty("batches").EnumerateArray());
        Assert.Equal((5m, 100m), (batch.GetProperty("unitCost").GetDecimal(), batch.GetProperty("outstandingBaseQty").GetDecimal()));
        Assert.Equal((400m, 100m), (await QohAsync(w, yStore), await QohAsync(w, transit) - transitBefore));

        // Receive 6 boxes + 1 damaged at x; the receipt response carries the receipt id and the transfer.
        var receipt = await OkAsync(await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{id}/receipts", ReceiveBody(dispatched, box, 6m, 1m), x));
        Assert.NotEqual(Guid.Empty, receipt.GetProperty("receiptId").GetGuid());
        Assert.Equal("PartiallyReceived", receipt.GetProperty("transfer").GetProperty("status").GetString());
        Assert.Equal(30m, receipt.GetProperty("transfer").GetProperty("lines")[0].GetProperty("outstandingBaseQty").GetDecimal());

        // Excess is a 400 on the nested field.
        var excess = await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{id}/receipts", ReceiveBody(dispatched, box, 4m), x);
        Assert.Equal(HttpStatusCode.BadRequest, excess.StatusCode);
        Assert.Contains("lines[0].acceptedquantity", await ErrorFieldsAsync(excess));

        // Resolve the rest back to the source (company-level admin), which completes the transfer.
        var resolved = await OkAsync(await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{id}/resolve",
            new ResolveStockTransferRequest("returning", dispatched.GetProperty("lines").EnumerateArray().Select(l => new ResolveStockTransferLineRequest(l.GetProperty("id").GetGuid(), Sellora.Domain.Stock.StockTransferResolution.ReturnToSource)).ToList()), x));
        Assert.Equal("Completed", resolved.GetProperty("transfer").GetProperty("status").GetString());
        Assert.Equal((430m, 60m, 0m), (await QohAsync(w, yStore), await QohAsync(w, xStore), await QohAsync(w, transit) - transitBefore));

        var detail = await OkAsync(await SendAsync(w.Admin, HttpMethod.Get, $"/api/v1/stock-transfers/{id}", null, y));
        Assert.Equal(2, detail.GetProperty("receipts").GetArrayLength());
        Assert.Equal(["Receipt", "Resolution"], detail.GetProperty("receipts").EnumerateArray().Select(r => r.GetProperty("kind").GetString()));
        Assert.NotNull(detail.GetProperty("dispatched").GetProperty("userName").GetString());
        await Sellora.IntegrationTests.TestData.TransferValidation.AssertCleanAsync(w.Host.Database, "api lifecycle");
    }

    private static async Task<Guid> TransitAsync(BranchApiWorld w)
    {
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        return await db.StockLocations.Where(l => l.LocationType == Sellora.Domain.Inventory.StockLocationType.Transit).Select(l => l.Id).SingleAsync();
    }

    // ---- 400 field mapping --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_InvalidInput_Is400_WithNestedFieldNames_AndLegacyDispatchIsAlways400OnStatus()
    {
        var w = await BranchApiWorld.GetAsync(postgres);
        var (x, _) = await w.NewBranchAsync();
        await w.EnsureBranchStockAsync(x, 100m);
        var (pc, box, _) = await UnitsAsync(w);
        var xStore = await StoreOfAsync(w, x);
        var main = await StoreOfAsync(w, w.MainId);

        async Task<List<string>> Fields(object body)
        {
            var response = await SendAsync(w.Admin, HttpMethod.Post, "/api/v1/stock-transfers", body, x);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await ErrorFieldsAsync(response);
        }

        Assert.Contains("lines[0].quantity", await Fields(CreateBody(xStore, main, w.ProductId, box, 0.5m)));                      // fraction of a Box (0 decimals)
        Assert.Contains("lines[0].productunitid", await Fields(CreateBody(xStore, main, w.ProductId, Guid.NewGuid(), 1m)));         // unknown unit
        Assert.Contains("lines[0].productid", await Fields(CreateBody(xStore, main, Guid.NewGuid(), pc, 1m)));                       // unknown product
        Assert.Contains("destinationlocationid", await Fields(CreateBody(xStore, xStore, w.ProductId, pc, 1m)));                     // same endpoints
        Assert.Contains("sourcelocationid", await Fields(CreateBody(Guid.NewGuid(), main, w.ProductId, pc, 1m)));                    // unknown location
        Assert.Contains("destinationlocationid", await Fields(CreateBody(xStore, await TransitAsync(w), w.ProductId, pc, 1m)));    // Transit
        Assert.Contains("lines[0].quantity", await Fields(new CreateStockTransferRequest(xStore, main, null, true, [new StockTransferLineRequest(w.ProductId, pc, -1m)]))); // [Range] annotation
        Assert.Contains("sourcelocationid", await Fields(new { destinationLocationId = main, submit = true, lines = new[] { new { productId = w.ProductId, productUnitId = pc, quantity = 1m } } })); // [Required]

        var t = await CreateOkAsync(w, w.Admin, xStore, main, box, 2m, x);
        await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(t)}/approve", null, x);
        var tooMuch = await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(t)}/dispatch", DispatchBody(t, box, 3m), x);
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
        Assert.Contains("status", await ErrorFieldsAsync(tooMuch)); // FI-D3: whatever the body, dispatch goes through a shipment
        var notApproved = await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(await CreateOkAsync(w, w.Admin, xStore, main, box, 1m, x))}/dispatch", DispatchBody(t, box, 1m), x);
        Assert.Equal(HttpStatusCode.BadRequest, notApproved.StatusCode);
        Assert.Contains("status", await ErrorFieldsAsync(notApproved));
    }

    [Fact]
    public async Task StatusErrors_AreOn400FieldStatus_AndAnUnknownIdIs404()
    {
        var w = await BranchApiWorld.GetAsync(postgres);
        var (x, _) = await w.NewBranchAsync();
        await w.EnsureBranchStockAsync(x, 100m);
        var (_, box, _) = await UnitsAsync(w);
        var (xStore, main) = (await StoreOfAsync(w, x), await StoreOfAsync(w, w.MainId));
        var t = await CreateOkAsync(w, w.Admin, xStore, main, box, 1m, x, submit: false);

        var early = await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(t)}/dispatch", DispatchBody(t, box, 1m), x);
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        Assert.Contains("status", await ErrorFieldsAsync(early));
        var cancel = await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(t)}/cancel", new StockTransferReasonRequest("not needed"), x);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var again = await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(t)}/cancel", new StockTransferReasonRequest("not needed"), x); // idempotent by state
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var approve = await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(t)}/approve", null, x);
        Assert.Equal(HttpStatusCode.BadRequest, approve.StatusCode);
        Assert.Contains("status", await ErrorFieldsAsync(approve));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(w.Admin, HttpMethod.Get, $"/api/v1/stock-transfers/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{Guid.NewGuid()}/approve")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(t)}/reject", new { reason = "" })).StatusCode);
    }

    // ---- idempotent replay --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_AndReceipt_ReplayedWithTheSameKey_ReturnTheStoredResult_AndPostOnce()
    {
        var w = await BranchApiWorld.GetAsync(postgres);
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        await w.EnsureBranchStockAsync(y, 300m);
        var (_, box, _) = await UnitsAsync(w);
        var (xStore, yStore) = (await StoreOfAsync(w, x), await StoreOfAsync(w, y));
        var key = Guid.NewGuid().ToString("N");

        var first = await CreateOkAsync(w, w.Admin, yStore, xStore, box, 5m, y, key: key);
        var replay = await CreateOkAsync(w, w.Admin, yStore, xStore, box, 5m, y, key: key);
        Assert.Equal((IdOf(first), first.GetProperty("transferNo").GetString()), (IdOf(replay), replay.GetProperty("transferNo").GetString()));
        var list = await OkAsync(await SendAsync(w.Admin, HttpMethod.Get, $"/api/v1/stock-transfers?pageSize=100&destinationLocationId={xStore}", null, x)); // FI-D11: x's store is read from x (the receiving, active branch)
        Assert.Single(list.GetProperty("items").EnumerateArray());

        var id = IdOf(first);
        var approved = await OkAsync(await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{id}/approve", null, y));
        // The legacy dispatch is refused even with a key (FI-D3); the receipt replay below runs on the seeded legacy-dispatched state.
        var refused = await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{id}/dispatch", DispatchBody(approved, box, 5m), y, key + "d");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("status", await ErrorFieldsAsync(refused));
        approved = await SeedDispatchedAsync(w, approved, box, 5m, y);
        Assert.Equal(250m, await QohAsync(w, yStore));

        var r1 = await OkAsync(await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{id}/receipts", ReceiveBody(approved, box, 5m), x, key + "r"));
        var r2 = await OkAsync(await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{id}/receipts", ReceiveBody(approved, box, 5m), x, key + "r"));
        Assert.Equal(r1.GetProperty("receiptId").GetGuid(), r2.GetProperty("receiptId").GetGuid());
        Assert.Equal(50m, await QohAsync(w, xStore));

        var badKey = await SendAsync(w.Admin, HttpMethod.Post, "/api/v1/stock-transfers", CreateBody(yStore, xStore, w.ProductId, box, 1m), y, new string('k', 101));
        Assert.Equal(HttpStatusCode.BadRequest, badKey.StatusCode);
    }

    // ---- policies --------------------------------------------------------------------------------------------------------------------------

    private static async Task<Guid> RoleOfAsync(BranchApiWorld w, params string[] codes)
    {
        var name = "TT " + string.Join("+", codes.Select(c => c.Replace("StockTransfers.", "")));
        await RoleGate.WaitAsync();
        try
        {
            if (RoleByCode.TryGetValue(name, out var id)) return id;
            var response = await w.Admin.PostAsJsonAsync("/api/v1/roles", new CreateRoleRequest(name, null, codes), ApiHost.Json);
            Check(response, "create role " + name);
            return RoleByCode[name] = (await BodyAsync(response)).GetProperty("id").GetGuid();
        }
        finally { RoleGate.Release(); }
    }

    /// <summary>A user allocated to <paramref name="branch"/> only, holding <paramref name="codes"/> through a role scoped to that branch (the permission is branch-level, not company-level).</summary>
    private static async Task<RestrictedUser> BranchUserAsync(RestrictedUserWorld r, Guid branch, params string[] codes)
    {
        var response = await r.World.SendAsync(HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest("BR " + Guid.NewGuid().ToString("N")[..8], null, codes, RoleScope.Branch, branch));
        Check(response, "create branch role");
        var role = (await BodyAsync(response)).GetProperty("id").GetGuid();
        // A user needs a company role to be created; give it a harmless one and add the branch role by assignment.
        var user = await r.CreateAsync("bu", await RoleOfAsync(r.World, "Sales.View"), CompanyAccessScope.AssignedBranches, [branch], branch);
        var assigned = await r.World.SendAsync(HttpMethod.Post, $"/api/v1/users/{user.Id}/roles", new AssignRoleRequest(role, null));
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
        return user;
    }

    private static string Route(string template, Guid id) => template.Replace("{id}", id.ToString());

    private static object? BodyFor(string key, Guid id) => key switch
    {
        "create" => CreateBody(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1m),
        "update" => new UpdateStockTransferRequest(Guid.NewGuid(), Guid.NewGuid(), null, [new StockTransferLineRequest(Guid.NewGuid(), Guid.NewGuid(), 1m)]),
        "reason" => new StockTransferReasonRequest("because"),
        "dispatch" => new DispatchStockTransferRequest([new DispatchStockTransferLineRequest(Guid.NewGuid(), Guid.NewGuid(), 1m)]),
        "receive" => new ReceiveStockTransferRequest(null, [new ReceiveStockTransferLineRequest(Guid.NewGuid(), Guid.NewGuid(), 1m, 0m)]),
        "resolve" => new ResolveStockTransferRequest("because", [new ResolveStockTransferLineRequest(Guid.NewGuid(), Sellora.Domain.Stock.StockTransferResolution.WriteOff)]),
        _ => null,
    };

    public static IEnumerable<object[]> Endpoints() =>
    [
        ["GET", "/api/v1/stock-transfers", "StockTransfers.View", "none"],
        ["GET", "/api/v1/stock-transfers/{id}", "StockTransfers.View", "none"],
        ["POST", "/api/v1/stock-transfers", "StockTransfers.Create", "create"],
        ["PUT", "/api/v1/stock-transfers/{id}", "StockTransfers.Create", "update"],
        ["POST", "/api/v1/stock-transfers/{id}/submit", "StockTransfers.Create", "none"],
        ["POST", "/api/v1/stock-transfers/{id}/approve", "StockTransfers.Approve", "none"],
        ["POST", "/api/v1/stock-transfers/{id}/reject", "StockTransfers.Approve", "reason"],
        ["POST", "/api/v1/stock-transfers/{id}/requester-approve", "StockTransfers.ApproveRequest", "none"],
        ["POST", "/api/v1/stock-transfers/{id}/requester-reject", "StockTransfers.ApproveRequest", "reason"],
        ["POST", "/api/v1/stock-transfers/{id}/cancel", "StockTransfers.Cancel", "reason"],
        ["POST", "/api/v1/stock-transfers/{id}/dispatch", "StockTransfers.Dispatch", "dispatch"],
        ["POST", "/api/v1/stock-transfers/{id}/receipts", "StockTransfers.Receive", "receive"],
        ["POST", "/api/v1/stock-transfers/{id}/resolve", "StockTransfers.Resolve", "resolve"],
    ];

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Endpoint_WithoutItsPermissionCode_Is403_AndWithOnlyThatCode_IsAuthorized(string method, string template, string code, string bodyKind)
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var other = TransferCodesExcept(code);
        var without = await r.CreateAsync("pol", await RoleOfAsync(w, other), CompanyAccessScope.CompanyWide);
        var with = await r.CreateAsync("pol", await RoleOfAsync(w, code), CompanyAccessScope.CompanyWide);
        var id = Guid.NewGuid();

        var denied = await SendAsync(without.Client, new HttpMethod(method), Route(template, id), BodyFor(bodyKind, id));
        var allowed = await SendAsync(with.Client, new HttpMethod(method), Route(template, id), BodyFor(bodyKind, id));

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed.StatusCode);
        Assert.True((int)allowed.StatusCode is 200 or 400 or 404, $"{method} {template} with {code}: {(int)allowed.StatusCode}"); // authorized, then a business answer for a random id
    }

    private static string[] TransferCodesExcept(string code) => Sellora.IntegrationTests.TestData.TransferScenario.TransferCodes.Where(c => c != code).ToArray();

    [Fact]
    public async Task Unauthenticated_IsUnauthorized()
    {
        var w = await BranchApiWorld.GetAsync(postgres);
        using var anonymous = w.Host.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/stock-transfers")).StatusCode);
    }

    [Fact]
    public async Task Resolve_IsCompanyOnly_ABranchScopedRoleCannotHoldIt_TheOtherTransferCodesCan()
    {
        var w = await BranchApiWorld.GetAsync(postgres);
        var (x, _) = await w.NewBranchAsync();
        string Name() => "BR " + Guid.NewGuid().ToString("N")[..8];

        var resolve = await w.SendAsync(HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(Name(), null, ["StockTransfers.Resolve"], RoleScope.Branch, x));
        Assert.Equal(HttpStatusCode.BadRequest, resolve.StatusCode);
        var others = await w.SendAsync(HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest(Name(), null, Sellora.IntegrationTests.TestData.TransferScenario.TransferCodes.Where(c => c != "StockTransfers.Resolve").ToArray(), RoleScope.Branch, x));
        Assert.Equal(HttpStatusCode.Created, others.StatusCode);
    }

    // ---- Branch filter, list filters, direction -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task BranchFilter_HiddenTransferIs404_ListNarrows_DirectionIsRelativeToTheWorkingBranch()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        await w.EnsureBranchStockAsync(x, 200m); await w.EnsureBranchStockAsync(y, 200m);
        var (_, box, _) = await UnitsAsync(w);
        var (xStore, yStore) = (await StoreOfAsync(w, x), await StoreOfAsync(w, y));
        var yWarehouse = await NewWarehouseLocationAsync(w, y);

        var hidden = await CreateOkAsync(w, w.Admin, yStore, yWarehouse, box, 1m, y);                // y -> y: nothing of x's
        var outgoing = await CreateOkAsync(w, w.Admin, xStore, yStore, box, 1m, x);                  // x -> y
        var incoming = await CreateOkAsync(w, w.Admin, yStore, xStore, box, 1m, y);                  // y -> x
        var user = await r.CreateAtAsync(x);

        // The Branch filter hides y's own transfer: 404 on detail and on every write (not 403: nothing is revealed).
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(user.Client, HttpMethod.Get, $"/api/v1/stock-transfers/{IdOf(hidden)}", null, x)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(user.Client, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(hidden)}/approve", null, x)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(user.Client, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(hidden)}/cancel", new StockTransferReasonRequest("x"), x)).StatusCode);
        Assert.Equal("Requested", StockTransferStatusOf(await OkAsync(await SendAsync(w.Admin, HttpMethod.Get, $"/api/v1/stock-transfers/{IdOf(hidden)}", null, y))));

        async Task<Guid[]> Ids(string query) =>
            (await OkAsync(await SendAsync(user.Client, HttpMethod.Get, "/api/v1/stock-transfers?pageSize=100" + query, null, x))).GetProperty("items").EnumerateArray().Select(IdOf).ToArray();

        var all = await Ids("");
        Assert.Contains(IdOf(outgoing), all); Assert.Contains(IdOf(incoming), all); Assert.DoesNotContain(IdOf(hidden), all);
        Assert.Equal([IdOf(outgoing)], await Ids("&direction=outgoing"));
        Assert.Equal([IdOf(incoming)], await Ids("&direction=incoming"));
        Assert.Equal([IdOf(outgoing)], await Ids($"&sourceLocationId={xStore}"));
        Assert.Equal([IdOf(incoming)], await Ids($"&destinationLocationId={xStore}"));
        Assert.Equal(all.Length, (await Ids("&status=Requested")).Length);
        Assert.Empty(await Ids("&status=Completed"));
        Assert.Contains(IdOf(outgoing), await Ids($"&dateFrom={DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)):yyyy-MM-dd}&dateTo={DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd}"));
        Assert.Empty(await Ids("&dateTo=2000-01-01"));
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(user.Client, HttpMethod.Get, "/api/v1/stock-transfers?direction=sideways", null, x)).StatusCode);

        // List rows carry the counts (quantities are never summed across products).
        var row = (await OkAsync(await SendAsync(user.Client, HttpMethod.Get, $"/api/v1/stock-transfers?pageSize=100&sourceLocationId={xStore}", null, x))).GetProperty("items")[0];
        Assert.Equal((1, 0), (row.GetProperty("lineCount").GetInt32(), row.GetProperty("outstandingLineCount").GetInt32()));
        Assert.Equal("Requested", row.GetProperty("status").GetString());

        // FI-D11: the company-level admin reads the ACTIVE branch only. y touches all three transfers (y->y, x->y, y->x); x does not touch the y->y one.
        async Task<HashSet<Guid>> AdminIds(Guid at) => (await OkAsync(await SendAsync(w.Admin, HttpMethod.Get, "/api/v1/stock-transfers?pageSize=100", null, at))).GetProperty("items").EnumerateArray().Select(IdOf).ToHashSet();
        Assert.Superset(new HashSet<Guid> { IdOf(hidden), IdOf(outgoing), IdOf(incoming) }, await AdminIds(y));
        var adminAtX = await AdminIds(x);
        Assert.Superset(new HashSet<Guid> { IdOf(outgoing), IdOf(incoming) }, adminAtX);
        Assert.DoesNotContain(IdOf(hidden), adminAtX);
    }

    private static string StockTransferStatusOf(JsonElement dto) => dto.GetProperty("status").GetString()!;

    [Fact]
    public async Task BranchScopedUser_CanWorkItsOwnSide_AndIsRefusedTheOthers()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        await w.EnsureBranchStockAsync(x, 200m);
        var (_, box, _) = await UnitsAsync(w);
        var (xStore, yStore) = (await StoreOfAsync(w, x), await StoreOfAsync(w, y));
        var userX = await BranchUserAsync(r, x, "StockTransfers.View", "StockTransfers.Create", "StockTransfers.Approve", "StockTransfers.Dispatch", "StockTransfers.Receive", "StockTransfers.Cancel");

        // x -> y created by x's user (source fits), approved and dispatched by x's user (source fits); y's store is not x's, so x cannot receive.
        var t = await CreateOkAsync(w, userX.Client, xStore, yStore, box, 2m, x);
        var approved = await OkAsync(await SendAsync(userX.Client, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(t)}/approve", null, x));
        var dispatched = await SeedDispatchedAsync(w, approved, box, 2m, x); // legacy-dispatched state (FI-D3): the dispatch itself is a shipment concern now
        Assert.Equal("Dispatched", dispatched.GetProperty("status").GetString());
        var receive = await SendAsync(userX.Client, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(t)}/receipts", ReceiveBody(dispatched, box, 2m), x);
        Assert.Equal(HttpStatusCode.BadRequest, receive.StatusCode);
        Assert.Contains("stocklocationid", await ErrorFieldsAsync(receive));
        // y -> x created by x's user: the destination fits.
        await w.EnsureBranchStockAsync(y, 200m);
        var incoming = await SendAsync(userX.Client, HttpMethod.Post, "/api/v1/stock-transfers", CreateBody(yStore, xStore, w.ProductId, box, 1m), x);
        Assert.Equal(HttpStatusCode.Created, incoming.StatusCode);
        // y -> y warehouse: neither endpoint is x's.
        var yWarehouse = await NewWarehouseLocationAsync(w, y);
        var foreign = await SendAsync(userX.Client, HttpMethod.Post, "/api/v1/stock-transfers", CreateBody(yStore, yWarehouse, w.ProductId, box, 1m), x);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
    }

    // ---- Transit is hidden from branch-scoped stock reads --------------------------------------------------------------------------------

    [Fact]
    public async Task Transit_IsVisibleToTheCompanyLevelAdmin_AndHiddenFromABranchScopedReader()
    {
        var r = await RestrictedUserWorld.GetWorldAsync(postgres); var w = r.World;
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        await w.EnsureBranchStockAsync(y, 300m);
        var (_, box, _) = await UnitsAsync(w);
        var (xStore, yStore) = (await StoreOfAsync(w, x), await StoreOfAsync(w, y));
        var transit = await TransitAsync(w);
        var t = await CreateOkAsync(w, w.Admin, yStore, xStore, box, 3m, y);
        var approved = await OkAsync(await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(t)}/approve", null, y));
        await SeedDispatchedAsync(w, approved, box, 3m, y);
        var user = await BranchUserAsync(r, x, "Inventory.View"); // Inventory.View held at branch x only

        // FI-D11: goods in flight (Transit) appear only in the explicit company view (?scope=company); the default active-branch read never shows them.
        var adminOverview = await OkAsync(await SendAsync(w.Admin, HttpMethod.Get, "/api/v1/stock/overview?search=STN-PEN&pageSize=100&scope=company", null));
        var adminRow = adminOverview.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("productId").GetGuid() == w.ProductId);
        Assert.Contains(adminRow.GetProperty("locations").EnumerateArray(), l => l.GetProperty("locationId").GetGuid() == transit && l.GetProperty("qty").GetDecimal() >= 30m);
        var adminMovements = await OkAsync(await SendAsync(w.Admin, HttpMethod.Get, $"/api/v1/reports/stock-movement?scope=company&stockLocationId={transit}&pageSize=100", null));
        Assert.Contains(adminMovements.GetProperty("items").EnumerateArray(), m => m.GetProperty("type").GetString() == "TransferIn" && m.GetProperty("referenceType").GetString() == "StockTransfer"); // dispatch books INTO Transit (the TransferOut sits at the source store)
        var adminDefault = await OkAsync(await SendAsync(w.Admin, HttpMethod.Get, $"/api/v1/stock/{w.ProductId}"));
        Assert.DoesNotContain(adminDefault.GetProperty("locations").EnumerateArray(), l => l.GetProperty("locationId").GetGuid() == transit);

        var userDetail = await OkAsync(await SendAsync(user.Client, HttpMethod.Get, $"/api/v1/stock/{w.ProductId}", null, x));
        Assert.DoesNotContain(userDetail.GetProperty("locations").EnumerateArray(), l => l.GetProperty("locationId").GetGuid() == transit);
        Assert.DoesNotContain(userDetail.GetProperty("movements").EnumerateArray(), m => m.GetProperty("stockLocationId").GetGuid() == transit);
        var explicitTransit = await SendAsync(user.Client, HttpMethod.Get, $"/api/v1/stock/{w.ProductId}?stockLocationId={transit}", null, x);
        Assert.Equal(HttpStatusCode.Forbidden, explicitTransit.StatusCode);
    }

    // ---- another company's ids -------------------------------------------------------------------------------------------------------------

    private static readonly SemaphoreSlim IsolationGate = new(1, 1);
    private static IsolationWorld? _isolation;

    private async Task<IsolationWorld> IsolationAsync()
    {
        await IsolationGate.WaitAsync();
        try { return _isolation ??= await IsolationWorld.BuildAsync(postgres); }
        finally { IsolationGate.Release(); }
    }

    [Fact]
    public async Task OtherCompanysIds_Are404OrRefused_AndNothingChanges()
    {
        var iso = await IsolationAsync();
        using var a = iso.Host.NewClient(iso.A.Token);
        using var b = iso.Host.NewClient(iso.B.Token);
        Guid WarehouseLocation(Ids ids)
        {
            using var db = iso.Host.Database.CreateDbContext(ids.Company);
            return db.Warehouses.Where(x => x.Id == ids.WarehouseId).Select(x => x.StockLocationId).Single();
        }
        var (aWarehouse, bWarehouse) = (WarehouseLocation(iso.A), WarehouseLocation(iso.B));

        var created = await SendAsync(a, HttpMethod.Post, "/api/v1/stock-transfers",
            new CreateStockTransferRequest(iso.A.LocationId, aWarehouse, "isolation", true, [new StockTransferLineRequest(iso.A.ProductId, iso.A.ProductUnitId, 1m)]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var transfer = await BodyAsync(created);
        var id = IdOf(transfer);
        var bOwn = await SendAsync(b, HttpMethod.Post, "/api/v1/stock-transfers",
            new CreateStockTransferRequest(iso.B.LocationId, bWarehouse, "isolation b", false, [new StockTransferLineRequest(iso.B.ProductId, iso.B.ProductUnitId, 1m)]));
        Assert.Equal(HttpStatusCode.Created, bOwn.StatusCode);
        var bId = IdOf(await BodyAsync(bOwn));
        Assert.StartsWith("TRF", transfer.GetProperty("transferNo").GetString());

        // Reads: B never sees A's transfer (detail 404, list without it) and vice versa.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(b, HttpMethod.Get, $"/api/v1/stock-transfers/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(a, HttpMethod.Get, $"/api/v1/stock-transfers/{bId}")).StatusCode);
        Assert.DoesNotContain(id, (await OkAsync(await SendAsync(b, HttpMethod.Get, "/api/v1/stock-transfers?pageSize=100"))).GetProperty("items").EnumerateArray().Select(IdOf));
        Assert.DoesNotContain(bId, (await OkAsync(await SendAsync(a, HttpMethod.Get, "/api/v1/stock-transfers?pageSize=100"))).GetProperty("items").EnumerateArray().Select(IdOf));

        // Writes from B naming A's transfer: every one is a 404.
        var line = Guid.NewGuid();
        foreach (var (method, route, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Put, $"/api/v1/stock-transfers/{id}", new UpdateStockTransferRequest(iso.B.LocationId, bWarehouse, null, [new StockTransferLineRequest(iso.B.ProductId, iso.B.ProductUnitId, 1m)])),
            (HttpMethod.Post, $"/api/v1/stock-transfers/{id}/submit", null), (HttpMethod.Post, $"/api/v1/stock-transfers/{id}/approve", null),
            (HttpMethod.Post, $"/api/v1/stock-transfers/{id}/reject", new StockTransferReasonRequest("x")), (HttpMethod.Post, $"/api/v1/stock-transfers/{id}/cancel", new StockTransferReasonRequest("x")),
            (HttpMethod.Post, $"/api/v1/stock-transfers/{id}/requester-approve", null), (HttpMethod.Post, $"/api/v1/stock-transfers/{id}/requester-reject", new StockTransferReasonRequest("x")),
            (HttpMethod.Post, $"/api/v1/stock-transfers/{id}/receipts", new ReceiveStockTransferRequest(null, [new ReceiveStockTransferLineRequest(line, iso.B.ProductUnitId, 1m, 0m)])),
            (HttpMethod.Post, $"/api/v1/stock-transfers/{id}/resolve", new ResolveStockTransferRequest("x", [new ResolveStockTransferLineRequest(line, Sellora.Domain.Stock.StockTransferResolution.WriteOff)])),
        })
        {
            var response = await SendAsync(b, method, route, body);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {route}: {(int)response.StatusCode}");
        }

        // The legacy dispatch is a constant 400 (FI-D3) whoever asks and whichever transfer it names: it reveals nothing about the other company's transfer.
        var legacy = await SendAsync(b, HttpMethod.Post, $"/api/v1/stock-transfers/{id}/dispatch", new DispatchStockTransferRequest([new DispatchStockTransferLineRequest(line, iso.B.ProductUnitId, 1m)]));
        Assert.Equal(HttpStatusCode.BadRequest, legacy.StatusCode);

        // Creates naming the other company's locations / products are 400 on the field, and create nothing.
        var count = await ScalarAsync<long>(iso.Host.Database, """SELECT COUNT(*) FROM "StockTransfers" """);
        var foreignLocation = await SendAsync(b, HttpMethod.Post, "/api/v1/stock-transfers",
            new CreateStockTransferRequest(iso.A.LocationId, bWarehouse, null, true, [new StockTransferLineRequest(iso.B.ProductId, iso.B.ProductUnitId, 1m)]));
        Assert.Equal(HttpStatusCode.BadRequest, foreignLocation.StatusCode);
        Assert.Contains("sourcelocationid", await ErrorFieldsAsync(foreignLocation));
        var foreignDestination = await SendAsync(b, HttpMethod.Post, "/api/v1/stock-transfers",
            new CreateStockTransferRequest(iso.B.LocationId, aWarehouse, null, true, [new StockTransferLineRequest(iso.B.ProductId, iso.B.ProductUnitId, 1m)]));
        Assert.Contains("destinationlocationid", await ErrorFieldsAsync(foreignDestination));
        var foreignProduct = await SendAsync(b, HttpMethod.Post, "/api/v1/stock-transfers",
            new CreateStockTransferRequest(iso.B.LocationId, bWarehouse, null, true, [new StockTransferLineRequest(iso.A.ProductId, iso.A.ProductUnitId, 1m)]));
        Assert.Equal(HttpStatusCode.BadRequest, foreignProduct.StatusCode);
        Assert.Contains("lines[0].productid", await ErrorFieldsAsync(foreignProduct));
        var foreignUnit = await SendAsync(b, HttpMethod.Post, "/api/v1/stock-transfers",
            new CreateStockTransferRequest(iso.B.LocationId, bWarehouse, null, true, [new StockTransferLineRequest(iso.B.ProductId, iso.A.ProductUnitId, 1m)]));
        Assert.Equal(HttpStatusCode.BadRequest, foreignUnit.StatusCode);
        Assert.Equal(count, await ScalarAsync<long>(iso.Host.Database, """SELECT COUNT(*) FROM "StockTransfers" """));

        Assert.Equal("Requested", await ScalarAsync<string>(iso.Host.Database, $"""SELECT "Status" FROM "StockTransfers" WHERE "Id" = '{id}'"""));
        Assert.Equal(2L, await ScalarAsync<long>(iso.Host.Database, """SELECT COUNT(DISTINCT "CompanyId") FROM "StockTransfers" """));
    }
}
