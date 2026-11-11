using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Stock;

/// <summary>
/// Phase 9 D3-D5 on EF InMemory with the Pen (pc 1, Box 10, Half Carton 50, Full Carton 100): unit snapshots (request, dispatch, receipt),
/// base quantities never rounded, partial dispatch, multiple partial receipts, damaged goods written off from Transit, excess refused,
/// resolve (WriteOff / ReturnToSource), batch identity and cost preserved, company value conserved. After every step the location
/// invariants (QOH = Σ movements = Σ balances, batch = Σ balances, V4 Transit = outstanding) hold.
/// </summary>
public class StockTransferQuantityTests
{
    private static async Task<(TransferWorld W, StockTransfer T)> DispatchedAsync(decimal boxes = 10m, decimal sourcePcs = 200m, decimal cost = 5m)
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, sourcePcs, cost);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, boxes));
        await w.DispatchAsRequestedAsync(t.Id);
        return (w, await w.ReloadAsync(t.Id));
    }

    // ---- unit snapshots -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_HalfCartonEntered_SnapshotsUnitAndFactor_AndTheBaseIs50Pieces()
    {
        var w = await TransferWorld.CreateAsync();
        var t = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Half, 1m)]);

        var line = Assert.Single(t.Lines);
        Assert.Equal((w.Half, "hctn", 50m, 1m, 50m), (line.ProductUnitId, line.UnitCode, line.ConversionFactorToBase, line.RequestedQty, line.RequestedBaseQty));
        Assert.Null(line.DispatchProductUnitId);
    }

    [Theory]
    [InlineData("pc", 1, 1)]
    [InlineData("box", 1, 10)]
    [InlineData("hctn", 1, 50)]
    [InlineData("fctn", 1, 100)]
    [InlineData("fctn", 2, 200)]
    public async Task Create_PenUnits_ConvertToBaseWithTheStoredFactor(string unit, decimal qty, decimal expectedBase)
    {
        var w = await TransferWorld.CreateAsync();
        var id = unit switch { "pc" => w.Pc, "box" => w.Box, "hctn" => w.Half, _ => w.Full };
        var t = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(id, qty)]);
        Assert.Equal(expectedBase, t.Lines.Single().RequestedBaseQty);
    }

    [Fact]
    public async Task Create_TwoProductsTwoUnits_EachLineKeepsItsOwnSnapshot()
    {
        var w = await TransferWorld.CreateAsync();
        var t = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore,
            [w.Pen(w.Box, 3m), TransferWorld.Line(w.FlourId, w.FlourKg, 2.5m), w.Pen(w.Full, 1m)]);

        Assert.Equal([30m, 2.5m, 100m], t.Lines.OrderBy(l => l.LineNo).Select(l => l.RequestedBaseQty));
        Assert.Equal([w.PenId, w.FlourId, w.PenId], t.Lines.OrderBy(l => l.LineNo).Select(l => l.ProductId));
    }

    [Fact]
    public async Task Dispatch_InADifferentUnitThanRequested_SnapshotsTheDispatchUnit_AndKeepsTheRequestSnapshot()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 300m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Half, 2m)); // requested 100 pc

        var line = t.Lines.Single();
        await w.DispatchAsync(t.Id, new DispatchStockTransferInput([new DispatchStockTransferLineInput(line.Id, w.Box, 5m)])); // ships 5 Box = 50 pc

        var after = (await w.ReloadAsync(t.Id)).Lines.Single();
        Assert.Equal(("hctn", 50m, 2m, 100m), (after.UnitCode, after.ConversionFactorToBase, after.RequestedQty, after.RequestedBaseQty));
        Assert.Equal((w.Box, "box", 10m, 5m, 50m), (after.DispatchProductUnitId, after.DispatchUnitCode, after.DispatchFactor, after.DispatchedQty, after.DispatchedBaseQty));
        Assert.Equal((250m, 50m), (await w.QohAsync(w.L.MainStore), await w.QohAsync(w.L.Transit)));
        var moves = await w.MovementsAsync(t.Id);
        Assert.All(moves, m => Assert.Equal((w.Box, 10m), (m.EnteredProductUnitId, m.ConversionFactorSnapshot)));
        await w.AssertInvariantsAsync("dispatch in box");
    }

    [Fact]
    public async Task Dispatch_PartialBelowRequested_ClosesTheRemainder_SoReceivingWhatShippedCompletes()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 300m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Full, 1m)); // requested 100 pc
        var line = t.Lines.Single();
        await w.DispatchAsync(t.Id, new DispatchStockTransferInput([new DispatchStockTransferLineInput(line.Id, w.Pc, 60m)]));

        var dispatched = await w.ReloadAsync(t.Id);
        Assert.Equal((StockTransferStatus.Dispatched, 60m, 100m), (dispatched.Status, dispatched.Lines.Single().DispatchedBaseQty, dispatched.Lines.Single().RequestedBaseQty));
        Assert.Equal(240m, await w.QohAsync(w.L.MainStore)); // only what shipped left the source

        var result = await w.ReceiveAsync(t.Id, line.Id, w.Pc, 60m);
        Assert.Equal(StockTransferStatus.Completed, result!.Transfer.Status); // the 40 not shipped never hold the transfer open
        Assert.Equal((60m, 0m), (await w.QohAsync(w.L.BlrStore), await w.QohAsync(w.L.Transit)));
        await w.AssertInvariantsAsync("partial dispatch completed");
    }

    [Fact]
    public async Task Dispatch_ZeroOnOneLine_ShipsTheOther_AndTheZeroLineHasASnapshotButNoMovementsOrBatches()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 300m);
        await w.StockAsync(w.L.MainStore, w.FlourId, 50m, 3m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 4m), TransferWorld.Line(w.FlourId, w.FlourKg, 5m));
        var (pen, flour) = (t.Lines.Single(l => l.ProductId == w.PenId), t.Lines.Single(l => l.ProductId == w.FlourId));

        await w.DispatchAsync(t.Id, new DispatchStockTransferInput([new DispatchStockTransferLineInput(pen.Id, w.Box, 4m), new DispatchStockTransferLineInput(flour.Id, w.FlourKg, 0m)]));

        var after = await w.ReloadAsync(t.Id);
        var zero = after.Lines.Single(l => l.ProductId == w.FlourId);
        Assert.Equal((0m, w.FlourKg), (zero.DispatchedBaseQty, zero.DispatchProductUnitId));
        Assert.Empty(zero.Batches);
        Assert.Equal(50m, await w.QohAsync(w.L.MainStore, w.FlourId));
        Assert.Equal(40m, after.Lines.Single(l => l.ProductId == w.PenId).DispatchedBaseQty);
        await w.AssertInvariantsAsync("zero line");
    }

    [Fact]
    public async Task Dispatch_InputShape_AllZero_MissingLine_DuplicateLine_UnknownLine_NegativeQuantity_AreRefused()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 300m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 4m), w.Pen(w.Pc, 3m));
        var (a, b) = (t.Lines[0], t.Lines[1]);
        DispatchStockTransferLineInput L(Guid id, decimal q) => new(id, w.Pc, q);

        Assert.Equal("lines", await LocationWorld.FieldAsync(() => w.DispatchAsync(t.Id, new([L(a.Id, 0m), L(b.Id, 0m)]))));
        Assert.Equal("lines", await LocationWorld.FieldAsync(() => w.DispatchAsync(t.Id, new([L(a.Id, 5m)]))));
        Assert.Equal("lines", await LocationWorld.FieldAsync(() => w.DispatchAsync(t.Id, new([]))));
        Assert.Equal("lines[1].lineId", await LocationWorld.FieldAsync(() => w.DispatchAsync(t.Id, new([L(a.Id, 1m), L(a.Id, 1m)]))));
        Assert.Equal("lines[1].lineId", await LocationWorld.FieldAsync(() => w.DispatchAsync(t.Id, new([L(a.Id, 1m), L(Guid.NewGuid(), 1m)]))));
        Assert.Equal("lines[0].quantity", await LocationWorld.FieldAsync(() => w.DispatchAsync(t.Id, new([L(a.Id, -1m), L(b.Id, 1m)]))));
        Assert.Equal(StockTransferStatus.Approved, (await w.ReloadAsync(t.Id)).Status);
        Assert.Equal(300m, await w.QohAsync(w.L.MainStore));
    }

    [Fact]
    public async Task Dispatch_MoreThanRequested_OrMoreThanInStock_IsRefusedWithNothingMoved()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 30m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 5m)); // 50 pc requested, 30 in stock
        var line = t.Lines.Single();

        Assert.Equal("lines[0].quantity", await LocationWorld.FieldAsync(() => w.DispatchAsync(t.Id, new([new(line.Id, w.Box, 6m)])))); // above requested
        Assert.Equal("lines[0].quantity", await LocationWorld.FieldAsync(() => w.DispatchAsync(t.Id, new([new(line.Id, w.Box, 4m)])))); // 40 > 30 in stock

        Assert.Equal((30m, 0m), (await w.QohAsync(w.L.MainStore), await w.QohAsync(w.L.Transit)));
        Assert.Equal(StockTransferStatus.Approved, (await w.ReloadAsync(t.Id)).Status);
        await w.AssertInvariantsAsync("refused dispatch");
    }

    [Fact]
    public async Task Dispatch_TwoLinesOfTheSameProductInDifferentUnits_ChecksAvailabilityOnTheirSum()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Full, 1m), w.Pen(w.Box, 1m)); // 100 + 10 > 100 in stock
        var (a, b) = (t.Lines[0], t.Lines[1]);

        Assert.Equal("lines[0].quantity", await LocationWorld.FieldAsync(() => w.DispatchAsync(t.Id, new([new(a.Id, w.Full, 1m), new(b.Id, w.Box, 1m)]))));
        Assert.Equal(100m, await w.QohAsync(w.L.MainStore));
    }

    // ---- base quantity is never rounded ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_BaseQuantityNotStorableAtThreeDecimals_IsRejected_NeverRounded()
    {
        var w = await TransferWorld.CreateAsync();
        // 1 pouch = 0.3335 kg: entered 1 is fine as a count, the base 0.3335 kg has 4 decimals.
        Assert.Equal("lines[0].quantity", await LocationWorld.FieldAsync(() =>
            w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [TransferWorld.Line(w.FlourId, w.FlourPouch, 1m)])));
        // 3 pouches = 1.0005 kg: also not storable; 2 pouches = 0.667 is.
        Assert.Equal("lines[0].quantity", await LocationWorld.FieldAsync(() =>
            w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [TransferWorld.Line(w.FlourId, w.FlourPouch, 3m)])));
        var ok = await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [TransferWorld.Line(w.FlourId, w.FlourPouch, 2m)]);
        Assert.Equal(0.667m, ok.Lines.Single().RequestedBaseQty);
        Assert.Equal(1, await w.TransferCountAsync());
    }

    [Theory]
    [InlineData(0.5)]       // a fraction of a Piece/Box (0 decimals)
    [InlineData(1.0001)]
    public async Task Create_EnteredQuantityBeyondTheUnitsDecimalPlaces_IsRejected(double qty)
    {
        var w = await TransferWorld.CreateAsync();
        Assert.Equal("lines[0].quantity", await LocationWorld.FieldAsync(() => w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, (decimal)qty)])));
    }

    [Fact]
    public async Task Dispatch_AndReceive_NonStorableBase_AreRejected_AndPostNothing()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockAsync(w.L.MainStore, w.FlourId, 10m, 4m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, TransferWorld.Line(w.FlourId, w.FlourKg, 3m));
        var line = t.Lines.Single();

        Assert.Equal("lines[0].quantity", await LocationWorld.FieldAsync(() => w.DispatchAsync(t.Id, new([new(line.Id, w.FlourPouch, 1m)]))));
        Assert.Equal((10m, 0m), (await w.QohAsync(w.L.MainStore, w.FlourId), await w.QohAsync(w.L.Transit, w.FlourId)));

        await w.DispatchAsync(t.Id, new([new(line.Id, w.FlourKg, 3m)]));
        Assert.Equal("lines[0].acceptedQuantity", await LocationWorld.FieldAsync(() => w.ReceiveAsync(t.Id, line.Id, w.FlourPouch, 1m)));
        Assert.Equal("lines[0].damagedQuantity", await LocationWorld.FieldAsync(() => w.ReceiveAsync(t.Id, line.Id, w.FlourPouch, 0m, 1m)));
        Assert.Equal(3m, await w.QohAsync(w.L.Transit, w.FlourId));
        await w.AssertInvariantsAsync("non-storable refused");
    }

    // ---- receipts -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Receive_SeveralPartialReceipts_InDifferentUnits_CompleteTheTransferOnTheLast()
    {
        var (w, t) = await DispatchedAsync(boxes: 10m);
        var line = t.Lines.Single();

        var r1 = await w.ReceiveAsync(t.Id, line.Id, w.Box, 3m);
        Assert.Equal(StockTransferStatus.PartiallyReceived, r1!.Transfer.Status);
        Assert.Equal((30m, 70m, 100m), (await w.QohAsync(w.L.BlrStore), await w.QohAsync(w.L.Transit), await w.QohAsync(w.L.MainStore)));
        await w.AssertInvariantsAsync("receipt 1");

        var r2 = await w.ReceiveAsync(t.Id, line.Id, w.Pc, 20m);
        Assert.Equal(StockTransferStatus.PartiallyReceived, r2!.Transfer.Status);
        await w.AssertInvariantsAsync("receipt 2");

        var r3 = await w.ReceiveAsync(t.Id, line.Id, w.Half, 1m); // 50 pc exactly what is left
        Assert.Equal(StockTransferStatus.Completed, r3!.Transfer.Status);
        Assert.NotNull(r3.Transfer.CompletedAt);

        var done = await w.ReloadAsync(t.Id);
        Assert.Equal([1, 2, 3], done.Receipts.OrderBy(r => r.Sequence).Select(r => r.Sequence));
        Assert.Equal([30m, 20m, 50m], done.Receipts.OrderBy(r => r.Sequence).Select(r => r.Lines.Single().AcceptedBaseQty));
        Assert.Equal(["box", "pc", "hctn"], done.Receipts.OrderBy(r => r.Sequence).Select(r => r.Lines.Single().UnitCode));
        Assert.Equal(100m, done.Lines.Single().ReceivedBaseQty);
        Assert.Equal((100m, 0m), (await w.QohAsync(w.L.BlrStore), await w.QohAsync(w.L.Transit)));
        await w.AssertInvariantsAsync("receipt 3");
    }

    [Fact]
    public async Task Receive_Damaged_IsWrittenOffFromTransit_AsAdjustmentDamage_AndNeverEntersTheDestination()
    {
        var (w, t) = await DispatchedAsync(boxes: 10m);
        var line = t.Lines.Single();
        var batchBefore = await RemainingOfBatchesAsync(w);

        var result = await w.ReceiveAsync(t.Id, line.Id, w.Box, 8m, damaged: 2m);

        Assert.Equal(StockTransferStatus.Completed, result!.Transfer.Status);
        Assert.Equal((80m, 0m, 100m), (await w.QohAsync(w.L.BlrStore), await w.QohAsync(w.L.Transit), await w.QohAsync(w.L.MainStore)));
        var damage = Assert.Single(await w.MovementsAsync(t.Id), m => m.Type == StockMovementType.Adjustment);
        Assert.Equal((w.L.Transit, -20m, AdjustmentType.Damage, "StockTransfer"), (damage.StockLocationId, damage.QuantityBaseUnits, damage.ReasonCode, damage.ReferenceType));
        Assert.Equal(batchBefore - 20m, await RemainingOfBatchesAsync(w)); // the damaged 20 leave the company
        Assert.Equal(180m, await w.CompanyQuantityAsync());
        var after = (await w.ReloadAsync(t.Id)).Lines.Single();
        Assert.Equal((80m, 20m), (after.ReceivedBaseQty, after.DamagedBaseQty));
        await w.AssertInvariantsAsync("damaged");
    }

    [Fact]
    public async Task Receive_MoreThanOutstanding_IsRefusedOnAcceptedQuantity_AndPostsNothing()
    {
        var (w, t) = await DispatchedAsync(boxes: 10m);
        var line = t.Lines.Single();
        await w.ReceiveAsync(t.Id, line.Id, w.Box, 6m);
        var movements = (await w.MovementsAsync(t.Id)).Count;

        Assert.Equal("lines[0].acceptedQuantity", await LocationWorld.FieldAsync(() => w.ReceiveAsync(t.Id, line.Id, w.Box, 5m)));              // 50 > 40 outstanding
        Assert.Equal("lines[0].acceptedQuantity", await LocationWorld.FieldAsync(() => w.ReceiveAsync(t.Id, line.Id, w.Box, 3m, damaged: 2m)));   // 30 + 20 > 40
        Assert.Equal("lines[0].acceptedQuantity", await LocationWorld.FieldAsync(() => w.ReceiveAsync(t.Id, line.Id, w.Pc, 0m, damaged: 41m)));   // damaged alone beyond outstanding

        Assert.Equal(movements, (await w.MovementsAsync(t.Id)).Count);
        Assert.Equal((60m, 40m), (await w.QohAsync(w.L.BlrStore), await w.QohAsync(w.L.Transit)));
        Assert.Single((await w.ReloadAsync(t.Id)).Receipts);
        await w.AssertInvariantsAsync("excess refused");
    }

    [Fact]
    public async Task Receive_InputShape_AllZeroNoLinesUnknownDuplicateNegative_AreRefused()
    {
        var (w, t) = await DispatchedAsync(boxes: 10m);
        var line = t.Lines.Single();
        ReceiveStockTransferLineInput L(Guid id, decimal a, decimal d = 0m) => new(id, w.Box, a, d);

        Assert.Equal("lines", await LocationWorld.FieldAsync(() => w.ReceiveAsync(t.Id, new(null, [L(line.Id, 0m)]))));
        Assert.Equal("lines", await LocationWorld.FieldAsync(() => w.ReceiveAsync(t.Id, new(null, []))));
        Assert.Equal("lines[0].lineId", await LocationWorld.FieldAsync(() => w.ReceiveAsync(t.Id, new(null, [L(Guid.NewGuid(), 1m)]))));
        Assert.Equal("lines[1].lineId", await LocationWorld.FieldAsync(() => w.ReceiveAsync(t.Id, new(null, [L(line.Id, 1m), L(line.Id, 1m)]))));
        Assert.Equal("lines[0].acceptedQuantity", await LocationWorld.FieldAsync(() => w.ReceiveAsync(t.Id, new(null, [L(line.Id, -1m)]))));
        Assert.Equal("lines[0].damagedQuantity", await LocationWorld.FieldAsync(() => w.ReceiveAsync(t.Id, new(null, [L(line.Id, 1m, -1m)]))));
        Assert.Equal("lines[0].productUnitId", await LocationWorld.FieldAsync(() => w.ReceiveAsync(t.Id, new(null, [new(line.Id, w.FlourKg, 1m, 0m)]))));
        Assert.Empty((await w.ReloadAsync(t.Id)).Receipts);
    }

    [Fact]
    public async Task Receive_ASecondLineWithNothingToRecord_IsSkipped_AndDoesNotAppearOnTheReceipt()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 300m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 4m), w.Pen(w.Pc, 5m));
        await w.DispatchAsRequestedAsync(t.Id);
        var (a, b) = (t.Lines[0], t.Lines[1]);

        var r = await w.ReceiveAsync(t.Id, new(null, [new(a.Id, w.Box, 4m, 0m), new(b.Id, w.Pc, 0m, 0m)]));

        Assert.Equal(StockTransferStatus.PartiallyReceived, r!.Transfer.Status);
        Assert.Single((await w.ReloadAsync(t.Id)).Receipts.Single().Lines);
    }

    // ---- resolve ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resolve_WriteOff_DropsTheBatchTotal_AndLeavesTheCompany()
    {
        var (w, t) = await DispatchedAsync(boxes: 10m);
        var line = t.Lines.Single();
        await w.ReceiveAsync(t.Id, line.Id, w.Box, 4m);
        var batchBefore = await RemainingOfBatchesAsync(w);
        var valueBefore = await w.CompanyValueAsync();

        var result = await w.ResolveAsync(t.Id, new("lost in transit", [new(line.Id, StockTransferResolution.WriteOff)]));

        Assert.Equal(StockTransferStatus.Completed, result!.Transfer.Status);
        Assert.Equal((40m, 0m, 100m), (await w.QohAsync(w.L.BlrStore), await w.QohAsync(w.L.Transit), await w.QohAsync(w.L.MainStore)));
        Assert.Equal(batchBefore - 60m, await RemainingOfBatchesAsync(w));
        Assert.Equal(valueBefore - 60m * 5m, await w.CompanyValueAsync());
        var loss = Assert.Single(await w.MovementsAsync(t.Id), m => m.ReasonCode == AdjustmentType.Loss);
        Assert.Equal((StockMovementType.Adjustment, w.L.Transit, -60m), (loss.Type, loss.StockLocationId, loss.QuantityBaseUnits));
        var done = await w.ReloadAsync(t.Id);
        Assert.Equal((60m, 0m), (done.Lines.Single().WrittenOffBaseQty, done.Lines.Single().ReturnedBaseQty));
        var resolution = done.Receipts.Single(r => r.Kind == StockTransferReceiptKind.Resolution);
        Assert.Equal(("lost in transit", 60m, 0m), (resolution.Notes, resolution.Lines.Single().WrittenOffBaseQty, resolution.Lines.Single().AcceptedBaseQty));
        await w.AssertInvariantsAsync("write-off");
    }

    [Fact]
    public async Task Resolve_ReturnToSource_PutsTheRemainderBackAtTheSource_WithTheBatchCostPreserved()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 30m, cost: 5m);   // B1
        await w.StockPenAsync(w.L.MainStore, 70m, cost: 8m);   // B2
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Half, 1m)); // 50 pc = all of B1 + 20 of B2
        await w.DispatchAsRequestedAsync(t.Id);
        var line = (await w.ReloadAsync(t.Id)).Lines.Single();
        await w.ReceiveAsync(t.Id, line.Id, w.Pc, 10m);
        var batchTotal = await RemainingOfBatchesAsync(w);
        var value = await w.CompanyValueAsync();

        var result = await w.ResolveAsync(t.Id, new("send back", [new(line.Id, StockTransferResolution.ReturnToSource)]));

        Assert.Equal(StockTransferStatus.Completed, result!.Transfer.Status);
        Assert.Equal((90m, 10m, 0m), (await w.QohAsync(w.L.MainStore), await w.QohAsync(w.L.BlrStore), await w.QohAsync(w.L.Transit)));
        Assert.Equal(batchTotal, await RemainingOfBatchesAsync(w)); // a return changes no batch total
        await w.AssertValueConservedAsync(value, "return to source");
        // The 40 returned are B1's 20 outstanding + B2's 20 outstanding minus what was received first (FIFO: 10 of B1 received): batch rows keep their cost.
        var done = await w.ReloadAsync(t.Id);
        Assert.Equal([5m, 8m], done.Lines.Single().Batches.Select(b => b.UnitCost).Order());
        Assert.Equal(40m, done.Lines.Single().ReturnedBaseQty);
        Assert.All(done.Lines.Single().Batches, b => Assert.Equal(0m, b.DispatchedBaseQty - b.ReceivedBaseQty - b.DamagedBaseQty - b.WrittenOffBaseQty - b.ReturnedBaseQty));
        await using var db = w.Db();
        var atSource = await db.BatchLocationBalances.Where(b => b.StockLocationId == w.L.MainStore && b.ProductId == w.PenId && b.RemainingBaseQty > 0)
            .Join(db.PurchaseBatches, bl => bl.PurchaseBatchId, pb => pb.Id, (bl, pb) => new { pb.UnitCost, bl.RemainingBaseQty }).OrderBy(x => x.UnitCost).ToListAsync();
        Assert.Equal([(5m, 20m), (8m, 70m)], atSource.Select(x => (x.UnitCost, x.RemainingBaseQty)));
        await w.AssertInvariantsAsync("return to source");
    }

    [Fact]
    public async Task Resolve_MixedActionsPerLine_AndLinesWithNothingOutstandingMayBeOmitted()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 300m);
        await w.StockAsync(w.L.MainStore, w.FlourId, 40m, 3m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 5m), TransferWorld.Line(w.FlourId, w.FlourKg, 10m), w.Pen(w.Pc, 7m));
        await w.DispatchAsRequestedAsync(t.Id);
        var lines = (await w.ReloadAsync(t.Id)).Lines.OrderBy(l => l.LineNo).ToList();
        await w.ReceiveAsync(t.Id, lines[2].Id, w.Pc, 7m); // third line fully received

        // Missing an action for an outstanding line is refused.
        Assert.Equal("lines", await LocationWorld.FieldAsync(() => w.ResolveAsync(t.Id, new("x", [new(lines[0].Id, StockTransferResolution.WriteOff)]))));
        Assert.Equal("lines[0].lineId", await LocationWorld.FieldAsync(() => w.ResolveAsync(t.Id, new("x", [new(Guid.NewGuid(), StockTransferResolution.WriteOff)]))));
        Assert.Equal("reason", await LocationWorld.FieldAsync(() => w.ResolveAsync(t.Id, new(" ", [new(lines[0].Id, StockTransferResolution.WriteOff)]))));

        var result = await w.ResolveAsync(t.Id, new("mixed", [new(lines[0].Id, StockTransferResolution.WriteOff), new(lines[1].Id, StockTransferResolution.ReturnToSource)]));

        Assert.Equal(StockTransferStatus.Completed, result!.Transfer.Status);
        Assert.Equal((243m, 7m, 0m), (await w.QohAsync(w.L.MainStore), await w.QohAsync(w.L.BlrStore), await w.QohAsync(w.L.Transit))); // 300 - 50 (written off) - 7 (received)
        Assert.Equal(40m, await w.QohAsync(w.L.MainStore, w.FlourId));
        await w.AssertInvariantsAsync("mixed resolve");
    }

    [Fact]
    public async Task Resolve_OnACompletedOrNotYetDispatchedTransfer_IsRefusedOnStatus()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 1m));
        var line = t.Lines.Single();
        Assert.Equal("status", await LocationWorld.FieldAsync(() => w.ResolveAsync(t.Id, new("x", [new(line.Id, StockTransferResolution.WriteOff)]))));
    }

    // ---- batch identity, cost and value ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dispatch_ConsumesFifoAtTheSource_KeepsBatchIdentityAndCost_AndConservesCompanyValue()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 30m, cost: 5m);
        await w.StockPenAsync(w.L.MainStore, 70m, cost: 8m);
        var valueBefore = await w.CompanyValueAsync();
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Half, 1m));

        await w.DispatchAsRequestedAsync(t.Id);

        var line = (await w.ReloadAsync(t.Id)).Lines.Single();
        Assert.Equal([(5m, 30m), (8m, 20m)], line.Batches.OrderBy(b => b.UnitCost).Select(b => (b.UnitCost, b.DispatchedBaseQty)));
        var batchTotals = await RemainingOfBatchesAsync(w);
        Assert.Equal(100m, batchTotals); // a move leaves every batch total alone
        Assert.Equal((50m, 50m), (await w.QohAsync(w.L.MainStore), await w.QohAsync(w.L.Transit)));
        await w.AssertValueConservedAsync(valueBefore, "transfer");
        Assert.Equal(8m, await w.AverageCostAsync(w.L.MainStore));
        Assert.Equal((30m * 5m + 20m * 8m) / 50m, await w.AverageCostAsync(w.L.Transit), 6);

        var received = await w.ReceiveAsync(t.Id, line.Id, w.Half, 1m);
        Assert.Equal(StockTransferStatus.Completed, received!.Transfer.Status);
        await w.AssertValueConservedAsync(valueBefore, "transfer");
        Assert.Equal((30m * 5m + 20m * 8m) / 50m, await w.AverageCostAsync(w.L.BlrStore), 6);
        Assert.Equal(2, line.Batches.Count);
        await w.AssertInvariantsAsync("fifo dispatch + receipt");
    }

    [Fact]
    public async Task Movements_OnePerLineAndBatch_CarryTheTransferReference_AndEnteredQuantityOnlyWhenOneBatchCarriesTheLine()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 30m, cost: 5m);
        await w.StockPenAsync(w.L.MainStore, 70m, cost: 8m);
        var multi = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Half, 1m));   // spans two batches
        var single = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 2m));   // inside B2 only
        await w.DispatchAsRequestedAsync(multi.Id);
        await w.DispatchAsRequestedAsync(single.Id);

        var m = await w.MovementsAsync(multi.Id);
        Assert.Equal(4, m.Count); // 2 batches x (out + in)
        Assert.All(m, x => Assert.Equal((multi.TransferNo, "StockTransfer", null as decimal?), (x.ReferenceNo, x.ReferenceType, x.EnteredQuantity)));
        Assert.Equal(2, m.Count(x => x.Type == StockMovementType.TransferOut && x.StockLocationId == w.L.MainStore && x.QuantityBaseUnits < 0));
        Assert.Equal(2, m.Count(x => x.Type == StockMovementType.TransferIn && x.StockLocationId == w.L.Transit && x.QuantityBaseUnits > 0));
        var s = await w.MovementsAsync(single.Id);
        Assert.Equal(2, s.Count);
        Assert.All(s, x => Assert.Equal(2m, x.EnteredQuantity));
    }

    [Fact]
    public async Task Receive_FromTwoTransfersOfTheSameProduct_EachDrawsFromItsOwnBatches_NotTransitFifo()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 50m, cost: 5m);   // B1
        await w.StockPenAsync(w.L.MainStore, 50m, cost: 9m);   // B2
        var first = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Pc, 50m));  // takes B1
        var second = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Pc, 50m)); // takes B2
        await w.DispatchAsRequestedAsync(first.Id);
        await w.DispatchAsRequestedAsync(second.Id);

        // Receiving the SECOND transfer first must move B2 (its own batch), not B1 (the oldest at Transit).
        var line2 = (await w.ReloadAsync(second.Id)).Lines.Single();
        await w.ReceiveAsync(second.Id, line2.Id, w.Pc, 50m);

        await using var db = w.Db();
        var atBlr = await db.BatchLocationBalances.Where(b => b.StockLocationId == w.L.BlrStore).Join(db.PurchaseBatches, bl => bl.PurchaseBatchId, pb => pb.Id, (bl, pb) => new { pb.UnitCost, bl.RemainingBaseQty }).ToListAsync();
        Assert.Equal((9m, 50m), (atBlr.Single().UnitCost, atBlr.Single().RemainingBaseQty));
        Assert.Equal(9m, await w.AverageCostAsync(w.L.BlrStore));
        await w.AssertInvariantsAsync("two transfers, out-of-order receipt");
    }

    [Fact]
    public async Task Cancel_AfterDispatch_IsRefused_AndTheStockStaysInTransit()
    {
        var (w, t) = await DispatchedAsync(boxes: 10m);
        await using var db = w.Db();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => new StockTransferService(db, TransferWorld.Everything()).CancelAsync(t.Id, "oops", w.W.UserId));
        Assert.Equal("status", ex.Field);
        Assert.Equal(100m, await w.QohAsync(w.L.Transit));
    }

    [Fact]
    public async Task Cancel_BeforeDispatch_MovesNoStock_AndNeedsAReason()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Box, 1m));
        await using var db = w.Db();
        var service = new StockTransferService(db, TransferWorld.Everything());

        Assert.Equal("reason", await LocationWorld.FieldAsync(() => service.CancelAsync(t.Id, "  ", w.W.UserId)));
        var cancelled = await service.CancelAsync(t.Id, "not needed", w.W.UserId);

        Assert.Equal((StockTransferStatus.Cancelled, "not needed"), (cancelled!.Status, cancelled.CancellationReason));
        Assert.Equal(100m, await w.QohAsync(w.L.MainStore));
        Assert.Empty(await w.MovementsAsync(t.Id));
    }

    [Fact]
    public async Task ProductUnit_ReferencedByADraftLine_IsLockedAndImmutable()
    {
        var w = await TransferWorld.CreateAsync();
        await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m)]); // Draft only

        await using var db = w.Db();
        var units = await db.ProductUnits.AsNoTracking().Include(u => u.Unit).Where(u => u.ProductId == w.PenId).ToListAsync();
        var reasons = await ProductUnitUsage.GetLockReasonsAsync(db, units, CancellationToken.None);

        Assert.Equal(ProductUnitUsage.Transactions, reasons[w.Box]);
        Assert.Equal(ProductUnitUsage.ContainedInLockedUnit, reasons[w.Pc]);   // Box contains Piece: changing Piece would change Box's factor
        Assert.DoesNotContain(w.Half, reasons.Keys);                           // wraps Box, untouched
        Assert.DoesNotContain(w.Full, reasons.Keys);
    }

    [Fact]
    public async Task ProductUnit_UsedOnlyAsADispatchUnit_IsLocked()
    {
        var w = await TransferWorld.CreateAsync();
        await w.StockPenAsync(w.L.MainStore, 100m);
        var t = await w.ApprovedAsync(w.L.MainStore, w.L.BlrStore, w.Pen(w.Pc, 10m)); // requested in pieces
        await w.DispatchAsync(t.Id, new([new(t.Lines.Single().Id, w.Box, 1m)]));       // shipped as one Box

        await using var db = w.Db();
        var units = await db.ProductUnits.AsNoTracking().Include(u => u.Unit).Where(u => u.ProductId == w.PenId).ToListAsync();
        var reasons = await ProductUnitUsage.GetLockReasonsAsync(db, units, CancellationToken.None);

        Assert.Equal(ProductUnitUsage.Transactions, reasons[w.Box]);
        Assert.Equal(ProductUnitUsage.Transactions, reasons[w.Pc]);
    }

    [Fact]
    public async Task ProductService_ChangingTheFactorOfAUnitOnATransferLine_IsRefused()
    {
        var w = await TransferWorld.CreateAsync();
        await w.CreateTransferAsync(w.L.MainStore, w.L.BlrStore, [w.Pen(w.Box, 1m)]);

        await using var db = w.Db();
        var product = await db.Products.Include(p => p.Units).ThenInclude(u => u.Unit).SingleAsync(p => p.Id == w.PenId);
        var items = product.Units.Select(u => new Sellora.Domain.Inventory.ProductUnitInputItem(u.Id, u.UnitId, u.InnerProductUnitId is { } i ? product.Units.Single(x => x.Id == i).UnitId : null,
            u.Id == w.Box ? 12m : u.InnerQuantity, u.Label, null, u.IsPurchasable, u.IsSellable, u.IsDefaultPurchaseUnit, u.IsDefaultSalesUnit, u.IsActive)).ToList();

        await Assert.ThrowsAsync<DomainValidationException>(() => new ProductService(db).UpdateProductAsync(w.PenId,
            new Sellora.Domain.Inventory.UpsertProductInput(product.Sku, product.Name, product.Category, product.BaseUnitId, product.TaxId, product.ReorderLevel, items)));
    }

    private static async Task<decimal> RemainingOfBatchesAsync(TransferWorld w)
    {
        await using var db = w.Db();
        return await db.PurchaseBatches.Where(b => b.ProductId == w.PenId).SumAsync(b => b.RemainingBaseQty);
    }
}
