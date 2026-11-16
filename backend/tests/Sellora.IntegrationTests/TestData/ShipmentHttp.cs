using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Api;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.TestData;

/// <summary>One shipping scene on the shared <see cref="BranchApiWorld"/>: a fresh source branch (stocked with exactly <c>stock</c> pieces of the Pen) and two fresh destination branches.</summary>
public sealed record ShipScene(BranchApiWorld W, Guid Src, Guid A, Guid B, Guid SrcStore, Guid AStore, Guid BStore, Guid Pc, Guid Box);

/// <summary>Phase 04 HTTP helpers shared by the shipment API and reservation race tests (the harness pattern of TransferRequestFulfilmentApiTests).</summary>
public static class ShipmentHttp
{
    public static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, object? body = null, Guid? branch = null, string? key = null)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: ApiHost.Json);
        if (branch is { } b) request.Headers.Add("X-Branch-Id", b.ToString());
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    public static Task<JsonElement> OkAsync(HttpResponseMessage response) => RestrictedUserWorld.JsonOkAsync(response);
    public static Guid IdOf(JsonElement e) => e.GetProperty("id").GetGuid();

    public static async Task<ShipScene> NewSceneAsync(PostgresFixture postgres, decimal stock = 10m)
    {
        var w = await BranchApiWorld.GetAsync(postgres);
        var (src, _) = await w.NewBranchAsync(); var (a, _) = await w.NewBranchAsync(); var (b, _) = await w.NewBranchAsync();
        if (stock > 0m) await w.EnsureBranchStockAsync(src, stock);
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        var box = (await db.ProductUnits.AsNoTracking().Where(u => u.ProductId == w.ProductId).Include(u => u.Unit).ToListAsync()).Single(r => r.Unit.ShortCode == "box").Id;
        return new ShipScene(w, src, a, b, await StockSeed.StoreOfAsync(db, src), await StockSeed.StoreOfAsync(db, a), await StockSeed.StoreOfAsync(db, b), w.ProductUnitId, box);
    }

    /// <summary>A request of <paramref name="pieces"/> Piece out of <paramref name="sourceStore"/> (default: the scene's source), approved by default; returns the transfer detail.</summary>
    public static async Task<JsonElement> RequestAsync(ShipScene s, Guid destinationStore, decimal pieces, bool approve = true, Guid? sourceBranch = null, Guid? sourceStore = null)
    {
        var (branch, store) = (sourceBranch ?? s.Src, sourceStore ?? s.SrcStore);
        var create = await SendAsync(s.W.Admin, HttpMethod.Post, "/api/v1/stock-transfers",
            new CreateStockTransferRequest(store, destinationStore, "shipment", true, [new StockTransferLineRequest(s.W.ProductId, s.Pc, pieces)]), branch);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await BranchApiWorld.JsonAsync(create);
        if (!approve) return created;
        return await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(created)}/approve", null, branch));
    }

    public static Guid LineOf(JsonElement transfer) => transfer.GetProperty("lines")[0].GetProperty("id").GetGuid();

    public static Task<HttpResponseMessage> CreateShipmentAsync(ShipScene s, TransportMode mode = TransportMode.Road, TransportVia via = TransportVia.OwnFleet,
        string? provider = null, string? reference = null, string? key = null, HttpClient? client = null, Guid? sourceStore = null) =>
        SendAsync(client ?? s.W.Admin, HttpMethod.Post, "/api/v1/shipments",
            new CreateShipmentRequest(sourceStore ?? s.SrcStore, mode, via, provider, null, reference, null, "note"), s.Src, key);

    public static async Task<JsonElement> NewShipmentAsync(ShipScene s, TransportMode mode = TransportMode.Road, TransportVia via = TransportVia.OwnFleet, string? provider = null, string? reference = null)
    {
        var response = await CreateShipmentAsync(s, mode, via, provider, reference);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await BranchApiWorld.JsonAsync(response);
    }

    public static Task<HttpResponseMessage> AllocateAsync(ShipScene s, Guid shipmentId, Guid requestLineId, decimal pieces, string? key = null, HttpClient? client = null) =>
        SendAsync(client ?? s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{shipmentId}/allocations", new AllocateShipmentRequest(requestLineId, s.Pc, pieces), s.Src, key);

    public static Task<HttpResponseMessage> GetShipmentAsync(ShipScene s, Guid id, HttpClient? client = null, Guid? branch = null) =>
        SendAsync(client ?? s.W.Admin, HttpMethod.Get, $"/api/v1/shipments/{id}", null, branch ?? s.Src);

    public static Task<HttpResponseMessage> CancelShipmentAsync(ShipScene s, Guid id, string reason = "cancel", string? key = null) =>
        SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{id}/cancel", new ShipmentReasonRequest(reason), s.Src, key);

    public static JsonElement ConsignmentTo(JsonElement shipment, Guid destinationStore) =>
        shipment.GetProperty("consignments").EnumerateArray().Single(c => c.GetProperty("destination").GetProperty("id").GetGuid() == destinationStore);

    public static JsonElement LineOfConsignment(JsonElement consignment, int index = 0) => consignment.GetProperty("lines")[index];

    public static async Task<Guid> NewVehicleAsync(BranchApiWorld w, Guid? assignedBranch = null)
    {
        var number = "SH" + Guid.NewGuid().ToString("N")[..8];
        var response = await SendAsync(w.Admin, HttpMethod.Post, "/api/v1/vehicles", new CreateVehicleRequest(number, "Shipment van " + number, "Van", assignedBranch));
        return IdOf(await OkAsync(response));
    }

    /// <summary>Adds an own-fleet vehicle (a fresh central one) to the shipment; returns (shipment detail, ShipmentVehicleId).</summary>
    public static async Task<(JsonElement Shipment, Guid ShipmentVehicleId)> AddOwnVehicleAsync(ShipScene s, Guid shipmentId)
    {
        var vehicle = await NewVehicleAsync(s.W);
        var shipment = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/shipments/{shipmentId}/vehicles",
            new AddShipmentVehicleRequest(vehicle, null, null, "Driver", "9000000000"), s.Src));
        var id = shipment.GetProperty("vehicles").EnumerateArray().Single(v => v.GetProperty("vehicleId").GetGuid() == vehicle).GetProperty("id").GetGuid();
        return (shipment, id);
    }

    // ---- DB reads / invariants -------------------------------------------------------------------------------------------------------

    public static Task<decimal> ReservedAsync(ShipScene s, Guid? store = null) =>
        ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT COALESCE(SUM("ReservedQuantity"), 0) FROM "StockItems" WHERE "StockLocationId" = '{store ?? s.SrcStore}' AND "ProductId" = '{s.W.ProductId}'""");

    public static Task<decimal> QohAsync(ShipScene s, Guid? store = null) =>
        ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT COALESCE(SUM("QuantityOnHand"), 0) FROM "StockItems" WHERE "StockLocationId" = '{store ?? s.SrcStore}' AND "ProductId" = '{s.W.ProductId}'""");

    public static Task<decimal> ActiveReservationsAsync(ShipScene s) =>
        ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT COALESCE(SUM("BaseQty"), 0) FROM "StockReservations" WHERE "SourceLocationId" = '{s.SrcStore}' AND "ProductId" = '{s.W.ProductId}' AND "Status" = 'Active'""");

    public static Task<long> AuditCountAsync(BranchApiWorld w, Guid targetId, string action) =>
        ScalarAsync<long>(w.Host.Database, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "TargetId" = '{targetId}' AND "Action" = '{action}'""");

    /// <summary>ReservedQuantity = sum of Active reservations, 0 &lt;= reserved &lt;= on hand for the scene's source (and the shared FI-D5 reconciliation script, queries 1-3, returns no rows).</summary>
    public static async Task AssertReservationInvariantsAsync(ShipScene s, string when)
    {
        var (reserved, active, qoh) = (await ReservedAsync(s), await ActiveReservationsAsync(s), await QohAsync(s));
        Assert.True(reserved == active, $"{when}: ReservedQuantity {reserved} != sum of Active reservations {active}");
        Assert.True(reserved >= 0 && reserved <= qoh, $"{when}: reserved {reserved} outside 0..QOH {qoh}");
        await AssertReconciliationScriptCleanAsync(s.W, when);
    }

    public static Task AssertReconciliationScriptCleanAsync(BranchApiWorld w, string when) => AssertReconciliationScriptCleanAsync(w.Host.Database, when);

    public static async Task AssertReconciliationScriptCleanAsync(TestDatabase database, string when)
    {
        var statements = ReconciliationStatements();
        for (var i = 0; i < 3; i++)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(statements[i], connection);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = 0;
            while (await reader.ReadAsync()) rows++;
            Assert.True(rows == 0, $"{when}: reconciliation query {i + 1} returned {rows} row(s)");
        }
    }

    private static List<string> ReconciliationStatements()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", "sql", "stock-reservations-reconciliation.sql"))) dir = dir.Parent;
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "scripts", "sql", "stock-reservations-reconciliation.sql"));
        var code = string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        var statements = code.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x.StartsWith("SELECT", StringComparison.Ordinal)).ToList();
        Assert.True(statements.Count >= 3, "reconciliation script must contain at least queries 1-3");
        return statements;
    }

    // ---- parallel hammer -------------------------------------------------------------------------------------------------------------

    public static async Task<T[]> ParallelAsync<T>(params Func<Task<T>>[] work)
    {
        var start = new TaskCompletionSource();
        var tasks = work.Select(w => Task.Run(async () => { await start.Task; return await w(); })).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(120));
    }

    /// <summary>No 5xx (a constraint violation, deadlock or unhandled exception would be one) and only the expected statuses.</summary>
    public static async Task AssertNo500Async(string what, params HttpResponseMessage[] responses)
    {
        foreach (var r in responses)
        {
            Assert.True((int)r.StatusCode < 500, $"{what}: unexpected {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        }
    }
}
