using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Api;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.ShipmentHttp;

namespace Sellora.IntegrationTests.TestData;

/// <summary>One dispatched shipment of a single consignment (to destination A) on a fresh <see cref="ShipScene"/>.</summary>
public sealed record DispatchedScene(ShipScene S, Guid ShipmentId, Guid ConsId, Guid LineId, string ConsNo, Guid RequestId);

/// <summary>Phase 06 (FI-D39..D41) HTTP + SQL helpers shared by the receipt-approval and disposition API tests.</summary>
/// <summary>
/// The Phase 06 tests run in worlds of their own (fresh database + host), never the shared one: they change the company receipt policy, and a rejected receipt leaves
/// database-wide rows that the global reconciliation scripts inspect. <see cref="Phase06Fixture"/> gives a test class a world of its own; <see cref="FreshAsync"/> gives a
/// reject test its own database.
/// </summary>
public sealed record Phase06World(BranchApiWorld W, RestrictedUserWorld R) : IAsyncDisposable
{
    /// <summary>Disposes the host of a <see cref="FreshAsync"/> world (the connection pool would otherwise stay open until the run ends).</summary>
    public async ValueTask DisposeAsync()
    {
        await W.Host.DisposeAsync();
        Npgsql.NpgsqlConnection.ClearAllPools();
    }

    public static async Task<Phase06World> FreshAsync(PostgresFixture postgres)
    {
        // FI-D43h: no manual pool clearing; PostgresFixture.ConnectionStringFor prunes idle connections of every test database within seconds.
        var w = await BranchApiWorld.CreateDedicatedAsync(postgres);
        return new Phase06World(w, await RestrictedUserWorld.CreateDedicatedAsync(w));
    }
}

/// <summary>Class fixture: one dedicated world for a test class, disposed when the class finishes (no host or connection pool outlives its tests).</summary>
public sealed class Phase06Fixture(PostgresFixture postgres) : IAsyncLifetime
{
    public Phase06World World { get; private set; } = null!;
    public async Task InitializeAsync() => World = await Phase06World.FreshAsync(postgres);
    public async Task DisposeAsync() => await World.DisposeAsync();
}

public static class ReceiptHttp
{
    private static readonly SemaphoreSlim RoleGate = new(1, 1);
    private static readonly Dictionary<string, Guid> RoleByName = [];

    public static string NewKey(string prefix = "k") => prefix + "-" + Guid.NewGuid().ToString("N");

    // ---- users ----------------------------------------------------------------------------------------------------------------------------

    private static async Task<Guid> RoleAsync(BranchApiWorld w, string name, string[] codes, RoleScope? scope = null, Guid? branch = null)
    {
        await RoleGate.WaitAsync();
        try
        {
            var key = w.Host.Database.ConnectionString + name + ":" + branch;
            if (RoleByName.TryGetValue(key, out var id)) return id;
            var response = await w.SendAsync(HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest("RCP " + name + Guid.NewGuid().ToString("N")[..6], null, codes, scope, branch));
            Check(response, "create role " + name);
            return RoleByName[key] = IdOf(await JsonAsync(response));
        }
        finally { RoleGate.Release(); }
    }

    /// <summary>Role-name part for a code set: the codes joined when short, else a stable hash (role names are limited to 80 characters).</summary>
    private static string RoleTag(string[] codes)
    {
        var joined = string.Join("", codes.Select(c => c.Replace(".", "")));
        return joined.Length <= 40 ? joined : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined)))[..16];
    }

    /// <summary>A user allocated to <paramref name="branch"/> only, holding <paramref name="codes"/> as a branch role at that branch.</summary>
    public static async Task<RestrictedUser> BranchUserAsync(RestrictedUserWorld r, Guid branch, params string[] codes)
    {
        var user = await r.CreateAsync("rc", await RoleAsync(r.World, "harmless", ["Sales.View"]), CompanyAccessScope.AssignedBranches, [branch], branch);
        var assigned = await r.World.SendAsync(HttpMethod.Post, $"/api/v1/users/{user.Id}/roles",
            new AssignRoleRequest(await RoleAsync(r.World, "br" + RoleTag(codes), codes, RoleScope.Branch, branch), null));
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
        return user;
    }

    /// <summary>A user holding <paramref name="codes"/> through a company-wide role, allocated to <paramref name="branches"/> only (company-level permission, branch-limited reach).</summary>
    public static async Task<RestrictedUser> CompanyRoleUserAsync(RestrictedUserWorld r, Guid[] branches, params string[] codes) =>
        await r.CreateAsync("rcc", await RoleAsync(r.World, "co" + RoleTag(codes), codes), CompanyAccessScope.AssignedBranches, branches, branches[0]);

    // ---- the company receipt policy (FI-D39) ----------------------------------------------------------------------------------------------

    public static async Task SetPolicyAsync(BranchApiWorld w, ReceiptPostingMode mode, bool separation)
    {
        var current = (await w.Admin.GetFromJsonAsync<BusinessSettingsDto>("/api/v1/settings", ApiHost.Json))!;
        var response = await w.SendAsync(HttpMethod.Put, "/api/v1/settings",
            new UpdateBusinessSettingsRequest(current.BusinessName, current.AddressLine, current.City, current.State, current.Gstin, current.Phone, current.Email, current.InvoicePrefix, current.BaseCurrencyId, mode, separation));
        Check(response, $"set receipt policy {mode}/{separation}");
        var after = (await w.Admin.GetFromJsonAsync<BusinessSettingsDto>("/api/v1/settings", ApiHost.Json))!;
        Assert.Equal((mode, separation), (after.ReceiptPostingMode, after.ReceiptSeparationOfDuties));
    }

    /// <summary>Sets the policy and restores Direct / no separation on dispose, so the shared world's other tests keep the Phase 05 behaviour.</summary>
    public sealed class PolicyScope(BranchApiWorld w) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(SetPolicyAsync(w, ReceiptPostingMode.Direct, false));
    }

    public static async Task<PolicyScope> UsePolicyAsync(BranchApiWorld w, ReceiptPostingMode mode, bool separation = false)
    {
        var scope = new PolicyScope(w);
        try { await SetPolicyAsync(w, mode, separation); }
        catch { await scope.DisposeAsync(); throw; }
        return scope;
    }

    // ---- scenes ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>A fresh scene stocked with <paramref name="stock"/> pieces, <paramref name="pieces"/> of them dispatched in one consignment to destination A.</summary>
    public static async Task<DispatchedScene> DispatchedAsync(Phase06World world, decimal stock, decimal pieces)
    {
        var (s, sid, request) = await ReadySceneAsync(null!, stock, pieces, world.W);
        var shipment = await OkAsync(await DispatchShipmentAsync(s, sid));
        var cons = ConsignmentTo(shipment, s.AStore);
        return new DispatchedScene(s, sid, IdOf(cons), LineOfConsignment(cons).GetProperty("id").GetGuid(), cons.GetProperty("consignmentNo").GetString()!, IdOf(request));
    }

    // ---- endpoints ------------------------------------------------------------------------------------------------------------------------

    public static Task<HttpResponseMessage> RecordAsync(HttpClient client, Guid branch, DispatchedScene d, decimal accepted, decimal damaged = 0m, decimal rejected = 0m, decimal missing = 0m, string? key = null) =>
        SendAsync(client, HttpMethod.Post, $"/api/v1/consignments/{d.ConsId}/receipts",
            new RecordReceiptRequest([new ReceiptLineRequest(d.LineId, accepted, damaged, rejected, missing)], "phase 06 test"), branch, key ?? NewKey("rec"));

    public static async Task<Guid> RecordOkAsync(HttpClient client, Guid branch, DispatchedScene d, decimal accepted, decimal damaged = 0m, decimal rejected = 0m, decimal missing = 0m)
    {
        var response = await RecordAsync(client, branch, d, accepted, damaged, rejected, missing);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return IdOf(await JsonAsync(response));
    }

    public static Task<HttpResponseMessage> PostReceiptAsync(HttpClient client, Guid branch, DispatchedScene d, Guid receiptId, string? key = null) =>
        SendAsync(client, HttpMethod.Post, $"/api/v1/consignments/{d.ConsId}/receipts/{receiptId}/post", null, branch, key ?? NewKey("post"));

    public static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Guid branch, DispatchedScene d, Guid receiptId, string? key = null) =>
        SendAsync(client, HttpMethod.Post, $"/api/v1/consignments/{d.ConsId}/receipts/{receiptId}/approve", null, branch, key ?? NewKey("appr"));

    public static Task<HttpResponseMessage> RejectAsync(HttpClient client, Guid branch, DispatchedScene d, Guid receiptId, string reason = "count was wrong", string? key = null) =>
        SendAsync(client, HttpMethod.Post, $"/api/v1/consignments/{d.ConsId}/receipts/{receiptId}/reject", new RejectReceiptRequest(reason), branch, key ?? NewKey("rej"));

    public static Task<HttpResponseMessage> DispositionAsync(HttpClient client, Guid branch, DispatchedScene d, DispositionBucket bucket, DispositionAction action, decimal quantity, string reason = "resolve", string? key = null) =>
        SendAsync(client, HttpMethod.Post, $"/api/v1/consignments/{d.ConsId}/dispositions",
            new RecordDispositionRequest(reason, [new DispositionLineRequest(d.LineId, bucket, action, quantity)]), branch, key ?? NewKey("disp"));

    /// <summary>No 5xx; a 500 is reported together with the API's captured error log.</summary>
    public static async Task AssertNo500Async(BranchApiWorld w, string what, params HttpResponseMessage[] responses)
    {
        foreach (var r in responses)
        {
            Assert.True((int)r.StatusCode < 500, $"{what}: unexpected {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()} | server errors: {string.Join(" || ", w.Host.Factory.ServerErrors)}");
        }
    }

    public static async Task AssertRefusedAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var fields = await ErrorFieldsAsync(response);
        Assert.True(fields.Contains(field.ToLowerInvariant()), $"expected error field '{field}', got [{string.Join(",", fields)}]");
    }

    // ---- DB reads -------------------------------------------------------------------------------------------------------------------------

    public static Task<string> ReceiptStatusAsync(BranchApiWorld w, Guid receiptId) =>
        ScalarAsync<string>(w.Host.Database, $"""SELECT "Status" FROM "ConsignmentReceipts" WHERE "Id" = '{receiptId}'""");

    public static Task<string> ConsignmentStatusAsync(BranchApiWorld w, Guid id) =>
        ScalarAsync<string>(w.Host.Database, $"""SELECT "Status" FROM "Consignments" WHERE "Id" = '{id}'""");

    public static Task<string> ShipmentStatusAsync(BranchApiWorld w, Guid id) =>
        ScalarAsync<string>(w.Host.Database, $"""SELECT "Status" FROM "Shipments" WHERE "Id" = '{id}'""");

    /// <summary>A ConsignmentLines counter column of the scene's line (AcceptedBaseQty, PostedBaseQty, DispositionedDamagedBaseQty, ...).</summary>
    public static Task<decimal> LineAsync(DispatchedScene d, string column) =>
        ScalarAsync<decimal>(d.S.W.Host.Database, $"""SELECT "{column}" FROM "ConsignmentLines" WHERE "Id" = '{d.LineId}'""");

    /// <summary>Sum of a StockTransferLineBatches counter column over the line's dispatched batch rows (ReceivedBaseQty, WrittenOffBaseQty, ReturnedBaseQty, AcceptedCorrectionBaseQty, DamagedBaseQty).</summary>
    public static Task<decimal> BatchRowsAsync(DispatchedScene d, string column) =>
        ScalarAsync<decimal>(d.S.W.Host.Database, $"""SELECT COALESCE(SUM("{column}"), 0) FROM "StockTransferLineBatches" WHERE "ConsignmentLineId" = '{d.LineId}'""");

    /// <summary>Signed movement quantity of the scene's product at a location with the given type, restricted to this consignment's receipts / dispositions ("CONS-... receipt n" / "... disposition n").</summary>
    public static Task<decimal> MovementsAsync(DispatchedScene d, string type, Guid location, string kind) =>
        ScalarAsync<decimal>(d.S.W.Host.Database,
            $"""SELECT COALESCE(SUM("QuantityBaseUnits"), 0) FROM "StockMovements" WHERE "ProductId" = '{d.S.W.ProductId}' AND "Type" = '{type}' AND "StockLocationId" = '{location}' AND "Notes" LIKE '{d.ConsNo} {kind} %'""");

    public static Task<long> MovementCountAsync(DispatchedScene d, string type, Guid location, string kind) =>
        ScalarAsync<long>(d.S.W.Host.Database,
            $"""SELECT COUNT(*) FROM "StockMovements" WHERE "ProductId" = '{d.S.W.ProductId}' AND "Type" = '{type}' AND "StockLocationId" = '{location}' AND "Notes" LIKE '{d.ConsNo} {kind} %'""");

    public static Task<long> DispositionCountAsync(DispatchedScene d) =>
        ScalarAsync<long>(d.S.W.Host.Database, $"""SELECT COUNT(*) FROM "ConsignmentDispositions" WHERE "ConsignmentId" = '{d.ConsId}'""");

    public static Task<long> AuditAsync(DispatchedScene d, string action) => AuditCountAsync(d.S.W, d.ConsId, action);

    /// <summary>Every posted / dispositioned unit is a stock movement pair: the ledger of the product sums to its on-hand over the whole company.</summary>
    public static async Task AssertLedgerMatchesOnHandAsync(ShipScene s, string when)
    {
        var rows = await ScalarAsync<long>(s.W.Host.Database, $"""
            SELECT COUNT(*) FROM (SELECT i."StockLocationId", i."QuantityOnHand" AS qoh, COALESCE((SELECT SUM(m."QuantityBaseUnits") FROM "StockMovements" m WHERE m."ProductId" = i."ProductId" AND m."StockLocationId" = i."StockLocationId"), 0) AS led
            FROM "StockItems" i WHERE i."ProductId" = '{s.W.ProductId}' AND i."StockLocationId" IN ('{s.SrcStore}', '{s.AStore}', '{s.BStore}')) x WHERE qoh <> led
            """);
        Assert.True(rows == 0, $"{when}: {rows} of the scene's locations have on-hand different from their movement ledger");
    }

    /// <summary>The scene's closing check: nothing negative, the reconciliation scripts (transit: all nine queries; stock-transfers-validation V1-V8) clean, ledger = on hand.</summary>
    public static async Task AssertCleanAsync(ShipScene s, string when)
    {
        Assert.True(TransitScriptQueryCount() >= 9, "transit-reconciliation.sql must contain queries 1-9 (FI-D34..D41)");
        await AssertNothingNegativeAsync(s, when);
        await AssertLedgerMatchesOnHandAsync(s, when);
        // The legacy transfer validation (V1-V8) must hold for shipment-flow transfers too: request-line counters (incl. AcceptedCorrection) = batch rows, outstanding identities.
        await TransferValidation.AssertCleanAsync(s.W.Host.Database, when);
    }

    private static int TransitScriptQueryCount()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", "sql", "transit-reconciliation.sql"))) dir = dir.Parent;
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "scripts", "sql", "transit-reconciliation.sql"));
        var code = string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        return code.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(x => x.StartsWith("SELECT", StringComparison.Ordinal) || x.StartsWith("WITH", StringComparison.Ordinal));
    }

    public static async Task<List<Guid>> TransitIdsAsync(HttpClient client, Guid branch, string view) =>
        (await OkAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/transit?view={view}&pageSize=100", null, branch)))
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("consignmentId").GetGuid()).ToList();
}
