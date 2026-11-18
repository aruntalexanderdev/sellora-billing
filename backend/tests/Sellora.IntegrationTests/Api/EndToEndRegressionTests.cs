using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.Api.BranchApiWorld;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;
using static Sellora.IntegrationTests.TestData.ReceiptHttp;
using static Sellora.IntegrationTests.TestData.ShipmentHttp;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// Functional-improvements Phase 09 (FI-D43c): cross-phase regression over HTTP on PostgreSQL, in a world of its own (the reconciliation scripts are database-wide).
/// A = request (two products in non-base units) -> approval -> ONE shipment from a branch warehouse with two vehicles and two destination consignments (the multi-stop route) -> short allocation ->
/// dispatch -> SupervisorApproval receipts with damaged and missing quantity -> approval by another user -> dispositions (write off / accept to destination / return to source) -> Reconciled / Completed,
/// with idempotent retries, audit rows for every write and every reconciliation script clean. B = cash / bank post, transfer, expense, reverse, ledger balances. C = delegated branch roles.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EndToEndRegressionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private BranchApiWorld _w = null!;
    private RestrictedUserWorld _r = null!;

    public async Task InitializeAsync()
    {
        _w = await BranchApiWorld.CreateDedicatedAsync(postgres);
        _r = await RestrictedUserWorld.CreateDedicatedAsync(_w);
    }

    public async Task DisposeAsync() => await _w.Host.DisposeAsync();

    private static Task<long> CountAsync(BranchApiWorld w, string sql) => ScalarAsync<long>(w.Host.Database, sql);
    private static Task<long> AuditsAsync(BranchApiWorld w, string action) => CountAsync(w, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = '{action}'""");

    private static async Task<JsonElement> CreateRequestAsync(ShipScene s, Guid destinationStore, params (Guid Product, Guid Unit, decimal Qty)[] lines)
    {
        var create = await SendAsync(s.W.Admin, HttpMethod.Post, "/api/v1/stock-transfers",
            new CreateStockTransferRequest(s.SrcStore, destinationStore, "e2e", true, lines.Select(l => new StockTransferLineRequest(l.Product, l.Unit, l.Qty)).ToList()), s.Src, NewKey("req"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await JsonAsync(create);
        return await OkAsync(await SendAsync(s.W.Admin, HttpMethod.Post, $"/api/v1/stock-transfers/{IdOf(created)}/approve", null, s.Src));
    }

    private static Guid LineFor(JsonElement transfer, Guid product) =>
        transfer.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("productId").GetGuid() == product).GetProperty("id").GetGuid();

    private static JsonElement ConsLine(JsonElement consignment, Guid product) =>
        consignment.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("productId").GetGuid() == product);

    private static ReceiptLineRequest Rl(JsonElement line, decimal accepted, decimal damaged = 0m, decimal missing = 0m) =>
        new(line.GetProperty("id").GetGuid(), accepted, damaged, 0m, missing);

    private static Task<HttpResponseMessage> RecordAsync(HttpClient c, Guid branch, Guid consignment, string key, params ReceiptLineRequest[] lines) =>
        SendAsync(c, HttpMethod.Post, $"/api/v1/consignments/{consignment}/receipts", new RecordReceiptRequest([.. lines], "e2e"), branch, key);

    private static async Task<decimal> QohAtAsync(BranchApiWorld w, Guid location, Guid product) =>
        await ScalarAsync<decimal>(w.Host.Database, $"""SELECT COALESCE(SUM("QuantityOnHand"), 0) FROM "StockItems" WHERE "StockLocationId" = '{location}' AND "ProductId" = '{product}'""");

    private static async Task AssertAllScriptsCleanAsync(ShipScene s, string when)
    {
        var db = s.W.Host.Database;
        await AssertCleanAsync(s, when); // transit 1-9, reservations 1-3, stock-transfers V1-V8, negatives, ledger = on hand of the scene's locations
        await ValidationScripts.AssertZeroRowsAsync(db, "transit-reconciliation.sql", 9, when);
        await ValidationScripts.AssertZeroRowsAsync(db, "stock-reservations-reconciliation.sql", 4, when);
        await ValidationScripts.AssertZeroRowsAsync(db, "stock-transfers-validation.sql", 8, when);
        await ValidationScripts.AssertZeroRowsAsync(db, "stock-reconciliation.sql", 8, when);
        await ValidationScripts.AssertZeroRowsAsync(db, "stock-items-negative-check.sql", 1, when);
        await ValidationScripts.AssertZeroRowsAsync(db, "cash-bank-funding-validation.sql", 5, when);
    }

    // ---- Scenario A -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ScenarioA_RequestToReconciled_TwoVehiclesTwoStops_ShortLoad_SupervisorApproval_Dispositions_IdempotentRetries_AuditedAndReconciled()
    {
        var w = _w;
        var baseScene = await NewSceneAsync(postgres, 0m, w);
        var pen = w.ProductId;
        var rice = await ScalarAsync<Guid>(w.Host.Database, """SELECT "Id" FROM "Products" WHERE "Sku" = 'RICE-BP10'""");
        Guid bag;
        await using (var db = w.Host.Database.CreateDbContext(w.CompanyId))
        {
            bag = (await db.ProductUnits.AsNoTracking().Where(u => u.ProductId == rice).Include(u => u.Unit).ToListAsync()).Single(u => u.Unit.ShortCode == "bag").Id;
        }

        // Source = a warehouse of the source branch (not its store), stocked with 100 pc of Pen and 100 kg of Rice.
        var warehouse = await OkAsync(await w.SendAsync(HttpMethod.Post, "/api/v1/warehouses", new CreateWarehouseRequest("EW" + Guid.NewGuid().ToString("N")[..6], "E2E warehouse", baseScene.Src)));
        var wh = warehouse.GetProperty("stockLocationId").GetGuid();
        await using (var db = w.Host.Database.CreateDbContext(w.CompanyId))
        {
            await StockSeed.AddAsync(db, wh, pen, 100m, 5m, w.AdminId);
            await StockSeed.AddAsync(db, wh, rice, 100m, 40m, w.AdminId);
        }
        var s = baseScene with { SrcStore = wh };

        // Policy: SupervisorApproval with separation of duties.
        await SetPolicyAsync(w, ReceiptPostingMode.SupervisorApproval, true);

        // 1. Two approved requests in NON-BASE units: A wants 4 Box of Pen + 2 Bag of Rice, B wants 2 Box + 1 Bag.
        var reqA = await CreateRequestAsync(s, s.AStore, (pen, s.Box, 4m), (rice, bag, 2m));
        var reqB = await CreateRequestAsync(s, s.BStore, (pen, s.Box, 2m), (rice, bag, 1m));
        Assert.Equal("Approved", reqA.GetProperty("status").GetString());
        Assert.Equal(2L, await AuditsAsync(w, "stock_transfer.created"));
        Assert.Equal(2L, await AuditsAsync(w, "stock_transfer.approved"));

        // 2. One shipment, two own-fleet vehicles, two destination consignments (the multi-stop route), one vehicle per leg.
        var shipment = await NewShipmentAsync(s);
        var sid = IdOf(shipment);
        var (_, sv1) = await AddOwnVehicleAsync(s, sid);
        var (_, sv2) = await AddOwnVehicleAsync(s, sid);
        JsonElement current = shipment;
        foreach (var (request, product, unit, qty) in new[] { (reqA, pen, s.Box, 4m), (reqA, rice, bag, 2m), (reqB, pen, s.Box, 2m), (reqB, rice, bag, 1m) })
        {
            var allocated = await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/allocations", new AllocateShipmentRequest(LineFor(request, product), unit, qty), s.Src, NewKey("alloc"));
            current = await OkAsync(allocated);
        }
        var consA = ConsignmentTo(current, s.AStore);
        var consB = ConsignmentTo(current, s.BStore);
        Assert.Equal(2, current.GetProperty("consignments").GetArrayLength());
        Assert.Equal(2, current.GetProperty("vehicles").GetArrayLength());
        await OkAsync(await SendAsync(w.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}/consignments/{IdOf(consA)}/vehicle", new AssignConsignmentVehicleRequest(sv1), s.Src));
        await OkAsync(await SendAsync(w.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}/consignments/{IdOf(consB)}/vehicle", new AssignConsignmentVehicleRequest(sv2), s.Src));
        Assert.Equal((40m + 20m, 20m + 10m), (await ReservedOfAsync(w, wh, pen), await ReservedOfAsync(w, wh, rice)));      // 4+2 box = 60 pc, 2+1 bag = 30 kg

        // Partial fulfilment (a consignment is Loaded only when every line is loaded in full, so a shortfall is an allocation correction): A's Pen allocation drops from 4 to 3 boxes,
        // the fourth box is released back to the request (remaining unallocated 1 box) and its reservation goes with it.
        var shortened = await OkAsync(await SendAsync(w.Admin, HttpMethod.Put, $"/api/v1/shipments/{sid}/allocations/{ConsLine(consA, pen).GetProperty("id").GetGuid()}", new ChangeAllocationRequest(3m), s.Src, NewKey("chg")));
        consA = ConsignmentTo(shortened, s.AStore);
        Assert.Equal(1m, ConsLine(consA, pen).GetProperty("request").GetProperty("remainingUnallocatedBaseQty").GetDecimal() / 10m);
        Assert.Equal((30m + 20m, 20m + 10m), (await ReservedOfAsync(w, wh, pen), await ReservedOfAsync(w, wh, rice)));
        Assert.Equal(1L, await AuditsAsync(w, "shipment.allocation_changed"));

        // The Phase 09 list filter: this shipment is found by its transfer, a shipment of another transfer is excluded.
        var other = await NewSceneAsync(postgres, 5m, w);
        var otherRequest = await RequestAsync(other, other.AStore, 1m);
        var otherShipment = await NewShipmentAsync(other);
        await OkAsync(await AllocateAsync(other, IdOf(otherShipment), LineOf(otherRequest), 1m));
        var byTransfer = await OkAsync(await SendAsync(w.Admin, HttpMethod.Get, $"/api/v1/shipments?stockTransferId={IdOf(reqA)}&pageSize=100", null, s.Src));
        var ids = byTransfer.GetProperty("items").EnumerateArray().Select(IdOf).ToList();
        Assert.Contains(sid, ids);
        Assert.DoesNotContain(IdOf(otherShipment), ids);
        var byOther = await OkAsync(await SendAsync(w.Admin, HttpMethod.Get, $"/api/v1/shipments?stockTransferId={IdOf(otherRequest)}&pageSize=100", null, other.Src));
        Assert.DoesNotContain(sid, byOther.GetProperty("items").EnumerateArray().Select(IdOf));
        Assert.Contains(IdOf(otherShipment), byOther.GetProperty("items").EnumerateArray().Select(IdOf));

        // 3. Load both consignments in full (what is allocated: A's Pen is 3 boxes, not the 4 requested).
        var penA = ConsLine(consA, pen);
        var riceA = ConsLine(consA, rice);
        await OkAsync(await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{IdOf(consA)}/load",
            new LoadConsignmentRequest([.. consA.GetProperty("lines").EnumerateArray().Select(l => new LoadConsignmentLineRequest(l.GetProperty("id").GetGuid(), l.GetProperty("enteredQty").GetDecimal()))]), s.Src));
        var loadedB = await OkAsync(await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{IdOf(consB)}/load",
            new LoadConsignmentRequest([.. consB.GetProperty("lines").EnumerateArray().Select(l => new LoadConsignmentLineRequest(l.GetProperty("id").GetGuid(), l.GetProperty("enteredQty").GetDecimal()))]), s.Src));
        Assert.Equal("ReadyToDispatch", loadedB.GetProperty("status").GetString());

        // 4. Dispatch; the same key twice returns the same result and moves nothing more. The shipment dispatches 3+2 boxes; the 4th requested box stays open on the request.
        var key = NewKey("dispatch");
        var first = await OkAsync(await DispatchShipmentAsync(s, sid, key));
        var transitAfter = (await TransitOfAsync(w, pen), await TransitOfAsync(w, rice));
        var replay = await OkAsync(await DispatchShipmentAsync(s, sid, key));
        Assert.Equal((IdOf(first), first.GetProperty("status").GetString()), (IdOf(replay), replay.GetProperty("status").GetString()));
        Assert.Equal((30m + 20m, 20m + 10m), transitAfter);                                                         // Pen 3+2 box, Rice 2+1 bag
        Assert.Equal(transitAfter, (await TransitOfAsync(w, pen), await TransitOfAsync(w, rice)));
        Assert.Equal((50m, 70m, 0m), (await QohAtAsync(w, wh, pen), await QohAtAsync(w, wh, rice), await ReservedOfAsync(w, wh, pen)));
        Assert.Equal(1L, await AuditsAsync(w, "shipment.dispatched"));
        var dispatched = await OkAsync(await GetShipmentAsync(s, sid));
        consA = ConsignmentTo(dispatched, s.AStore);
        consB = ConsignmentTo(dispatched, s.BStore);
        penA = ConsLine(consA, pen); riceA = ConsLine(consA, rice);
        Assert.Equal(30m, penA.GetProperty("dispatchedBaseQty").GetDecimal());
        await AssertAllScriptsCleanAsync(s, "after dispatch");

        // 5. Receipts at the two destinations (different users record and approve, per branch). A: Pen 3 box = 1 accepted + 1 damaged + 1 missing; Rice 2 bag = 1 accepted + 1 damaged.
        var recorderA = await BranchUserAsync(_r, s.A, "Shipments.View", "StockTransfers.Receive");
        var approverA = await BranchUserAsync(_r, s.A, "Shipments.View", "StockTransfers.ApproveReceipt");
        var recorderB = await BranchUserAsync(_r, s.B, "Shipments.View", "StockTransfers.Receive");
        var approverB = await BranchUserAsync(_r, s.B, "Shipments.View", "StockTransfers.ApproveReceipt");

        var recKeyA = NewKey("recA");
        var recA = await RecordAsync(recorderA.Client, s.A, IdOf(consA), recKeyA, Rl(penA, 1m, 1m, 1m), Rl(riceA, 1m, 1m));
        Assert.Equal(HttpStatusCode.Created, recA.StatusCode);
        var ridA = IdOf(await JsonAsync(recA));
        var recAReplay = await RecordAsync(recorderA.Client, s.A, IdOf(consA), recKeyA, Rl(penA, 1m, 1m, 1m), Rl(riceA, 1m, 1m));
        Assert.True(recAReplay.IsSuccessStatusCode, await recAReplay.Content.ReadAsStringAsync());
        Assert.Equal(ridA, IdOf(await JsonAsync(recAReplay)));                                                       // idempotent record: same receipt
        Assert.Equal(1L, await CountAsync(w, $"""SELECT COUNT(*) FROM "ConsignmentReceipts" WHERE "ConsignmentId" = '{IdOf(consA)}'"""));
        Assert.Equal("PendingApproval", await ReceiptStatusAsync(w, ridA));
        Assert.Equal((0m, 0m), (await QohAtAsync(w, s.AStore, pen), await QohAtAsync(w, s.AStore, rice)));          // recording moves nothing
        await AssertRefusedAsync(await SendAsync(recorderA.Client, HttpMethod.Post, $"/api/v1/consignments/{IdOf(consA)}/receipts/{ridA}/post", null, s.A, NewKey("post")), "status"); // supervisor mode: post refused

        var recB = await RecordAsync(recorderB.Client, s.B, IdOf(consB), NewKey("recB"), [.. consB.GetProperty("lines").EnumerateArray().Select(l => Rl(l, l.GetProperty("enteredQty").GetDecimal()))]);
        Assert.Equal(HttpStatusCode.Created, recB.StatusCode);
        var ridB = IdOf(await JsonAsync(recB));

        // Another branch's approver cannot approve, the recorder cannot approve its own receipt (no permission and separation), the right approver can; a retry of the same key is the same result.
        Assert.Equal(HttpStatusCode.NotFound, (await ApproveForAsync(approverB.Client, s.B, IdOf(consA), ridA)).StatusCode);
        Assert.False((await ApproveForAsync(recorderA.Client, s.A, IdOf(consA), ridA)).IsSuccessStatusCode);
        var approveKey = NewKey("approveA");
        var approvedA = await ApproveForAsync(approverA.Client, s.A, IdOf(consA), ridA, approveKey);
        Assert.Equal("Posted", (await OkAsync(approvedA)).GetProperty("status").GetString());
        var approveReplay = await ApproveForAsync(approverA.Client, s.A, IdOf(consA), ridA, approveKey);
        Assert.Equal("Posted", (await OkAsync(approveReplay)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await ApproveForAsync(approverB.Client, s.B, IdOf(consB), ridB, NewKey("approveB"))).StatusCode);
        Assert.Equal((10m, 10m), (await QohAtAsync(w, s.AStore, pen), await QohAtAsync(w, s.AStore, rice)));         // 1 box, 1 bag
        Assert.Equal((20m, 10m), (await QohAtAsync(w, s.BStore, pen), await QohAtAsync(w, s.BStore, rice)));         // 2 box, 1 bag
        Assert.Equal(2L, await AuditsAsync(w, "consignment.receipt_approved"));
        Assert.Equal("Reconciled", await ConsignmentStatusAsync(w, IdOf(consB)));                                    // nothing wrong at B
        Assert.Equal("Received", await ConsignmentStatusAsync(w, IdOf(consA)));                                      // A: posted but open discrepancies
        Assert.Equal("Delivered", await ShipmentStatusAsync(w, sid));
        await AssertAllScriptsCleanAsync(s, "after receipts");

        // 6. Dispositions in one request: Pen damaged 1 box -> write off; Pen missing 1 box -> accept to destination; Rice damaged 1 bag -> return to source. Same key twice = one disposition.
        var dKey = NewKey("disp");
        var body = new RecordDispositionRequest("e2e resolve",
        [
            new DispositionLineRequest(penA.GetProperty("id").GetGuid(), DispositionBucket.Damaged, DispositionAction.WriteOff, 1m),
            new DispositionLineRequest(penA.GetProperty("id").GetGuid(), DispositionBucket.Missing, DispositionAction.AcceptToDestination, 1m),
            new DispositionLineRequest(riceA.GetProperty("id").GetGuid(), DispositionBucket.Damaged, DispositionAction.ReturnToSource, 1m),
        ]);
        var disposed = await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/consignments/{IdOf(consA)}/dispositions", body, s.Src, dKey);
        Assert.Equal(HttpStatusCode.Created, disposed.StatusCode);
        var dispositionId = IdOf(await JsonAsync(disposed));
        var disposedAgain = await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/consignments/{IdOf(consA)}/dispositions", body, s.Src, dKey);
        Assert.True(disposedAgain.IsSuccessStatusCode, await disposedAgain.Content.ReadAsStringAsync());
        Assert.Equal(dispositionId, IdOf(await JsonAsync(disposedAgain)));
        Assert.Equal(1L, await CountAsync(w, $"""SELECT COUNT(*) FROM "ConsignmentDispositions" WHERE "ConsignmentId" = '{IdOf(consA)}'"""));
        Assert.Equal(1L, await AuditsAsync(w, "consignment.dispositioned"));

        // 7. Final state.
        Assert.Equal(("Reconciled", "Reconciled", "Completed"), (await ConsignmentStatusAsync(w, IdOf(consA)), await ConsignmentStatusAsync(w, IdOf(consB)), await ShipmentStatusAsync(w, sid)));
        Assert.Equal((50m, 80m), (await QohAtAsync(w, wh, pen), await QohAtAsync(w, wh, rice)));                     // Rice: 10 kg returned
        Assert.Equal((20m, 10m), (await QohAtAsync(w, s.AStore, pen), await QohAtAsync(w, s.AStore, rice)));         // Pen: 10 posted + 10 accepted to destination
        Assert.Equal((20m, 10m), (await QohAtAsync(w, s.BStore, pen), await QohAtAsync(w, s.BStore, rice)));
        Assert.Equal((0m, 0m), (await TransitOfAsync(w, pen), await TransitOfAsync(w, rice)));
        // Pen conservation: 100 = 50 + 20 + 20 + 10 written off (the written-off box is the only loss).
        Assert.Equal(-10m, await ScalarAsync<decimal>(w.Host.Database, $"""SELECT COALESCE(SUM("QuantityBaseUnits"), 0) FROM "StockMovements" WHERE "ProductId" = '{pen}' AND "Type" = 'Adjustment' AND "ReasonCode" = 'Loss'"""));

        // Ledger = on hand for EVERY location and both products (also the Transit location), not just the scene's.
        Assert.Equal(0L, await CountAsync(w, """
            SELECT COUNT(*) FROM "StockItems" i WHERE i."QuantityOnHand" <> COALESCE((SELECT SUM(m."QuantityBaseUnits") FROM "StockMovements" m WHERE m."ProductId" = i."ProductId" AND m."StockLocationId" = i."StockLocationId"), 0)
            """));

        // Every write of the scenario left an audit row; the idempotent retries added none (counts asserted above are exact: 1 dispatch, 2 approvals, 1 disposition).
        foreach (var (action, atLeast) in new[] { ("shipment.created", 1L), ("shipment.vehicle_added", 2L), ("shipment.allocated", 4L), ("shipment.vehicle_assigned", 2L), ("shipment.allocation_changed", 1L), ("shipment.loaded", 2L),
                     ("shipment.dispatched", 1L), ("consignment.receipt_verified", 2L), ("consignment.receipt_approved", 2L), ("consignment.dispositioned", 1L), ("stock_transfer.created", 2L), ("stock_transfer.approved", 2L) })
        {
            Assert.True(await AuditsAsync(w, action) >= atLeast, $"audit rows for {action} fewer than {atLeast}");
        }
        Assert.Equal(2L, await AuditsAsync(w, "consignment.receipt_verified")); // one per recorded receipt; the replayed record added none
        Assert.Equal(0L, await CountAsync(w, """SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" IN ('shipment.dispatched','consignment.receipt_approved','consignment.dispositioned') AND "ActorUserId" IS NULL"""));
        await AssertAllScriptsCleanAsync(s, "final");
        await ValidationScripts.AssertZeroRowsAsync(w.Host.Database, "cash-bank-negative-balances.sql", 1, "final");
    }

    // ---- Scenario B: cash / bank -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ScenarioB_CashBank_FundTransferExpenseThenReverse_LedgerBalances_ReversalAudited_SecondReversalRefused_ValidationScriptsClean()
    {
        var w = _w;
        const string accounts = "/api/v1/financial-accounts";
        async Task<Guid> NewAsync(string name, FinancialAccountKind kind)
        {
            var response = await w.SendAsync(HttpMethod.Post, accounts, new CreateFinancialAccountRequest(name + Guid.NewGuid().ToString("N")[..8], kind, null, 0m, null, null, 0m));
            Check(response, "create account");
            return IdOf(await JsonAsync(response));
        }
        async Task<decimal> BalanceAsync(Guid id) => (await OkAsync(await SendAsync(w.Admin, HttpMethod.Get, $"{accounts}/{id}"))).GetProperty("balance").GetDecimal();
        async Task<decimal> LedgerAsync(Guid id) => await ScalarAsync<decimal>(w.Host.Database,
            $"""SELECT COALESCE(SUM(CASE WHEN "Direction" = 'In' THEN "BaseAmount" ELSE -"BaseAmount" END), 0) FROM "FinancialTransactions" WHERE "AccountId" = '{id}' AND "Status" = 'Active'""");

        var cash = await NewAsync("E2E cash ", FinancialAccountKind.Cash);
        var bank = await NewAsync("E2E bank ", FinancialAccountKind.Bank);

        // Funding (idempotent), transfer, expense.
        var fundKey = NewKey("fund");
        var funding = await SendAsync(w.Admin, HttpMethod.Post, $"{accounts}/{cash}/fundings", new FundingRequest(FundingSource.OwnerCapital, 1000m, Today, null, null, null, null, null), null, fundKey);
        Assert.Equal(HttpStatusCode.Created, funding.StatusCode);
        var fundingId = (await JsonAsync(funding)).GetProperty("transactionId").GetGuid();
        var fundReplay = await SendAsync(w.Admin, HttpMethod.Post, $"{accounts}/{cash}/fundings", new FundingRequest(FundingSource.OwnerCapital, 1000m, Today, null, null, null, null, null), null, fundKey);
        Assert.Equal(fundingId, (await JsonAsync(fundReplay)).GetProperty("transactionId").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(w.Admin, HttpMethod.Post, $"{accounts}/transfers", new TransferRequest(cash, bank, 400m, Today, "e2e"), null, NewKey("xfer"))).StatusCode);
        Check(await w.SendAsync(HttpMethod.Post, "/api/v1/expenses", new CreateExpenseRequest(w.CategoryId, Today, w.CurrencyId, 150m, PaymentMethod.Cash, null, null, cash)), "expense");
        Assert.Equal((450m, 400m), (await BalanceAsync(cash), await BalanceAsync(bank)));
        Assert.Equal((450m, 400m), (await LedgerAsync(cash), await LedgerAsync(bank)));
        await ValidationScripts.AssertZeroRowsAsync(w.Host.Database, "cash-bank-funding-validation.sql", 5, "B after posting");

        // Reverse the transfer through one leg: both legs reverse, both balances restore, the reversal is audited with its reason and actor.
        var leg = await ScalarAsync<Guid>(w.Host.Database, $"""SELECT "Id" FROM "FinancialTransactions" WHERE "AccountId" = '{bank}' AND "Type" = 'Transfer'""");
        var reverseKey = NewKey("rev");
        var reversed = await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/financial-transactions/{leg}/reverse", new ReverseTransactionRequest("e2e wrong account"), null, reverseKey);
        Assert.Equal(HttpStatusCode.Created, reversed.StatusCode);
        var reversalIds = (await JsonAsync(reversed)).GetProperty("reversalTransactionIds").EnumerateArray().Select(e => e.GetGuid()).Order().ToList();
        Assert.Equal(2, reversalIds.Count);
        var replay = await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/financial-transactions/{leg}/reverse", new ReverseTransactionRequest("e2e wrong account"), null, reverseKey);
        Assert.Equal(reversalIds, (await JsonAsync(replay)).GetProperty("reversalTransactionIds").EnumerateArray().Select(e => e.GetGuid()).Order().ToList());
        Assert.Equal((850m, 0m), (await BalanceAsync(cash), await BalanceAsync(bank)));
        Assert.Equal((850m, 0m), (await LedgerAsync(cash), await LedgerAsync(bank)));
        Assert.Equal(1L, await AuditsAsync(w, "cash.reversed"));
        Assert.Equal(1L, await CountAsync(w, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'cash.reversed' AND "TargetId" = '{leg}' AND "ActorUserId" = '{w.AdminId}' AND "Reason" = 'e2e wrong account'"""));
        Assert.Equal((1L, 1L), (await AuditsAsync(w, "cash.funded"), await AuditsAsync(w, "cash.transferred")));

        // A second reversal (by either leg, with a new key) is refused and writes nothing.
        var legCash = await ScalarAsync<Guid>(w.Host.Database, $"""SELECT "Id" FROM "FinancialTransactions" WHERE "AccountId" = '{cash}' AND "Type" = 'Transfer'""");
        var rowsBefore = await CountAsync(w, """SELECT COUNT(*) FROM "FinancialTransactions" """);
        foreach (var id in new[] { leg, legCash })
        {
            await AssertRefusedAsync(await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/financial-transactions/{id}/reverse", new ReverseTransactionRequest("again"), null, NewKey("rev2")), "id");
        }
        Assert.Equal((rowsBefore, 1L), (await CountAsync(w, """SELECT COUNT(*) FROM "FinancialTransactions" """), await AuditsAsync(w, "cash.reversed")));
        // The funding can no longer be reversed in full once its cash was spent (cash cannot go below zero): refused, nothing posted.
        await AssertRefusedAsync(await SendAsync(w.Admin, HttpMethod.Post, $"/api/v1/financial-transactions/{fundingId}/reverse", new ReverseTransactionRequest("overdraw"), null, NewKey("rev3")), "amount");
        Assert.Equal((850m, rowsBefore), (await BalanceAsync(cash), await CountAsync(w, """SELECT COUNT(*) FROM "FinancialTransactions" """)));

        await ValidationScripts.AssertZeroRowsAsync(w.Host.Database, "cash-bank-funding-validation.sql", 5, "B final");
        await ValidationScripts.AssertZeroRowsAsync(w.Host.Database, "cash-bank-negative-balances.sql", 1, "B final");
        await ValidationScripts.AssertZeroRowsAsync(w.Host.Database, "stock-reconciliation.sql", 8, "B final");
    }

    // ---- Scenario C: delegated branch roles across the whole shipping chain ------------------------------------------------------------------
    // Gaps found in the existing coverage (IsolationCases + ShipmentsApiTests isolation test: other company 404 on every shipment / consignment / receipt / disposition endpoint, CashBank reverse of another
    // company 404; ReceiptApprovalApiTests: receive / approve / resolve matrix; ScopedRoles*: grant rules): (1) a delegated role at the SOURCE branch that plans, loads and dispatches was never run end to end
    // and refused at the other branches; (2) a destination delegate trying source-side writes; (3) a delegate of an uninvolved branch using the shipment's ids. Filled here; audit coverage of the chain is in scenario A.

    [Fact]
    public async Task ScenarioC_DelegatedBranchRoles_ActOnlyAtTheirOwnBranch_AcrossPlanLoadDispatchReceive_AndWritesAreAudited()
    {
        var w = _w;
        var s = await NewSceneAsync(postgres, 20m, w);
        var request = await RequestAsync(s, s.AStore, 5m);

        var source = await BranchUserAsync(_r, s.Src, "Shipments.View", "Shipments.Plan", "Shipments.Load", "StockTransfers.View", "StockTransfers.Dispatch");
        var receiverA = await BranchUserAsync(_r, s.A, "Shipments.View", "StockTransfers.View", "StockTransfers.Receive");
        var bystanderB = await BranchUserAsync(_r, s.B, "Shipments.View", "Shipments.Plan", "Shipments.Load", "StockTransfers.View", "StockTransfers.Dispatch", "StockTransfers.Receive");

        // The source delegate plans at the source branch ...
        var created = await CreateShipmentAsync(s, client: source.Client, key: NewKey("c"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var shipment = await JsonAsync(created);
        var sid = IdOf(shipment);
        // ... but not through another branch's header, and nobody else touches the shipment.
        Assert.Equal(HttpStatusCode.Created, (await CreateShipmentAsync(s, client: source.Client)).StatusCode); // its own branch again: fine
        var elsewhere = await SendAsync(source.Client, HttpMethod.Post, "/api/v1/shipments", new CreateShipmentRequest(s.SrcStore, TransportMode.Road, TransportVia.OwnFleet, null, null, null, null, "x"), s.A);
        Assert.Contains(elsewhere.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        foreach (var (who, branch) in new[] { (receiverA, s.A), (bystanderB, s.B) })
        {
            Assert.Contains((await SendAsync(who.Client, HttpMethod.Post, $"/api/v1/shipments/{sid}/allocations", new AllocateShipmentRequest(LineOf(request), s.Pc, 5m), branch, NewKey("a"))).StatusCode,
                new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
            Assert.Contains((await GetShipmentAsync(s, sid, who.Client, branch)).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        }
        Assert.Equal(0L, await CountAsync(w, $"""SELECT COUNT(*) FROM "ConsignmentLines" l JOIN "Consignments" c ON c."Id" = l."ConsignmentId" WHERE c."ShipmentId" = '{sid}'"""));

        // Plan, load, dispatch as the source delegate (own-fleet shipment needs a vehicle on the leg).
        var vehicle = await NewVehicleAsync(w);
        var withVehicle = await OkAsync(await SendAsync(source.Client, HttpMethod.Post, $"/api/v1/shipments/{sid}/vehicles", new AddShipmentVehicleRequest(vehicle, null, null, "Driver", "9000000000"), s.Src));
        var sv = withVehicle.GetProperty("vehicles")[0].GetProperty("id").GetGuid();
        var allocated = await OkAsync(await SendAsync(source.Client, HttpMethod.Post, $"/api/v1/shipments/{sid}/allocations", new AllocateShipmentRequest(LineOf(request), s.Pc, 5m), s.Src, NewKey("a")));
        var cons = ConsignmentTo(allocated, s.AStore);
        await OkAsync(await SendAsync(source.Client, HttpMethod.Put, $"/api/v1/shipments/{sid}/consignments/{IdOf(cons)}/vehicle", new AssignConsignmentVehicleRequest(sv), s.Src));
        await OkAsync(await SendAsync(source.Client, HttpMethod.Post, $"/api/v1/shipments/{sid}/consignments/{IdOf(cons)}/load",
            new LoadConsignmentRequest([new LoadConsignmentLineRequest(LineOfConsignment(cons).GetProperty("id").GetGuid(), 5m)]), s.Src));

        // The destination delegate and the bystander cannot dispatch (not at the source branch); the destination delegate has no dispatch permission at all.
        Assert.Contains((await SendAsync(receiverA.Client, HttpMethod.Post, $"/api/v1/shipments/{sid}/dispatch", null, s.A, NewKey("d"))).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Contains((await SendAsync(bystanderB.Client, HttpMethod.Post, $"/api/v1/shipments/{sid}/dispatch", null, s.B, NewKey("d"))).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Contains((await SendAsync(bystanderB.Client, HttpMethod.Post, $"/api/v1/shipments/{sid}/dispatch", null, s.Src, NewKey("d"))).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Equal("ReadyToDispatch", await ShipmentStatusAsync(w, sid));
        Assert.Equal(0L, await AuditsAsync(w, "shipment.dispatched"));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(source.Client, HttpMethod.Post, $"/api/v1/shipments/{sid}/dispatch", null, s.Src, NewKey("d"))).StatusCode);
        Assert.Equal(1L, await AuditsAsync(w, "shipment.dispatched"));

        // Receiving: the destination delegate at A only. The source delegate (no Receive) and the bystander of B are refused; at B's header the consignment of A is invisible.
        var consignment = IdOf(cons);
        var line = LineOfConsignment(cons).GetProperty("id").GetGuid();
        var body = new RecordReceiptRequest([new ReceiptLineRequest(line, 5m, 0m, 0m, 0m)], "c");
        Assert.Contains((await SendAsync(source.Client, HttpMethod.Post, $"/api/v1/consignments/{consignment}/receipts", body, s.Src, NewKey("r"))).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Contains((await SendAsync(bystanderB.Client, HttpMethod.Post, $"/api/v1/consignments/{consignment}/receipts", body, s.B, NewKey("r"))).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Contains((await SendAsync(receiverA.Client, HttpMethod.Post, $"/api/v1/consignments/{consignment}/receipts", body, s.B, NewKey("r"))).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Equal(0L, await CountAsync(w, $"""SELECT COUNT(*) FROM "ConsignmentReceipts" WHERE "ConsignmentId" = '{consignment}'"""));
        var recorded = await SendAsync(receiverA.Client, HttpMethod.Post, $"/api/v1/consignments/{consignment}/receipts", body, s.A, NewKey("r"));
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(receiverA.Client, HttpMethod.Post, $"/api/v1/consignments/{consignment}/receipts/{IdOf(await JsonAsync(recorded))}/post", null, s.A, NewKey("p"))).StatusCode);
        Assert.Equal(5m, await QohAsync(s, s.AStore));

        // Every write above left exactly its audit row, attributed to the delegate who did it.
        Assert.Equal(2L, await CountAsync(w, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'shipment.created' AND "ActorUserId" = '{source.Id}'"""));
        Assert.Equal(1L, await CountAsync(w, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'shipment.dispatched' AND "ActorUserId" = '{source.Id}'"""));
        Assert.Equal(1L, await CountAsync(w, $"""SELECT COUNT(*) FROM "AuditEvents" WHERE "Action" = 'consignment.receipt_posted' AND "ActorUserId" = '{receiverA.Id}'"""));
        Assert.Equal("Reconciled", await ConsignmentStatusAsync(w, consignment));
        await AssertAllScriptsCleanAsync(s, "scenario C");
    }

    private static Task<HttpResponseMessage> ApproveForAsync(HttpClient client, Guid branch, Guid consignment, Guid receipt, string? key = null) =>
        SendAsync(client, HttpMethod.Post, $"/api/v1/consignments/{consignment}/receipts/{receipt}/approve", null, branch, key ?? NewKey("appr"));

    private static async Task<decimal> TransitOfAsync(BranchApiWorld w, Guid product) =>
        await ScalarAsync<decimal>(w.Host.Database, $"""SELECT COALESCE(SUM(i."QuantityOnHand"), 0) FROM "StockItems" i JOIN "StockLocations" l ON l."Id" = i."StockLocationId" WHERE l."LocationType" = 'Transit' AND i."ProductId" = '{product}'""");

    private static async Task<decimal> ReservedOfAsync(BranchApiWorld w, Guid location, Guid product) =>
        await ScalarAsync<decimal>(w.Host.Database, $"""SELECT COALESCE(SUM("ReservedQuantity"), 0) FROM "StockItems" WHERE "StockLocationId" = '{location}' AND "ProductId" = '{product}'""");
}
