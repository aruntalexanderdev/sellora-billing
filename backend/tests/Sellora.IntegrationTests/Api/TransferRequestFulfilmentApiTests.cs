using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.Api.BranchApiWorld;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Functional-improvements Phase 03 (FI-D26..FI-D28, FI-D11) over HTTP on PostgreSQL: approval enters the ready-for-shipment queue without touching stock,
/// closing a partly fulfilled request short, the DB checks that make over-allocation / over-fulfilment impossible, active-branch queue scoping, source-only
/// cost visibility, history visibility and close racing other writers.
/// PartiallyFulfilled is produced by shipments only in Phase 04, so the tests ARRANGE that state with direct SQL on the line counters and header status
/// (<see cref="ArrangeAsync"/>); everything else goes through the real pipeline.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TransferRequestFulfilmentApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>The arranged / hand-edited transfers have counters with no stock documents behind them, which the shared transfer validation script (run by other tests on the same
    /// database) rightly flags, so they are removed after each test. Genuinely dispatched transfers (real batches and movements) are left alone.</summary>
    public async Task DisposeAsync()
    {
        if (_world is null) return;
        await ExecuteAsync(_world.Host.Database, """
            CREATE TEMP TABLE "ArrangedTransfers" AS
            SELECT t."Id" FROM "StockTransfers" t WHERE t."Notes" = 'fulfilment'
              AND (t."Status" IN ('PartiallyFulfilled', 'Closed') OR EXISTS (SELECT 1 FROM "StockTransferLines" l WHERE l."TransferId" = t."Id" AND (l."AllocatedBaseQty" > 0 OR l."ClosedBaseQty" > 0)));
            DELETE FROM "StockTransferLines" WHERE "TransferId" IN (SELECT "Id" FROM "ArrangedTransfers");
            DELETE FROM "StockTransfers" WHERE "Id" IN (SELECT "Id" FROM "ArrangedTransfers");
            """);
    }

    private BranchApiWorld? _world;

    private static readonly SemaphoreSlim RoleGate = new(1, 1);
    private static readonly Dictionary<string, Guid> RoleByName = [];

    // ---- helpers -------------------------------------------------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, object? body = null, Guid? branch = null)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: ApiHost.Json);
        if (branch is { } b) request.Headers.Add("X-Branch-Id", b.ToString());
        return client.SendAsync(request);
    }

    private static Task<JsonElement> OkAsync(HttpResponseMessage response) => RestrictedUserWorld.JsonOkAsync(response);
    private static Guid IdOf(JsonElement e) => e.GetProperty("id").GetGuid();

    private sealed record Scene(BranchApiWorld W, Guid Box, Guid X, Guid Y, Guid XStore, Guid YStore);

    private async Task<Scene> SceneAsync()
    {
        var w = _world = await BranchApiWorld.GetAsync(postgres);
        var (x, _) = await w.NewBranchAsync(); var (y, _) = await w.NewBranchAsync();
        await w.EnsureBranchStockAsync(x, 500m); await w.EnsureBranchStockAsync(y, 500m);
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        var box = (await db.ProductUnits.AsNoTracking().Where(u => u.ProductId == w.ProductId).Include(u => u.Unit).ToListAsync()).Single(r => r.Unit.ShortCode == "box").Id;
        return new Scene(w, box, x, y, await Sellora.IntegrationTests.TestData.StockSeed.StoreOfAsync(db, x), await Sellora.IntegrationTests.TestData.StockSeed.StoreOfAsync(db, y));
    }

    /// <summary>Creates (submitted) and approves a transfer of <paramref name="boxes"/> Box (factor 10) from <paramref name="sourceBranch"/>'s store; returns the approved detail.</summary>
    private static async Task<JsonElement> ApprovedAsync(Scene s, Guid sourceBranch, Guid sourceStore, Guid destinationStore, decimal boxes = 10m, bool approve = true)
    {
        var create = await SendAsync(s.W.Admin, HttpMethod.Post, "/api/v1/stock-transfers",
            new CreateStockTransferRequest(sourceStore, destinationStore, "fulfilment", true, [new StockTransferLineRequest(s.W.ProductId, s.Box, boxes)]), sourceBranch);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await JsonAsync(create);
        if (!approve) return created;
        return await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(created)}/approve", null, sourceBranch));
    }

    private static Task<HttpResponseMessage> DispatchAsync(Scene s, JsonElement transfer, Guid branch, HttpClient? client = null) =>
        SendAsync(client ?? s.W.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(transfer)}/dispatch",
            new DispatchStockTransferRequest(transfer.GetProperty("lines").EnumerateArray()
                .Select(l => new DispatchStockTransferLineRequest(l.GetProperty("id").GetGuid(), s.Box, l.GetProperty("requestedQty").GetDecimal())).ToList()), branch);

    /// <summary>Seeds the legacy-dispatched state of the transfer (what a request dispatched before the Phase 05 cut-over looks like), with its dispatch audit event.</summary>
    private static async Task SeedDispatchedAsync(Scene s, JsonElement transfer)
    {
        await using var db = s.W.Host.Database.CreateDbContext(s.W.CompanyId);
        var lines = transfer.GetProperty("lines").EnumerateArray()
            .Select(l => new DispatchStockTransferLineInput(l.GetProperty("id").GetGuid(), s.Box, l.GetProperty("requestedQty").GetDecimal())).ToList();
        await LegacyTransferSeed.DispatchAsync(db, s.W.AdminId, IdOf(transfer), lines);
    }

    /// <summary>Arranges the Phase-04 state directly: every line has <paramref name="dispatchedBoxes"/> Box dispatched (+ <paramref name="allocatedBase"/> allocated) and the header is PartiallyFulfilled.</summary>
    private static async Task ArrangeAsync(BranchApiWorld w, Guid transferId, decimal dispatchedBoxes, decimal allocatedBase = 0m, string status = "PartiallyFulfilled")
    {
        await ExecuteAsync(w.Host.Database, $"""
            UPDATE "StockTransferLines" SET "DispatchProductUnitId" = "ProductUnitId", "DispatchUnitId" = "UnitId", "DispatchUnitCode" = "UnitCode",
                "DispatchFactor" = "ConversionFactorToBase", "DispatchedQty" = {dispatchedBoxes}, "DispatchedBaseQty" = {dispatchedBoxes} * "ConversionFactorToBase",
                "AllocatedBaseQty" = {allocatedBase}
            WHERE "TransferId" = '{transferId}';
            UPDATE "StockTransfers" SET "Status" = '{status}' WHERE "Id" = '{transferId}';
            """);
    }


    private static Task<HttpResponseMessage> CloseAsync(HttpClient client, Guid id, object? body, Guid? branch) =>
        SendAsync(client, HttpMethod.Post, $"/api/v1/stock-transfers/{id}/close", body, branch);

    private static async Task<(long Qoh, long Remaining, long Movements)> StockSnapshotAsync(BranchApiWorld w)
    {
        var db = w.Host.Database;
        return (await ScalarAsync<long>(db, """SELECT COALESCE(SUM("QuantityOnHand"), 0)::bigint FROM "StockItems" """),
                await ScalarAsync<long>(db, """SELECT COALESCE(SUM("RemainingBaseQty"), 0)::bigint FROM "PurchaseBatches" """),
                await ScalarAsync<long>(db, """SELECT COUNT(*) FROM "StockMovements" """));
    }

    private static async Task<Guid> RoleAsync(BranchApiWorld w, string name, string[] codes, RoleScope? scope = null, Guid? branch = null)
    {
        await RoleGate.WaitAsync();
        try
        {
            var key = name + ":" + branch;
            if (RoleByName.TryGetValue(key, out var id)) return id;
            var response = await w.SendAsync(HttpMethod.Post, "/api/v1/roles", new CreateRoleRequest("FUL " + name + Guid.NewGuid().ToString("N")[..6], null, codes, scope, branch));
            Check(response, "create role " + name);
            return RoleByName[key] = IdOf(await JsonAsync(response));
        }
        finally { RoleGate.Release(); }
    }

    /// <summary>A user at <paramref name="branch"/> whose <paramref name="codes"/> are held by a role scoped to that branch only.</summary>
    private static async Task<RestrictedUser> BranchUserAsync(RestrictedUserWorld r, Guid branch, params string[] codes)
    {
        var user = await r.CreateAsync("fu", await RoleAsync(r.World, "harmless", ["Sales.View"]), CompanyAccessScope.AssignedBranches, [branch], branch);
        var assigned = await r.World.SendAsync(HttpMethod.Post, $"/api/v1/users/{user.Id}/roles", new AssignRoleRequest(await RoleAsync(r.World, "br" + string.Join("", codes.Select(c => c[^3..])), codes, RoleScope.Branch, branch), null));
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
        return user;
    }

    private static async Task<List<Guid>> QueueAsync(HttpClient client, Guid? branch, string query = "")
    {
        var body = await OkAsync(await SendAsync(client, HttpMethod.Get, "/api/v1/stock-transfers/ready-for-shipment?pageSize=100" + query, null, branch));
        return body.GetProperty("items").EnumerateArray().Select(IdOf).ToList();
    }

    // ---- 1. approval -> ready queue, stock untouched --------------------------------------------------------------------------------------

    [Fact]
    public async Task Approve_OverHttp_EntersTheReadyQueue_WithoutChangingStockBatchesOrMovements()
    {
        var s = await SceneAsync();
        var requested = await ApprovedAsync(s, s.Y, s.YStore, s.XStore, approve: false);
        var id = IdOf(requested);
        Assert.DoesNotContain(id, await QueueAsync(s.W.Admin, s.Y)); // Requested is not ready for shipment yet
        var before = await StockSnapshotAsync(s.W);
        var qohBefore = await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT "QuantityOnHand" FROM "StockItems" WHERE "StockLocationId" = '{s.YStore}' AND "ProductId" = '{s.W.ProductId}'""");

        var approved = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{id}/approve", null, s.Y));

        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        Assert.Equal(before, await StockSnapshotAsync(s.W));
        Assert.Equal(qohBefore, await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT "QuantityOnHand" FROM "StockItems" WHERE "StockLocationId" = '{s.YStore}' AND "ProductId" = '{s.W.ProductId}'"""));
        var line = approved.GetProperty("lines")[0];
        Assert.Equal((0m, 0m, 100m), (line.GetProperty("allocatedBaseQty").GetDecimal(), line.GetProperty("closedBaseQty").GetDecimal(), line.GetProperty("remainingBaseQty").GetDecimal()));
        Assert.Contains(id, await QueueAsync(s.W.Admin, s.Y));
        Assert.Equal(0L, await ScalarAsync<long>(s.W.Host.Database, $"""SELECT COUNT(*) FROM "StockMovements" WHERE "ReferenceId" = '{id}' """));
    }

    // ---- 2. close ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Close_FromPartiallyFulfilled_ClosesTheRemainder_RecordsReasonInHistory_AndLeavesTheQueue()
    {
        var s = await SceneAsync();
        var t = await ApprovedAsync(s, s.Y, s.YStore, s.XStore, 10m);
        var id = IdOf(t);
        await ArrangeAsync(s.W, id, dispatchedBoxes: 4m); // 40 of 100 shipped
        Assert.Contains(id, await QueueAsync(s.W.Admin, s.Y));
        var before = await StockSnapshotAsync(s.W);

        var closed = await OkAsync(await CloseAsync(s.W.Admin, id, new CloseStockTransferRequest("  customer cancelled the rest  "), s.Y));

        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.Equal("customer cancelled the rest", closed.GetProperty("closureReason").GetString());
        Assert.NotNull(closed.GetProperty("closedByName").GetString());
        Assert.Equal(JsonValueKind.String, closed.GetProperty("closedAt").ValueKind);
        var line = closed.GetProperty("lines")[0];
        Assert.Equal((40m, 60m, 0m), (line.GetProperty("dispatchedBaseQty").GetDecimal(), line.GetProperty("closedBaseQty").GetDecimal(), line.GetProperty("remainingBaseQty").GetDecimal()));
        Assert.Equal(before, await StockSnapshotAsync(s.W)); // closing moves no stock
        Assert.DoesNotContain(id, await QueueAsync(s.W.Admin, s.Y));

        var history = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Get, $"/api/v1/stock-transfers/{id}/history", null, s.Y));
        var entry = history.EnumerateArray().Single(e => e.GetProperty("action").GetString() == "stock_transfer.closed");
        Assert.Equal(("PartiallyFulfilled", "Closed", "customer cancelled the rest"), (entry.GetProperty("fromStatus").GetString(), entry.GetProperty("toStatus").GetString(), entry.GetProperty("reason").GetString()));
        Assert.NotNull(entry.GetProperty("actorName").GetString());
    }

    [Fact]
    public async Task Close_WithNothingDispatched_Or_OutstandingAllocation_Is400OnStatus_AndChangesNothing()
    {
        var s = await SceneAsync();
        var untouched = await ApprovedAsync(s, s.Y, s.YStore, s.XStore, 5m);
        var refused = await CloseAsync(s.W.Admin, IdOf(untouched), new CloseStockTransferRequest("nothing shipped"), s.Y);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("status", await ErrorFieldsAsync(refused));
        Assert.Equal("Approved", await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Status" FROM "StockTransfers" WHERE "Id" = '{IdOf(untouched)}'"""));

        var allocated = await ApprovedAsync(s, s.Y, s.YStore, s.XStore, 10m);
        await ArrangeAsync(s.W, IdOf(allocated), dispatchedBoxes: 4m, allocatedBase: 100m); // 60 base still allocated, undispatched
        var blocked = await CloseAsync(s.W.Admin, IdOf(allocated), new CloseStockTransferRequest("too early"), s.Y);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("status", await ErrorFieldsAsync(blocked));
        Assert.Equal(("PartiallyFulfilled", 0m), (await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Status" FROM "StockTransfers" WHERE "Id" = '{IdOf(allocated)}'"""),
            await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT "ClosedBaseQty" FROM "StockTransferLines" WHERE "TransferId" = '{IdOf(allocated)}'""")));
    }

    [Fact]
    public async Task Close_ReasonRequiredAndAtMost500_Is400OnReason()
    {
        var s = await SceneAsync();
        var t = await ApprovedAsync(s, s.Y, s.YStore, s.XStore);
        await ArrangeAsync(s.W, IdOf(t), 3m);

        foreach (var reason in new[] { "", "   ", new string('r', 501) })
        {
            var response = await CloseAsync(s.W.Admin, IdOf(t), new { reason }, s.Y);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("reason", await ErrorFieldsAsync(response));
        }
        Assert.Contains("reason", await ErrorFieldsAsync(await CloseAsync(s.W.Admin, IdOf(t), new { }, s.Y)));
        Assert.Equal("PartiallyFulfilled", await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Status" FROM "StockTransfers" WHERE "Id" = '{IdOf(t)}'"""));
        Assert.Equal(HttpStatusCode.OK, (await CloseAsync(s.W.Admin, IdOf(t), new CloseStockTransferRequest(new string('r', 500)), s.Y)).StatusCode); // boundary accepted
    }

    [Fact]
    public async Task Close_WithoutApprovePermission_Is403_AndAnUnknownIdIs404()
    {
        var s = await SceneAsync();
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        var t = await ApprovedAsync(s, s.Y, s.YStore, s.XStore);
        await ArrangeAsync(s.W, IdOf(t), 3m);
        string[] noApprove = ["StockTransfers.View", "StockTransfers.Create", "StockTransfers.Dispatch", "StockTransfers.Receive", "StockTransfers.Cancel"];
        var user = await r.CreateAsync("na", await RoleAsync(s.W, "noapprove", noApprove), CompanyAccessScope.CompanyWide);

        Assert.Equal(HttpStatusCode.Forbidden, (await CloseAsync(user.Client, IdOf(t), new CloseStockTransferRequest("nope"), s.Y)).StatusCode);
        Assert.Equal("PartiallyFulfilled", await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Status" FROM "StockTransfers" WHERE "Id" = '{IdOf(t)}'"""));
        Assert.Equal(HttpStatusCode.NotFound, (await CloseAsync(s.W.Admin, Guid.NewGuid(), new CloseStockTransferRequest("x"), s.Y)).StatusCode);
    }

    [Fact]
    public async Task Close_Repeated_IsIdempotent_OneAuditEvent_NoDoubleClosing()
    {
        var s = await SceneAsync();
        var t = await ApprovedAsync(s, s.Y, s.YStore, s.XStore);
        await ArrangeAsync(s.W, IdOf(t), 2m);

        var first = await OkAsync(await CloseAsync(s.W.Admin, IdOf(t), new CloseStockTransferRequest("first"), s.Y));
        var again = await OkAsync(await CloseAsync(s.W.Admin, IdOf(t), new CloseStockTransferRequest("second"), s.Y));

        Assert.Equal("Closed", again.GetProperty("status").GetString());
        Assert.Equal("first", again.GetProperty("closureReason").GetString()); // the repeat changes nothing
        Assert.Equal(first.GetProperty("closedAt").GetString(), again.GetProperty("closedAt").GetString());
        Assert.Equal(80m, again.GetProperty("lines")[0].GetProperty("closedBaseQty").GetDecimal());
        Assert.Equal(1L, await ScalarAsync<long>(s.W.Host.Database, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "TargetId" = '{IdOf(t)}' AND "Action" = 'stock_transfer.closed'"""));
    }

    // ---- 3. DB checks ------------------------------------------------------------------------------------------------------------------------

    private static async Task<PostgresException> ViolationAsync(BranchApiWorld w, string sql)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(w.Host.Database, sql));
        Assert.Equal("23514", ex.SqlState);
        return ex;
    }

    [Fact]
    public async Task Database_RejectsOverAllocation_OverClosing_OverFulfilment_AndClosureWithoutActorOrReason()
    {
        var s = await SceneAsync();
        var t = await ApprovedAsync(s, s.Y, s.YStore, s.XStore, 10m); // requested 100 base
        var id = IdOf(t);
        string Lines(string set) => $"""UPDATE "StockTransferLines" SET {set} WHERE "TransferId" = '{id}'""";

        Assert.Equal("CK_StockTransferLines_Allocation_Valid", (await ViolationAsync(s.W, Lines(@"""AllocatedBaseQty"" = 100.001"))).ConstraintName);
        Assert.Equal("CK_StockTransferLines_Allocation_Valid", (await ViolationAsync(s.W, Lines(@"""AllocatedBaseQty"" = 60, ""ClosedBaseQty"" = 50"))).ConstraintName);
        Assert.Equal("CK_StockTransferLines_Allocation_Valid", (await ViolationAsync(s.W, Lines(@"""AllocatedBaseQty"" = -1"))).ConstraintName);
        Assert.Contains((await ViolationAsync(s.W, Lines(@"""ClosedBaseQty"" = 100.001"))).ConstraintName, new[] { "CK_StockTransferLines_Allocation_Valid", "CK_StockTransferLines_Dispatched_Max" });
        // 60 dispatched + 50 closed > 100 (allocation 0 + closed 50 alone would be fine, so only the dispatch check can fire).
        Assert.Equal("CK_StockTransferLines_Dispatched_Max", (await ViolationAsync(s.W, Lines(
            @"""DispatchProductUnitId"" = ""ProductUnitId"", ""DispatchUnitId"" = ""UnitId"", ""DispatchUnitCode"" = ""UnitCode"", ""DispatchFactor"" = ""ConversionFactorToBase"", ""DispatchedQty"" = 6, ""DispatchedBaseQty"" = 60, ""ClosedBaseQty"" = 50"))).ConstraintName);

        string Header(string set) => $"""UPDATE "StockTransfers" SET {set} WHERE "Id" = '{id}'""";
        var admin = s.W.AdminId;
        Assert.Equal("CK_StockTransfers_Status_Closure", (await ViolationAsync(s.W, Header($@"""Status"" = 'Closed', ""ClosedAt"" = now(), ""ClosedByUserId"" = '{admin}'"))).ConstraintName); // no reason
        Assert.Equal("CK_StockTransfers_Status_Closure", (await ViolationAsync(s.W, Header($@"""Status"" = 'Closed', ""ClosedAt"" = now(), ""ClosedByUserId"" = '{admin}', ""ClosureReason"" = ''"))).ConstraintName);
        Assert.Equal("CK_StockTransfers_Actors_Paired", (await ViolationAsync(s.W, Header(@"""Status"" = 'Closed', ""ClosedAt"" = now(), ""ClosureReason"" = 'r'"))).ConstraintName); // no actor
        Assert.Equal("CK_StockTransfers_Status_Closure", (await ViolationAsync(s.W, Header($@"""ClosedAt"" = now(), ""ClosedByUserId"" = '{admin}', ""ClosureReason"" = 'r'"))).ConstraintName); // stamps without the status
        Assert.Equal("CK_StockTransfers_Status_Closure", (await ViolationAsync(s.W, Header(@"""ClosureReason"" = 'orphan reason'"))).ConstraintName);

        // Nothing leaked from the refused statements, and a valid closed state is accepted.
        Assert.Equal((0m, 0m), (await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT "AllocatedBaseQty" FROM "StockTransferLines" WHERE "TransferId" = '{id}'"""),
            await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT "ClosedBaseQty" FROM "StockTransferLines" WHERE "TransferId" = '{id}'""")));
        await ExecuteAsync(s.W.Host.Database, Lines(@"""AllocatedBaseQty"" = 70, ""ClosedBaseQty"" = 30"));
    }

    // ---- 4. queue scoping --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadyQueue_IsScopedToTheActiveBranchsSourceLocations_AndCompanyScopeNeedsACompanyLevelGrant()
    {
        var s = await SceneAsync();
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        var outOfX = await ApprovedAsync(s, s.X, s.XStore, s.YStore);   // source x
        var intoX = await ApprovedAsync(s, s.Y, s.YStore, s.XStore);    // destination x only
        var requestedOnly = await ApprovedAsync(s, s.X, s.XStore, s.YStore, approve: false);
        var userX = await BranchUserAsync(r, s.X, "StockTransfers.View", "StockTransfers.Dispatch");
        var userY = await BranchUserAsync(r, s.Y, "StockTransfers.View", "StockTransfers.Dispatch");

        var atX = await QueueAsync(userX.Client, s.X);
        Assert.Contains(IdOf(outOfX), atX);
        Assert.DoesNotContain(IdOf(intoX), atX);          // destination-only: not the receiver's queue
        Assert.DoesNotContain(IdOf(requestedOnly), atX);  // not approved
        var atY = await QueueAsync(userY.Client, s.Y);
        Assert.Contains(IdOf(intoX), atY); Assert.DoesNotContain(IdOf(outOfX), atY);

        // Company scope: refused without a company-level Dispatch grant; the admin sees both sources; the company-level staff user is limited to its allowed branch.
        var denied = await SendAsync(userX.Client, HttpMethod.Get, "/api/v1/stock-transfers/ready-for-shipment?scope=company", null, s.X);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("company_scope_denied", await denied.ProblemCodeAsync());
        var company = await QueueAsync(s.W.Admin, null, "&scope=company");
        Assert.Contains(IdOf(outOfX), company); Assert.Contains(IdOf(intoX), company);
        var staff = await r.CreateAtAsync(s.X); // staff role holds StockTransfers.Dispatch company-level, allowed {x}
        var staffCompany = await QueueAsync(staff.Client, s.X, "&scope=company");
        Assert.Contains(IdOf(outOfX), staffCompany); Assert.DoesNotContain(IdOf(intoX), staffCompany);

        // A source location outside the read scope is a 403 (no existence leak); the active branch's own store is fine.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(userX.Client, HttpMethod.Get, $"/api/v1/stock-transfers/ready-for-shipment?sourceLocationId={s.YStore}", null, s.X)).StatusCode);
        Assert.Equal([IdOf(outOfX)], await QueueAsync(userX.Client, s.X, $"&sourceLocationId={s.XStore}"));
    }

    [Fact]
    public async Task ReadyQueue_SearchAndPaging_TranslateToSqlAndStayStable()
    {
        var s = await SceneAsync();
        var a = await ApprovedAsync(s, s.X, s.XStore, s.YStore);
        var b = await ApprovedAsync(s, s.X, s.XStore, s.YStore);
        var c = await ApprovedAsync(s, s.X, s.XStore, s.YStore);

        Assert.Equal(3, (await QueueAsync(s.W.Admin, s.X)).Count);
        var byNo = await QueueAsync(s.W.Admin, s.X, "&search=" + a.GetProperty("transferNo").GetString()!.ToLowerInvariant()); // case-insensitive
        Assert.Equal([IdOf(a)], byNo);
        Assert.Empty(await QueueAsync(s.W.Admin, s.X, "&search=zzz-no-such-transfer"));

        var page1 = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Get, "/api/v1/stock-transfers/ready-for-shipment?page=1&pageSize=2", null, s.X));
        var page2 = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Get, "/api/v1/stock-transfers/ready-for-shipment?page=2&pageSize=2", null, s.X));
        Assert.Equal((3, 2, 1), (page1.GetProperty("total").GetInt32(), page1.GetProperty("items").GetArrayLength(), page2.GetProperty("items").GetArrayLength()));
        Assert.Equal(new[] { IdOf(a), IdOf(b), IdOf(c) }.Order(), page1.GetProperty("items").EnumerateArray().Concat(page2.GetProperty("items").EnumerateArray()).Select(IdOf).Order()); // disjoint pages cover all three
        var row = page1.GetProperty("items")[0];
        Assert.Equal((1, 1), (row.GetProperty("lineCount").GetInt32(), row.GetProperty("remainingLineCount").GetInt32()));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s.W.Admin, HttpMethod.Get, "/api/v1/stock-transfers/ready-for-shipment?page=-3&pageSize=5000", null, s.X)).StatusCode); // clamped
    }

    // ---- 5. field visibility -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BatchUnitCost_IsNullForADestinationOnlyReader_AndPresentForTheSource_HistoryFollowsDetailVisibility()
    {
        var s = await SceneAsync();
        var r = await RestrictedUserWorld.GetWorldAsync(postgres);
        var (z, _) = await s.W.NewBranchAsync();
        await s.W.EnsureBranchStockAsync(z, 100m);
        var zStore = await ScalarAsync<Guid>(s.W.Host.Database, $"""SELECT "Id" FROM "StockLocations" WHERE "BranchId" = '{z}' AND "LocationType" = 'BranchStore' LIMIT 1""");
        var t = await ApprovedAsync(s, s.Y, s.YStore, s.XStore, 3m);
        await SeedDispatchedAsync(s, t); // FI-D3: the legacy-dispatched state (a shipment dispatch has its own tests)
        var unrelated = await ApprovedAsync(s, s.Y, s.YStore, zStore, 1m);

        var destination = await BranchUserAsync(r, s.X, "StockTransfers.View");
        var source = await BranchUserAsync(r, s.Y, "StockTransfers.View");

        var asDestination = await OkAsync(await SendAsync(destination.Client, HttpMethod.Get, $"/api/v1/stock-transfers/{IdOf(t)}", null, s.X));
        var batch = asDestination.GetProperty("lines")[0].GetProperty("batches").EnumerateArray().First();
        Assert.Equal(JsonValueKind.Null, batch.GetProperty("unitCost").ValueKind);
        Assert.True(batch.GetProperty("dispatchedBaseQty").GetDecimal() > 0m); // quantities stay visible
        var asSource = await OkAsync(await SendAsync(source.Client, HttpMethod.Get, $"/api/v1/stock-transfers/{IdOf(t)}", null, s.Y));
        Assert.Equal(5m, asSource.GetProperty("lines")[0].GetProperty("batches").EnumerateArray().First().GetProperty("unitCost").GetDecimal());
        var asAdmin = await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Get, $"/api/v1/stock-transfers/{IdOf(t)}", null, s.Y));
        Assert.Equal(5m, asAdmin.GetProperty("lines")[0].GetProperty("batches").EnumerateArray().First().GetProperty("unitCost").GetDecimal());

        // History: visible exactly where the detail is.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(destination.Client, HttpMethod.Get, $"/api/v1/stock-transfers/{IdOf(t)}/history", null, s.X)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(destination.Client, HttpMethod.Get, $"/api/v1/stock-transfers/{IdOf(unrelated)}", null, s.X)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(destination.Client, HttpMethod.Get, $"/api/v1/stock-transfers/{IdOf(unrelated)}/history", null, s.X)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(s.W.Admin, HttpMethod.Get, $"/api/v1/stock-transfers/{Guid.NewGuid()}/history", null, s.X)).StatusCode);
        var history = await OkAsync(await SendAsync(source.Client, HttpMethod.Get, $"/api/v1/stock-transfers/{IdOf(t)}/history", null, s.Y));
        Assert.Equal(["stock_transfer.created", "stock_transfer.submitted", "stock_transfer.approved", "stock_transfer.dispatched"],
            history.EnumerateArray().Select(e => e.GetProperty("action").GetString()).Where(a => a != "stock_transfer.updated"));
    }

    // ---- 6. concurrency ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentClosesAndADispatch_OnAPartiallyFulfilledRequest_CloseOnce_DispatchIsRefused()
    {
        var s = await SceneAsync();
        var t = await ApprovedAsync(s, s.Y, s.YStore, s.XStore, 10m);
        await ArrangeAsync(s.W, IdOf(t), 4m);
        var before = await StockSnapshotAsync(s.W);

        var results = await Task.WhenAll(
            CloseAsync(s.W.Admin, IdOf(t), new CloseStockTransferRequest("one"), s.Y),
            CloseAsync(s.W.Admin, IdOf(t), new CloseStockTransferRequest("two"), s.Y),
            DispatchAsync(s, t, s.Y));

        Assert.All(results, r => Assert.True((int)r.StatusCode < 500, $"unexpected {(int)r.StatusCode}"));
        Assert.Equal(HttpStatusCode.OK, results[0].StatusCode); Assert.Equal(HttpStatusCode.OK, results[1].StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, results[2].StatusCode); // dispatch needs Approved (PartiallyFulfilled shipments arrive in Phase 04)
        Assert.Equal(("Closed", 60m), (await ScalarAsync<string>(s.W.Host.Database, $"""SELECT "Status" FROM "StockTransfers" WHERE "Id" = '{IdOf(t)}'"""),
            await ScalarAsync<decimal>(s.W.Host.Database, $"""SELECT "ClosedBaseQty" FROM "StockTransferLines" WHERE "TransferId" = '{IdOf(t)}'""")));
        Assert.Equal(1L, await ScalarAsync<long>(s.W.Host.Database, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "TargetId" = '{IdOf(t)}' AND "Action" = 'stock_transfer.closed'"""));
        Assert.Equal(before, await StockSnapshotAsync(s.W));
    }

    // ---- company isolation ---------------------------------------------------------------------------------------------------------------

    private static readonly SemaphoreSlim IsolationGate = new(1, 1);
    private static IsolationWorld? _isolation;

    [Fact]
    public async Task OtherCompanysTransfer_IsA404ForCloseAndHistory_AndNeverInTheReadyQueue()
    {
        await IsolationGate.WaitAsync();
        IsolationWorld iso;
        try { iso = _isolation ??= await IsolationWorld.BuildAsync(postgres); }
        finally { IsolationGate.Release(); }
        using var a = iso.Host.NewClient(iso.A.Token);
        using var b = iso.Host.NewClient(iso.B.Token);
        Guid WarehouseLocation(Ids ids)
        {
            using var db = iso.Host.Database.CreateDbContext(ids.Company);
            return db.Warehouses.Where(x => x.Id == ids.WarehouseId).Select(x => x.StockLocationId).Single();
        }
        var created = await SendAsync(a, HttpMethod.Post, "/api/v1/stock-transfers",
            new CreateStockTransferRequest(iso.A.LocationId, WarehouseLocation(iso.A), "isolation", true, [new StockTransferLineRequest(iso.A.ProductId, iso.A.ProductUnitId, 1m)]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = IdOf(await JsonAsync(created));
        await OkAsync(await SendAsync(a, HttpMethod.Post, $"/api/v1/stock-transfers/{id}/approve"));

        Assert.Contains(id, await QueueAsync(a, null));
        Assert.DoesNotContain(id, await QueueAsync(b, null));
        Assert.DoesNotContain(id, await QueueAsync(b, null, "&scope=company"));
        Assert.Equal(HttpStatusCode.NotFound, (await CloseAsync(b, id, new CloseStockTransferRequest("x"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(b, HttpMethod.Get, $"/api/v1/stock-transfers/{id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CloseAsync(a, id, new CloseStockTransferRequest("x"), null)).StatusCode); // owner: refused on status (nothing dispatched), not hidden
        Assert.Equal("Approved", await ScalarAsync<string>(iso.Host.Database, $"""SELECT "Status" FROM "StockTransfers" WHERE "Id" = '{id}'"""));
    }
}
