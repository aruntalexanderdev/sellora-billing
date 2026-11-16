using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Phase 9 stock transfer lifecycle (design docs/architecture/multi-company/phase-9-design.md). Entered quantities are
/// converted to base units once per document line with the unit row's stored factor and snapshotted (request, dispatch and
/// every receipt line); stock state, batches and the ledger are base units only. All stock mutation goes through
/// <see cref="StockPosting"/>; this class only builds documents, counters and ledger rows.
/// Lock order for dispatch / receipt / resolve (D6): idempotency advisory lock → transfer header FOR UPDATE → products KEY
/// SHARE → locations KEY SHARE {endpoint, Transit} → StockItems ensure + FOR UPDATE over {(endpoint, p), (Transit, p)}.
/// Create / edit: products KEY SHARE → endpoint locations KEY SHARE → TRF number; no stock locks.
/// </summary>
public class StockTransferService(SelloraDbContext db, IEffectivePermissionService? permissions = null) : IStockTransferService
{
    private const int MaxText = 300;
    private const int MaxCloseReason = 500;
    private const string LocationField = "stockLocationId";
    private const string InvalidLocation = "Select a valid, active stock location.";

    private enum Fit { Source, Destination, Either }

    // ------------------------------------------------------------------ create / edit / state changes

    public async Task<StockTransfer> CreateAsync(CreateStockTransferInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var notes = Text(input.Notes, "notes", required: false);
        RequireLines(input.Lines);
        RequireDifferentEndpoints(input.SourceLocationId, input.DestinationLocationId);

        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, userId, Idempotency.StockTransfer, input.IdempotencyKey, ct) is { } existingId)
        {
            return await LoadGraphAsync(existingId, ct)
                ?? throw new DomainValidationException("IdempotencyKey", "This request was already processed, but its result is no longer visible with your current branch access.");
        }

        await StockLocks.LockProductsKeyShareAsync(db, input.Lines.Select(l => l.ProductId), ct);

        var branch = await BranchRules.ResolveWorkingBranchAsync(db, ct);
        var (source, destination, sourceBranch, destinationBranch) = await ResolveEndpointsAsync(input.SourceLocationId, input.DestinationLocationId, ct);
        await EnsureFitAsync("StockTransfers.Create", Fit.Either, source, destination, ct);
        await StockLocks.LockLocationsKeyShareAsync(db, [source.Id, destination.Id], ct);
        await ResolveEndpointsAsync(input.SourceLocationId, input.DestinationLocationId, ct);

        var transit = await GetTransitAsync(ct);
        var transferId = Guid.NewGuid();
        var lines = await BuildRequestLinesAsync(transferId, input.Lines, ct);
        var transferNo = await DocumentNumbers.NextNumberAsync(db, DocumentSeries.Transfer, branch, ct);

        var now = DateTimeOffset.UtcNow;
        var transfer = new StockTransfer
        {
            Id = transferId,
            TransferNo = transferNo,
            BranchId = branch.Id,
            SourceLocationId = source.Id,
            DestinationLocationId = destination.Id,
            TransitLocationId = transit.Id,
            SourceBranchId = sourceBranch,
            DestinationBranchId = destinationBranch,
            Status = input.Submit ? StockTransferStatus.Requested : StockTransferStatus.Draft,
            Notes = notes,
            CreatedAt = now,
            CreatedByUserId = userId,
            SubmittedAt = input.Submit ? now : null,
            SubmittedByUserId = input.Submit ? userId : null,
            Lines = lines,
        };
        db.StockTransfers.Add(transfer);

        Audit(userId, correlationId, AuditActions.StockTransferCreated, transfer, null, Snapshot(transfer, StockTransferStatus.Draft));
        if (input.Submit)
        {
            Audit(userId, correlationId, AuditActions.StockTransferSubmitted, transfer, StockTransferStatus.Draft, Snapshot(transfer));
        }

        Idempotency.Record(db, userId, Idempotency.StockTransfer, input.IdempotencyKey, transfer.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return transfer;
    }

    public async Task<StockTransfer?> UpdateDraftAsync(Guid id, UpdateStockTransferInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var notes = Text(input.Notes, "notes", required: false);
        RequireLines(input.Lines);
        RequireDifferentEndpoints(input.SourceLocationId, input.DestinationLocationId);

        await using var transaction = await StockLocks.BeginAsync(db, ct);

        var transfer = await LockAndLoadAsync(id, ct);
        if (transfer is null) return null;
        EnsureStatus(transfer, StockTransferStatus.Draft);

        // The caller must be able to work with the draft as it stands and with the endpoints it is moved to.
        var (oldSource, oldDestination) = await LoadEndpointsAsync(transfer, ct);
        await EnsureFitAsync("StockTransfers.Create", Fit.Either, oldSource, oldDestination, ct);

        await StockLocks.LockProductsKeyShareAsync(db, input.Lines.Select(l => l.ProductId), ct);

        var (source, destination, sourceBranch, destinationBranch) = await ResolveEndpointsAsync(input.SourceLocationId, input.DestinationLocationId, ct);
        await EnsureFitAsync("StockTransfers.Create", Fit.Either, source, destination, ct);
        await StockLocks.LockLocationsKeyShareAsync(db, [source.Id, destination.Id], ct);
        await ResolveEndpointsAsync(input.SourceLocationId, input.DestinationLocationId, ct);

        var lines = await BuildRequestLinesAsync(transfer.Id, input.Lines, ct);

        // Delete first: the (TransferId, LineNo) unique index would otherwise see old and new rows together.
        db.StockTransferLines.RemoveRange(transfer.Lines.ToList());
        await db.SaveChangesAsync(ct);

        transfer.SourceLocationId = source.Id;
        transfer.DestinationLocationId = destination.Id;
        transfer.SourceBranchId = sourceBranch;
        transfer.DestinationBranchId = destinationBranch;
        transfer.Notes = notes;
        // Add explicitly: the new lines carry preset ids, so collection fix-up would treat them as existing (Modified) rows.
        db.StockTransferLines.AddRange(lines);

        Audit(userId, correlationId, AuditActions.StockTransferUpdated, transfer, StockTransferStatus.Draft, Snapshot(transfer));
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Finish(transfer);
    }

    public Task<StockTransfer?> SubmitAsync(Guid id, Guid userId, string? correlationId = null, CancellationToken ct = default) =>
        TransitionAsync(id, userId, correlationId, StockTransferStatus.Requested, [StockTransferStatus.Draft], "StockTransfers.Create", Fit.Either, AuditActions.StockTransferSubmitted, null,
            (t, now) =>
            {
                t.Status = StockTransferStatus.Requested;
                t.SubmittedAt = now;
                t.SubmittedByUserId = userId;
            }, ct);

    public Task<StockTransfer?> ApproveAsync(Guid id, Guid userId, string? correlationId = null, CancellationToken ct = default) =>
        TransitionAsync(id, userId, correlationId, StockTransferStatus.Approved, [StockTransferStatus.Requested], "StockTransfers.Approve", Fit.Source, AuditActions.StockTransferApproved, null,
            (t, now) =>
            {
                t.Status = StockTransferStatus.Approved;
                t.ApprovedAt = now;
                t.ApprovedByUserId = userId;
            }, ct);

    public Task<StockTransfer?> RejectAsync(Guid id, string reason, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var text = Text(reason, "reason", required: true)!;
        return TransitionAsync(id, userId, correlationId, StockTransferStatus.Rejected, [StockTransferStatus.Requested], "StockTransfers.Approve", Fit.Source, AuditActions.StockTransferRejected, text,
            (t, now) =>
            {
                t.Status = StockTransferStatus.Rejected;
                t.RejectedAt = now;
                t.RejectedByUserId = userId;
                t.RejectionReason = text;
            }, ct);
    }

    public Task<StockTransfer?> CancelAsync(Guid id, string reason, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var text = Text(reason, "reason", required: true)!;
        return TransitionAsync(id, userId, correlationId, StockTransferStatus.Cancelled, [StockTransferStatus.Draft, StockTransferStatus.Requested, StockTransferStatus.Approved],
            "StockTransfers.Cancel", Fit.Either, AuditActions.StockTransferCancelled, text,
            (t, now) =>
            {
                t.Status = StockTransferStatus.Cancelled;
                t.CancelledAt = now;
                t.CancelledByUserId = userId;
                t.CancellationReason = text;
            }, ct);
    }

    public Task<StockTransfer?> CloseAsync(Guid id, string reason, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var text = Text(reason, "reason", required: true, MaxCloseReason)!;
        return TransitionAsync(id, userId, correlationId, StockTransferStatus.Closed, [StockTransferStatus.Approved, StockTransferStatus.PartiallyFulfilled],
            "StockTransfers.Approve", Fit.Source, AuditActions.StockTransferClosed, text,
            (t, now) =>
            {
                // FI-D26/FI-D27: closing is only meaningful once something shipped, and never while an allocation is still outstanding.
                if (!t.Lines.Any(l => l.DispatchedBaseQty > 0))
                {
                    throw new DomainValidationException("status", "Nothing has been dispatched on this transfer yet; cancel it instead of closing it.");
                }
                if (!t.Lines.Any(l => l.RequestedBaseQty - l.ClosedBaseQty - l.DispatchedBaseQty > 0))
                {
                    throw new DomainValidationException("status", "Nothing remains to be closed on this transfer.");
                }
                if (t.Lines.Any(l => l.AllocatedBaseQty > l.DispatchedBaseQty))
                {
                    throw new DomainValidationException("status", "Some allocated quantity has not been dispatched yet; dispatch or release the allocation before closing.");
                }
                foreach (var line in t.Lines) line.ClosedBaseQty = line.RequestedBaseQty - line.DispatchedBaseQty;
                t.Status = StockTransferStatus.Closed;
                t.ClosedAt = now;
                t.ClosedByUserId = userId;
                t.ClosureReason = text;
            }, ct);
    }

    // Header lock → reload → transition + fit check → stamp. No stock is touched, so no stock locks.
    private async Task<StockTransfer?> TransitionAsync(
        Guid id, Guid userId, string? correlationId, StockTransferStatus target, StockTransferStatus[] allowedFrom, string permissionCode, Fit fit,
        string auditAction, string? reason, Action<StockTransfer, DateTimeOffset> apply, CancellationToken ct)
    {
        await using var transaction = await StockLocks.BeginAsync(db, ct);

        var transfer = await LockAndLoadAsync(id, ct);
        if (transfer is null) return null;
        // Idempotent by state (D6): repeating the call on a transfer already in the target state changes nothing and
        // returns it as is (after the same fit check, so the answer reveals nothing to a caller who may not act).
        var alreadyDone = transfer.Status == target;
        if (!alreadyDone) EnsureStatus(transfer, allowedFrom);

        var (source, destination) = await LoadEndpointsAsync(transfer, ct);
        await EnsureFitAsync(permissionCode, fit, source, destination, ct);
        if (alreadyDone) return Finish(transfer);

        var before = transfer.Status;
        apply(transfer, DateTimeOffset.UtcNow);
        Audit(userId, correlationId, auditAction, transfer, before, Snapshot(transfer), reason);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Finish(transfer);
    }

    // ------------------------------------------------------------------ dispatch

    public async Task<StockTransfer?> DispatchAsync(Guid id, DispatchStockTransferInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        if (input.Lines is null || input.Lines.Count == 0)
        {
            throw new DomainValidationException("lines", "Enter the quantity to dispatch for every line.");
        }
        for (var i = 0; i < input.Lines.Count; i++)
        {
            if (input.Lines[i].Quantity < 0) throw new DomainValidationException($"lines[{i}].quantity", "Quantity cannot be negative.");
        }

        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, userId, Idempotency.StockTransferDispatch, input.IdempotencyKey, ct) is { } existingId)
        {
            if (existingId != id) throw new DomainValidationException("IdempotencyKey", "This key was already used for a different transfer.");
            return await LoadGraphAsync(id, ct)
                ?? throw new DomainValidationException("IdempotencyKey", "This request was already processed, but its result is no longer visible with your current branch access.");
        }

        var transfer = await LockAndLoadAsync(id, ct);
        if (transfer is null) return null;
        EnsureStatus(transfer, StockTransferStatus.Approved);

        var (source, destination) = await LoadEndpointsAsync(transfer, ct);
        await EnsureFitAsync("StockTransfers.Dispatch", Fit.Source, source, destination, ct);

        // Every line exactly once.
        var byLine = transfer.Lines.ToDictionary(l => l.Id);
        var requested = new Dictionary<Guid, (int Index, DispatchStockTransferLineInput Input)>();
        for (var i = 0; i < input.Lines.Count; i++)
        {
            var li = input.Lines[i];
            if (!byLine.ContainsKey(li.LineId)) throw new DomainValidationException($"lines[{i}].lineId", "This line does not belong to the transfer.");
            if (!requested.TryAdd(li.LineId, (i, li))) throw new DomainValidationException($"lines[{i}].lineId", "This line appears more than once.");
        }
        if (requested.Count != byLine.Count)
        {
            throw new DomainValidationException("lines", "Enter the quantity to dispatch for every line (0 for lines that are not shipped).");
        }

        await StockLocks.LockProductsKeyShareAsync(db, transfer.Lines.Select(l => l.ProductId), ct);

        // Dispatch snapshots, converted once with the stored factor.
        var (products, units) = await LoadProductsAndUnitsAsync(transfer.Lines.Select(l => l.ProductId), requested.Values.Select(r => r.Input.ProductUnitId), ct);
        var prepared = new List<(StockTransferLine Line, int Index, ProductUnit Unit, decimal Qty, decimal BaseQty)>();
        foreach (var line in transfer.Lines.OrderBy(l => l.LineNo))
        {
            var (index, li) = requested[line.Id];
            var unit = RequireUnit(units, li.ProductUnitId, line.ProductId, $"lines[{index}].productUnitId");
            var baseQty = ConvertEntered(unit, products[line.ProductId], li.Quantity, $"lines[{index}].quantity");
            if (baseQty > line.RequestedBaseQty)
            {
                throw new DomainValidationException($"lines[{index}].quantity",
                    $"Cannot dispatch more than was requested ({StockQuantity.Format(line.RequestedBaseQty)} {products[line.ProductId].BaseUnit.ShortCode}).");
            }
            prepared.Add((line, index, unit, li.Quantity, baseQty));
        }
        if (prepared.All(p => p.BaseQty == 0))
        {
            throw new DomainValidationException("lines", "Dispatch at least one line with a quantity above zero.");
        }

        await StockLocks.LockLocationsKeyShareAsync(db, [transfer.SourceLocationId, transfer.TransitLocationId], ct);
        await EnsureLocationActiveAsync(transfer.SourceLocationId, ct);

        var shipping = prepared.Where(p => p.BaseQty > 0).ToList();
        var items = await StockLocks.EnsureAndLockStockItemsAsync(db,
            shipping.SelectMany(p => new[] { (transfer.SourceLocationId, p.Line.ProductId), (transfer.TransitLocationId, p.Line.ProductId) }), ct);

        // Availability per product across all lines (mixed units) before anything moves; no negative stock.
        await StockAvailability.EnsureAsync(db, items,
            shipping.Select(p => new StockAvailability.Requirement(transfer.SourceLocationId, p.Line.ProductId, p.BaseQty)),
            shortage =>
            {
                var first = shipping.First(p => p.Line.ProductId == shortage.ProductId);
                var baseCode = products[shortage.ProductId].BaseUnit.ShortCode;
                return new DomainValidationException($"lines[{first.Index}].quantity",
                    $"Cannot dispatch {StockQuantity.Format(shortage.Requested)} {baseCode} of {products[shortage.ProductId].Name} — only {StockQuantity.Format(shortage.Available)} {baseCode} is in stock at the source.");
            }, ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var (line, index, unit, qty, baseQty) in prepared)
        {
            line.DispatchProductUnitId = unit.Id;
            line.DispatchUnitId = unit.UnitId;
            line.DispatchUnitCode = unit.Unit.ShortCode;
            line.DispatchFactor = unit.ConversionFactorToBase;
            line.DispatchedQty = qty;
            line.DispatchedBaseQty = baseQty;
            if (baseQty == 0) continue;

            var baseCode = products[line.ProductId].BaseUnit.ShortCode;
            var allocations = await StockPosting.MoveFifoAsync(db, items[(transfer.SourceLocationId, line.ProductId)], items[(transfer.TransitLocationId, line.ProductId)], baseQty,
                shortBy => new DomainValidationException($"lines[{index}].quantity", $"Batch stock is short by {StockQuantity.Format(shortBy)} {baseCode} at the source."), ct);

            foreach (var allocation in allocations)
            {
                line.Batches.Add(new StockTransferLineBatch
                {
                    LineId = line.Id,
                    PurchaseBatchId = allocation.Batch.Id,
                    DispatchedBaseQty = allocation.QtyBaseUnits,
                    UnitCost = allocation.UnitCost,
                });

                var entered = allocations.Count == 1 ? qty : (decimal?)null;
                db.StockMovements.Add(Movement(userId, transfer, line, StockMovementType.TransferOut, transfer.SourceLocationId, allocation.Batch.Id, -allocation.QtyBaseUnits, unit.Id, unit.Unit.ShortCode, unit.ConversionFactorToBase, entered, null));
                db.StockMovements.Add(Movement(userId, transfer, line, StockMovementType.TransferIn, transfer.TransitLocationId, allocation.Batch.Id, allocation.QtyBaseUnits, unit.Id, unit.Unit.ShortCode, unit.ConversionFactorToBase, entered, null));
            }
        }

        transfer.Status = StockTransferStatus.Dispatched;
        transfer.DispatchedAt = now;
        transfer.DispatchedByUserId = userId;

        Audit(userId, correlationId, AuditActions.StockTransferDispatched, transfer, StockTransferStatus.Approved, Snapshot(transfer));
        Idempotency.Record(db, userId, Idempotency.StockTransferDispatch, input.IdempotencyKey, transfer.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Finish(transfer);
    }

    // ------------------------------------------------------------------ receive

    public async Task<StockTransferReceiptResult?> ReceiveAsync(Guid id, ReceiveStockTransferInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var notes = Text(input.Notes, "notes", required: false);
        if (input.Lines is null || input.Lines.Count == 0)
        {
            throw new DomainValidationException("lines", "Enter the quantities received.");
        }

        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, userId, Idempotency.StockTransferReceipt, input.IdempotencyKey, ct) is { } existingReceiptId)
        {
            return await ReplayReceiptAsync(id, existingReceiptId, ct);
        }

        var transfer = await LockAndLoadAsync(id, ct);
        if (transfer is null) return null;
        EnsureStatus(transfer, StockTransferStatus.Dispatched, StockTransferStatus.PartiallyReceived);

        var (source, destination) = await LoadEndpointsAsync(transfer, ct);
        await EnsureFitAsync("StockTransfers.Receive", Fit.Destination, source, destination, ct);

        // Request shape; lines with nothing to record are skipped.
        var byLine = transfer.Lines.ToDictionary(l => l.Id);
        var seen = new HashSet<Guid>();
        var received = new List<(int Index, StockTransferLine Line, ReceiveStockTransferLineInput Input)>();
        for (var i = 0; i < input.Lines.Count; i++)
        {
            var li = input.Lines[i];
            if (!byLine.TryGetValue(li.LineId, out var line)) throw new DomainValidationException($"lines[{i}].lineId", "This line does not belong to the transfer.");
            if (!seen.Add(li.LineId)) throw new DomainValidationException($"lines[{i}].lineId", "This line appears more than once.");
            if (li.AcceptedQuantity < 0) throw new DomainValidationException($"lines[{i}].acceptedQuantity", "Quantity cannot be negative.");
            if (li.DamagedQuantity < 0) throw new DomainValidationException($"lines[{i}].damagedQuantity", "Quantity cannot be negative.");
            if (li.AcceptedQuantity == 0 && li.DamagedQuantity == 0) continue;
            received.Add((i, line, li));
        }
        if (received.Count == 0)
        {
            throw new DomainValidationException("lines", "Enter an accepted or damaged quantity for at least one line.");
        }

        await StockLocks.LockProductsKeyShareAsync(db, received.Select(r => r.Line.ProductId), ct);

        var (products, units) = await LoadProductsAndUnitsAsync(received.Select(r => r.Line.ProductId), received.Select(r => r.Input.ProductUnitId), ct);
        var prepared = new List<(int Index, StockTransferLine Line, ProductUnit Unit, decimal Accepted, decimal AcceptedBase, decimal Damaged, decimal DamagedBase)>();
        foreach (var (index, line, li) in received)
        {
            var unit = RequireUnit(units, li.ProductUnitId, line.ProductId, $"lines[{index}].productUnitId");
            var product = products[line.ProductId];
            var acceptedBase = ConvertEntered(unit, product, li.AcceptedQuantity, $"lines[{index}].acceptedQuantity");
            var damagedBase = ConvertEntered(unit, product, li.DamagedQuantity, $"lines[{index}].damagedQuantity");

            var outstanding = Outstanding(line);
            if (acceptedBase + damagedBase > outstanding)
            {
                throw new DomainValidationException($"lines[{index}].acceptedQuantity",
                    $"Only {StockQuantity.Format(outstanding)} {product.BaseUnit.ShortCode} of {product.Name} is still outstanding on this transfer.");
            }
            prepared.Add((index, line, unit, li.AcceptedQuantity, acceptedBase, li.DamagedQuantity, damagedBase));
        }

        await StockLocks.LockLocationsKeyShareAsync(db, [transfer.DestinationLocationId, transfer.TransitLocationId], ct);
        await EnsureLocationActiveAsync(transfer.DestinationLocationId, ct);

        var items = await StockLocks.EnsureAndLockStockItemsAsync(db,
            prepared.SelectMany(p => new[] { (transfer.DestinationLocationId, p.Line.ProductId), (transfer.TransitLocationId, p.Line.ProductId) }), ct);
        var batches = await LoadBatchesAsync(prepared.SelectMany(p => p.Line.Batches).Select(b => b.PurchaseBatchId), ct);

        var receipt = new StockTransferReceipt
        {
            Id = Guid.NewGuid(),
            TransferId = transfer.Id,
            Sequence = NextSequence(transfer),
            Kind = StockTransferReceiptKind.Receipt,
            At = DateTimeOffset.UtcNow,
            ByUserId = userId,
            Notes = notes,
        };

        foreach (var (index, line, unit, accepted, acceptedBase, damaged, damagedBase) in prepared)
        {
            var transitItem = items[(transfer.TransitLocationId, line.ProductId)];
            var destinationItem = items[(transfer.DestinationLocationId, line.ProductId)];

            if (acceptedBase > 0)
            {
                var draws = TakeOutstanding(line, batches, acceptedBase);
                foreach (var (row, batch, take) in draws)
                {
                    await StockPosting.MoveBatchAsync(db, transitItem, destinationItem, batch, take, ct);
                    row.ReceivedBaseQty += take;
                    var entered = draws.Count == 1 ? accepted : (decimal?)null;
                    db.StockMovements.Add(Movement(userId, transfer, line, StockMovementType.TransferOut, transfer.TransitLocationId, batch.Id, -take, unit.Id, unit.Unit.ShortCode, unit.ConversionFactorToBase, entered, null));
                    db.StockMovements.Add(Movement(userId, transfer, line, StockMovementType.TransferIn, transfer.DestinationLocationId, batch.Id, take, unit.Id, unit.Unit.ShortCode, unit.ConversionFactorToBase, entered, null));
                }
                line.ReceivedBaseQty += acceptedBase;
            }

            if (damagedBase > 0)
            {
                // Damaged goods never enter destination stock (O2): written off from Transit, traceable on the transfer.
                var draws = TakeOutstanding(line, batches, damagedBase);
                foreach (var (row, batch, take) in draws)
                {
                    await StockPosting.IssueFromBatchAsync(db, transitItem, batch, take, ct);
                    row.DamagedBaseQty += take;
                    var entered = draws.Count == 1 ? damaged : (decimal?)null;
                    db.StockMovements.Add(Movement(userId, transfer, line, StockMovementType.Adjustment, transfer.TransitLocationId, batch.Id, -take, unit.Id, unit.Unit.ShortCode, unit.ConversionFactorToBase, entered, AdjustmentType.Damage));
                }
                line.DamagedBaseQty += damagedBase;
            }

            receipt.Lines.Add(new StockTransferReceiptLine
            {
                ReceiptId = receipt.Id,
                TransferLineId = line.Id,
                Kind = StockTransferReceiptKind.Receipt,
                ProductId = line.ProductId,
                ProductUnitId = unit.Id,
                UnitId = unit.UnitId,
                UnitCode = unit.Unit.ShortCode,
                ConversionFactorToBase = unit.ConversionFactorToBase,
                AcceptedQty = accepted,
                AcceptedBaseQty = acceptedBase,
                DamagedQty = damaged,
                DamagedBaseQty = damagedBase,
            });
        }

        db.StockTransferReceipts.Add(receipt);
        var before = transfer.Status;
        CompleteIfSettled(transfer, userId, DateTimeOffset.UtcNow);

        Audit(userId, correlationId, AuditActions.StockTransferReceived, transfer, before, Snapshot(transfer, receiptId: receipt.Id), notes);
        Idempotency.Record(db, userId, Idempotency.StockTransferReceipt, input.IdempotencyKey, receipt.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new StockTransferReceiptResult(Finish(transfer), receipt.Id);
    }

    // ------------------------------------------------------------------ resolve

    public async Task<StockTransferReceiptResult?> ResolveAsync(Guid id, ResolveStockTransferInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var reason = Text(input.Reason, "reason", required: true)!;
        if (input.Lines is null || input.Lines.Count == 0)
        {
            throw new DomainValidationException("lines", "Choose an action for every line that is still outstanding.");
        }

        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, userId, Idempotency.StockTransferResolve, input.IdempotencyKey, ct) is { } existingReceiptId)
        {
            return await ReplayReceiptAsync(id, existingReceiptId, ct);
        }

        var transfer = await LockAndLoadAsync(id, ct);
        if (transfer is null) return null;
        EnsureStatus(transfer, StockTransferStatus.Dispatched, StockTransferStatus.PartiallyReceived);

        // Resolving is a company-level decision (O4): the permission must be held company-wide.
        if (!await StockLocationRules.HasCompanyLevelAsync(permissions, "StockTransfers.Resolve", ct))
        {
            throw new DomainValidationException(LocationField, "Resolving a transfer requires the permission company-wide.");
        }
        // A write-off or return touches both sides: an AssignedBranches member may resolve only when both endpoints are within its allocation.
        var (resolveSource, resolveDestination) = await LoadEndpointsAsync(transfer, ct);
        await EnsureAccessibleAsync(resolveSource, ct);
        await EnsureAccessibleAsync(resolveDestination, ct);

        var byLine = transfer.Lines.ToDictionary(l => l.Id);
        var actions = new Dictionary<Guid, StockTransferResolution>();
        for (var i = 0; i < input.Lines.Count; i++)
        {
            var li = input.Lines[i];
            if (!byLine.ContainsKey(li.LineId)) throw new DomainValidationException($"lines[{i}].lineId", "This line does not belong to the transfer.");
            if (!Enum.IsDefined(li.Action)) throw new DomainValidationException($"lines[{i}].action", "Choose write-off or return to source.");
            if (!actions.TryAdd(li.LineId, li.Action)) throw new DomainValidationException($"lines[{i}].lineId", "This line appears more than once.");
        }

        var open = transfer.Lines.Where(l => Outstanding(l) > 0).OrderBy(l => l.LineNo).ToList();
        if (open.Any(l => !actions.ContainsKey(l.Id)))
        {
            throw new DomainValidationException("lines", "Choose an action for every line that is still outstanding.");
        }

        await StockLocks.LockProductsKeyShareAsync(db, open.Select(l => l.ProductId), ct);

        await StockLocks.LockLocationsKeyShareAsync(db, [transfer.SourceLocationId, transfer.TransitLocationId], ct);
        if (open.Any(l => actions[l.Id] == StockTransferResolution.ReturnToSource))
        {
            await EnsureLocationActiveAsync(transfer.SourceLocationId, ct);
        }

        var keys = new List<(Guid, Guid)>();
        foreach (var line in open)
        {
            keys.Add((transfer.TransitLocationId, line.ProductId));
            if (actions[line.Id] == StockTransferResolution.ReturnToSource) keys.Add((transfer.SourceLocationId, line.ProductId));
        }
        var items = await StockLocks.EnsureAndLockStockItemsAsync(db, keys, ct);
        var batches = await LoadBatchesAsync(open.SelectMany(l => l.Batches).Select(b => b.PurchaseBatchId), ct);

        var receipt = new StockTransferReceipt
        {
            Id = Guid.NewGuid(),
            TransferId = transfer.Id,
            Sequence = NextSequence(transfer),
            Kind = StockTransferReceiptKind.Resolution,
            At = DateTimeOffset.UtcNow,
            ByUserId = userId,
            Notes = reason,
        };

        foreach (var line in open)
        {
            var outstanding = Outstanding(line);
            var action = actions[line.Id];
            var transitItem = items[(transfer.TransitLocationId, line.ProductId)];
            var unitId = line.DispatchProductUnitId!.Value;
            var unitCode = line.DispatchUnitCode!;
            var factor = line.DispatchFactor!.Value;

            foreach (var (row, batch, take) in TakeOutstanding(line, batches, outstanding))
            {
                if (action == StockTransferResolution.WriteOff)
                {
                    await StockPosting.IssueFromBatchAsync(db, transitItem, batch, take, ct);
                    row.WrittenOffBaseQty += take;
                    db.StockMovements.Add(Movement(userId, transfer, line, StockMovementType.Adjustment, transfer.TransitLocationId, batch.Id, -take, unitId, unitCode, factor, null, AdjustmentType.Loss));
                }
                else
                {
                    await StockPosting.MoveBatchAsync(db, transitItem, items[(transfer.SourceLocationId, line.ProductId)], batch, take, ct);
                    row.ReturnedBaseQty += take;
                    db.StockMovements.Add(Movement(userId, transfer, line, StockMovementType.TransferOut, transfer.TransitLocationId, batch.Id, -take, unitId, unitCode, factor, null, null));
                    db.StockMovements.Add(Movement(userId, transfer, line, StockMovementType.TransferIn, transfer.SourceLocationId, batch.Id, take, unitId, unitCode, factor, null, null));
                }
            }

            if (action == StockTransferResolution.WriteOff) line.WrittenOffBaseQty += outstanding;
            else line.ReturnedBaseQty += outstanding;

            // Resolution lines carry the line's dispatch unit snapshot; only written-off / returned are non-zero.
            receipt.Lines.Add(new StockTransferReceiptLine
            {
                ReceiptId = receipt.Id,
                TransferLineId = line.Id,
                Kind = StockTransferReceiptKind.Resolution,
                ProductId = line.ProductId,
                ProductUnitId = unitId,
                UnitId = line.DispatchUnitId!.Value,
                UnitCode = unitCode,
                ConversionFactorToBase = factor,
                WrittenOffBaseQty = action == StockTransferResolution.WriteOff ? outstanding : 0,
                ReturnedBaseQty = action == StockTransferResolution.ReturnToSource ? outstanding : 0,
            });
        }

        db.StockTransferReceipts.Add(receipt);
        var before = transfer.Status;
        CompleteIfSettled(transfer, userId, DateTimeOffset.UtcNow);

        Audit(userId, correlationId, AuditActions.StockTransferResolved, transfer, before, Snapshot(transfer, receiptId: receipt.Id), reason);
        Idempotency.Record(db, userId, Idempotency.StockTransferResolve, input.IdempotencyKey, receipt.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new StockTransferReceiptResult(Finish(transfer), receipt.Id);
    }

    // ------------------------------------------------------------------ shared helpers

    private static decimal Outstanding(StockTransferLine l) =>
        l.DispatchedBaseQty - l.ReceivedBaseQty - l.DamagedBaseQty - l.WrittenOffBaseQty - l.ReturnedBaseQty;

    private static decimal Outstanding(StockTransferLineBatch b) =>
        b.DispatchedBaseQty - b.ReceivedBaseQty - b.DamagedBaseQty - b.WrittenOffBaseQty - b.ReturnedBaseQty;

    // Draws from the line's own batch rows outstanding in dispatch (FIFO) order - never from the shared Transit balance, so
    // concurrent transfers of the same product cannot swap batches. Plans only; the caller bumps the counters after posting.
    private static List<(StockTransferLineBatch Row, PurchaseBatch Batch, decimal Qty)> TakeOutstanding(
        StockTransferLine line, IReadOnlyDictionary<Guid, PurchaseBatch> batches, decimal baseQty)
    {
        var draws = new List<(StockTransferLineBatch, PurchaseBatch, decimal)>();
        var remaining = baseQty;
        foreach (var row in line.Batches.OrderBy(b => batches[b.PurchaseBatchId].CreatedAt).ThenBy(b => batches[b.PurchaseBatchId].BatchSequence))
        {
            if (remaining <= 0) break;
            var available = Outstanding(row);
            if (available <= 0) continue;
            var take = Math.Min(available, remaining);
            draws.Add((row, batches[row.PurchaseBatchId], take));
            remaining -= take;
        }
        if (remaining > 0)
        {
            throw new InvalidOperationException($"Transfer line {line.Id} has {remaining} less in its batches than its counters claim.");
        }
        return draws;
    }

    private static int NextSequence(StockTransfer transfer) => transfer.Receipts.Count == 0 ? 1 : transfer.Receipts.Max(r => r.Sequence) + 1;

    private void CompleteIfSettled(StockTransfer transfer, Guid userId, DateTimeOffset now)
    {
        if (transfer.Lines.All(l => Outstanding(l) == 0))
        {
            transfer.Status = StockTransferStatus.Completed;
            transfer.CompletedAt = now;
            transfer.CompletedByUserId = userId;
        }
        else
        {
            transfer.Status = StockTransferStatus.PartiallyReceived;
        }
    }

    private async Task<StockTransferReceiptResult> ReplayReceiptAsync(Guid transferId, Guid receiptId, CancellationToken ct)
    {
        var receipt = await db.StockTransferReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.Id == receiptId, ct);
        if (receipt is null || receipt.TransferId != transferId)
        {
            throw new DomainValidationException("IdempotencyKey", "This key was already used for a different transfer.");
        }
        var transfer = await LoadGraphAsync(transferId, ct)
            ?? throw new DomainValidationException("IdempotencyKey", "This request was already processed, but its result is no longer visible with your current branch access.");
        return new StockTransferReceiptResult(transfer, receiptId);
    }

    private IQueryable<StockTransfer> Graph() => db.StockTransfers
        .Include(t => t.Lines).ThenInclude(l => l.Batches)
        .Include(t => t.Receipts).ThenInclude(r => r.Lines)
        .AsSplitQuery();

    private async Task<StockTransfer?> LoadGraphAsync(Guid id, CancellationToken ct)
    {
        var transfer = await Graph().SingleOrDefaultAsync(t => t.Id == id, ct);
        return transfer is null ? null : Finish(transfer);
    }

    // Header FOR UPDATE, then the tracked graph (the Branch filter hides transfers of other branches: null = 404).
    private async Task<StockTransfer?> LockAndLoadAsync(Guid id, CancellationToken ct)
    {
        if (!await StockLocks.LockTransferAsync(db, id, ct)) return null;
        var transfer = await Graph().SingleOrDefaultAsync(t => t.Id == id, ct);
        return transfer is null ? null : Finish(transfer);
    }

    private static StockTransfer Finish(StockTransfer transfer)
    {
        transfer.Lines.Sort((a, b) => a.LineNo.CompareTo(b.LineNo));
        transfer.Receipts.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        return transfer;
    }

    private static void EnsureStatus(StockTransfer transfer, params StockTransferStatus[] allowed)
    {
        if (!allowed.Contains(transfer.Status))
        {
            throw new DomainValidationException("status", $"This transfer is {transfer.Status}; the action needs it to be {string.Join(" or ", allowed)}.");
        }
    }

    private static string? Text(string? value, string field, bool required, int max = MaxText)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text is null && required) throw new DomainValidationException(field, "Enter a reason.");
        if (text is { } t && t.Length > max) throw new DomainValidationException(field, $"Use at most {max} characters.");
        return text;
    }

    private static void RequireLines(IReadOnlyList<StockTransferLineInput>? lines)
    {
        if (lines is null || lines.Count == 0) throw new DomainValidationException("lines", "Add at least one line.");
    }

    private static void RequireDifferentEndpoints(Guid source, Guid destination)
    {
        if (source == destination) throw new DomainValidationException("destinationLocationId", "Source and destination must be different locations.");
    }

    private async Task<StockLocation> GetTransitAsync(CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        return await db.StockLocations.AsNoTracking().SingleOrDefaultAsync(l => l.CompanyId == companyId && l.LocationType == StockLocationType.Transit, ct)
            ?? throw new InvalidOperationException("The company has no Transit location.");
    }

    // Endpoints: active, non-Transit, in this company (Company filter + CompanyId predicate); the effective branch is the
    // location's own, else an assigned vehicle's AssignedBranchId. An unknown, foreign, inactive or Transit id shares one message.
    private async Task<(StockLocation Source, StockLocation Destination, Guid? SourceBranchId, Guid? DestinationBranchId)> ResolveEndpointsAsync(
        Guid sourceId, Guid destinationId, CancellationToken ct)
    {
        var (source, sourceBranch) = await ResolveEndpointAsync(sourceId, "sourceLocationId", ct);
        var (destination, destinationBranch) = await ResolveEndpointAsync(destinationId, "destinationLocationId", ct);
        return (source, destination, sourceBranch, destinationBranch);
    }

    private async Task<(StockLocation Location, Guid? EffectiveBranchId)> ResolveEndpointAsync(Guid id, string field, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var location = await db.StockLocations.AsNoTracking().SingleOrDefaultAsync(l => l.CompanyId == companyId && l.Id == id, ct);
        if (location is null || !location.IsActive || location.LocationType == StockLocationType.Transit)
        {
            throw new DomainValidationException(field, InvalidLocation);
        }

        var effectiveBranch = location.BranchId;
        if (location.LocationType == StockLocationType.Vehicle)
        {
            var vehicle = await db.Vehicles.AsNoTracking()
                .Where(v => v.CompanyId == companyId && v.StockLocationId == location.Id)
                .Select(v => new { v.AssignedBranchId, v.IsActive })
                .SingleOrDefaultAsync(ct);
            if (vehicle is null || !vehicle.IsActive) throw new DomainValidationException(field, InvalidLocation);
            effectiveBranch ??= vehicle.AssignedBranchId;
        }
        return (location, effectiveBranch);
    }

    private async Task<(StockLocation Source, StockLocation Destination)> LoadEndpointsAsync(StockTransfer transfer, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var ids = new[] { transfer.SourceLocationId, transfer.DestinationLocationId };
        var found = await db.StockLocations.AsNoTracking().Where(l => l.CompanyId == companyId && ids.Contains(l.Id)).ToListAsync(ct);
        return (found.Single(l => l.Id == transfer.SourceLocationId), found.Single(l => l.Id == transfer.DestinationLocationId));
    }

    // Re-read under the location KEY SHARE lock: a deactivation committed in between is seen here.
    private async Task EnsureLocationActiveAsync(Guid locationId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var active = await db.StockLocations.AsNoTracking().Where(l => l.CompanyId == companyId && l.Id == locationId).Select(l => (bool?)l.IsActive).SingleOrDefaultAsync(ct);
        if (active != true) throw new DomainValidationException(LocationField, InvalidLocation);
    }

    // D7: the permission held company-wide opens the action; otherwise the working branch must fit the endpoint(s) the action concerns.
    private async Task EnsureFitAsync(string permissionCode, Fit rule, StockLocation source, StockLocation destination, CancellationToken ct)
    {
        if (await StockLocationRules.HasCompanyLevelAsync(permissions, permissionCode, ct))
        {
            // A company-level grant opens company-level endpoints and every branch the membership may access — never a branch outside
            // its allocation (arch 13.2: an AssignedBranches member with a company-scope role is still limited to its branches).
            switch (rule)
            {
                case Fit.Source: await EnsureAccessibleAsync(source, ct); break;
                case Fit.Destination: await EnsureAccessibleAsync(destination, ct); break;
                default:
                    if (!await IsAccessibleAsync(source, ct)) await EnsureAccessibleAsync(destination, ct);
                    break;
            }
            return;
        }

        var branch = await BranchRules.ResolveWorkingBranchAsync(db, ct);
        switch (rule)
        {
            case Fit.Source:
                await StockLocationRules.EnsureFitsAsync(db, source, branch.Id, false, ct);
                break;
            case Fit.Destination:
                await StockLocationRules.EnsureFitsAsync(db, destination, branch.Id, false, ct);
                break;
            default:
                if (!await StockLocationRules.FitsAsync(db, source, branch.Id, false, ct))
                {
                    await StockLocationRules.EnsureFitsAsync(db, destination, branch.Id, false, ct);
                }
                break;
        }
    }

    // Effective owner = location.BranchId ?? the vehicle's assigned branch; company-level (no owner) is accessible to any member.
    private async Task<bool> IsAccessibleAsync(StockLocation location, CancellationToken ct)
    {
        if (db.BranchContext.AllowedBranchIds is null) return true;
        var owner = location.BranchId;
        if (owner is null && location.LocationType == StockLocationType.Vehicle)
        {
            var companyId = db.CompanyContext.RequireCompanyId();
            owner = await db.Vehicles.AsNoTracking().Where(v => v.CompanyId == companyId && v.StockLocationId == location.Id)
                .Select(v => v.AssignedBranchId).SingleOrDefaultAsync(ct);
        }
        return owner is not { } b || db.BranchContext.CanAccess(b);
    }

    private async Task EnsureAccessibleAsync(StockLocation location, CancellationToken ct)
    {
        if (!await IsAccessibleAsync(location, ct)) throw new DomainValidationException(LocationField, InvalidLocation);
    }

    private async Task<(Dictionary<Guid, Product> Products, Dictionary<Guid, ProductUnit> Units)> LoadProductsAndUnitsAsync(
        IEnumerable<Guid> productIds, IEnumerable<Guid> productUnitIds, CancellationToken ct)
    {
        var pids = productIds.Distinct().ToList();
        var uids = productUnitIds.Distinct().ToList();
        var products = await db.Products.Include(p => p.BaseUnit).Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var units = await db.ProductUnits.Include(u => u.Unit).Where(u => uids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        return (products, units);
    }

    private async Task<Dictionary<Guid, PurchaseBatch>> LoadBatchesAsync(IEnumerable<Guid> batchIds, CancellationToken ct)
    {
        var ids = batchIds.Distinct().ToList();
        return await db.PurchaseBatches.Where(b => ids.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
    }

    private static ProductUnit RequireUnit(IReadOnlyDictionary<Guid, ProductUnit> units, Guid productUnitId, Guid productId, string field)
    {
        if (!units.TryGetValue(productUnitId, out var unit)) throw new DomainValidationException(field, "Select a valid unit.");
        if (unit.ProductId != productId) throw new DomainValidationException(field, "This unit does not belong to this product.");
        if (!unit.IsActive) throw new DomainValidationException(field, $"{unit.Label ?? unit.Unit.Name} is no longer active.");
        return unit;
    }

    // Entered decimals -> exact base conversion storable at 3 dp (never rounded) -> within the base unit's own decimals.
    private static decimal ConvertEntered(ProductUnit unit, Product product, decimal enteredQty, string field)
    {
        StockQuantity.EnsureEnteredDecimals(enteredQty, unit.Unit.DecimalPlaces, unit.Label ?? unit.Unit.Name, field);
        var baseQty = enteredQty * unit.ConversionFactorToBase;
        StockQuantity.EnsureStorable(baseQty, field);
        StockQuantity.EnsureWithinBaseDecimals(enteredQty, unit.Unit.ShortCode, baseQty, product.BaseUnit, field);
        return baseQty;
    }

    private async Task<List<StockTransferLine>> BuildRequestLinesAsync(Guid transferId, IReadOnlyList<StockTransferLineInput> inputs, CancellationToken ct)
    {
        var (products, units) = await LoadProductsAndUnitsAsync(inputs.Select(l => l.ProductId), inputs.Select(l => l.ProductUnitId), ct);
        var lines = new List<StockTransferLine>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var li = inputs[i];
            if (!products.TryGetValue(li.ProductId, out var product) || !product.IsActive)
            {
                throw new DomainValidationException($"lines[{i}].productId", "Select a valid, active product.");
            }
            var unit = RequireUnit(units, li.ProductUnitId, product.Id, $"lines[{i}].productUnitId");
            if (li.Quantity <= 0) throw new DomainValidationException($"lines[{i}].quantity", "Quantity must be greater than zero.");
            var baseQty = ConvertEntered(unit, product, li.Quantity, $"lines[{i}].quantity");

            lines.Add(new StockTransferLine
            {
                Id = Guid.NewGuid(),
                TransferId = transferId,
                LineNo = i + 1,
                ProductId = product.Id,
                ProductUnitId = unit.Id,
                UnitId = unit.UnitId,
                UnitCode = unit.Unit.ShortCode,
                ConversionFactorToBase = unit.ConversionFactorToBase,
                RequestedQty = li.Quantity,
                RequestedBaseQty = baseQty,
            });
        }
        return lines;
    }

    private static StockMovement Movement(
        Guid userId, StockTransfer transfer, StockTransferLine line, StockMovementType type, Guid locationId, Guid batchId, decimal signedQty,
        Guid productUnitId, string unitCode, decimal factor, decimal? enteredQty, AdjustmentType? reason) =>
        MovementSnapshot.Apply(new StockMovement
        {
            Id = Guid.NewGuid(),
            ProductId = line.ProductId,
            StockLocationId = locationId,
            PurchaseBatchId = batchId,
            Type = type,
            QuantityBaseUnits = signedQty,
            ReferenceType = "StockTransfer",
            ReferenceId = transfer.Id,
            ReferenceNo = transfer.TransferNo,
            UserId = userId,
            Notes = reason?.ToString(),
            ReasonCode = reason,
        }, productUnitId, unitCode, factor, enteredQty);

    private void Audit(Guid userId, string? correlationId, string action, StockTransfer transfer, StockTransferStatus? before, object after, string? reason = null) =>
        AuditLog.Append(db, userId, correlationId, action, AuditTargets.StockTransfer, transfer.Id, reason,
            before is null ? null : new { status = before.ToString() }, after);

    private static object Snapshot(StockTransfer t, StockTransferStatus? statusOverride = null, Guid? receiptId = null) => new
    {
        transferNo = t.TransferNo,
        status = (statusOverride ?? t.Status).ToString(),
        sourceLocationId = t.SourceLocationId,
        destinationLocationId = t.DestinationLocationId,
        receiptId,
    };
}
