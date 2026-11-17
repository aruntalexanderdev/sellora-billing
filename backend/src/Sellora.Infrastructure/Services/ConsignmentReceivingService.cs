using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Phase 05 (FI-D35/D36) receiving at the destination. Verification records accepted / damaged / rejected / missing per consignment line and moves
/// NO stock; posting moves the ACCEPTED quantity Transit → destination over the line's own dispatched batch rows (ConsignmentLineId set), exactly once.
/// Damaged, rejected and missing quantities stay in Transit (not written to the batch DamagedBaseQty) until Phase 06 dispositions them.
/// Lock order: idempotency advisory lock → shipment header FOR UPDATE → consignment FOR UPDATE → transfer headers FOR UPDATE (id order) → products KEY SHARE →
/// locations KEY SHARE {destination, Transit} → StockItems ensure + FOR UPDATE. The shipment is locked first (as dispatch does) because its status is derived
/// from all of its consignments. The Branch filter on shipments is bypassed on purpose: the caller is the destination, not the source branch; visibility is the
/// destination fit, and a consignment the caller may not receive is a 404.
/// </summary>
public class ConsignmentReceivingService(SelloraDbContext db, IEffectivePermissionService? permissions = null) : IConsignmentReceivingService
{
    private const int MaxNotes = 500;
    private const string DestinationField = "destinationLocationId";
    private const string InvalidLocation = "Select a valid, active stock location.";

    // ------------------------------------------------------------------ verify

    public async Task<ConsignmentReceipt?> RecordAsync(Guid consignmentId, RecordReceiptInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        if (notes is { Length: > MaxNotes }) throw new DomainValidationException("notes", $"Use at most {MaxNotes} characters.");
        if (input.Lines is null || input.Lines.Count == 0) throw new DomainValidationException("lines", "Enter the quantities received for at least one line.");
        for (var i = 0; i < input.Lines.Count; i++)
        {
            var li = input.Lines[i];
            if (li.AcceptedQuantity < 0) throw new DomainValidationException($"lines[{i}].acceptedQuantity", "Quantity cannot be negative.");
            if (li.DamagedQuantity < 0) throw new DomainValidationException($"lines[{i}].damagedQuantity", "Quantity cannot be negative.");
            if (li.RejectedQuantity < 0) throw new DomainValidationException($"lines[{i}].rejectedQuantity", "Quantity cannot be negative.");
            if (li.MissingQuantity < 0) throw new DomainValidationException($"lines[{i}].missingQuantity", "Quantity cannot be negative.");
        }

        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, userId, Idempotency.ConsignmentReceipt, input.IdempotencyKey, ct) is { } existingReceiptId)
        {
            return await ReplayAsync(consignmentId, existingReceiptId, ct);
        }

        var loaded = await LockAndLoadAsync(consignmentId, ct);
        if (loaded is null) return null;
        var (shipment, consignment, destination) = loaded.Value;
        if (!await CanReceiveAsync(destination, ct)) return null;
        EnsureReceivable(consignment);

        var byLine = consignment.Lines.Where(l => l.Status == ConsignmentLineStatus.Active && l.DispatchedBaseQty > 0).ToDictionary(l => l.Id);
        var (products, units) = await LoadProductsAndUnitsAsync(byLine.Values.Select(l => l.ProductId), byLine.Values.Select(l => l.ProductUnitId), ct);

        var seen = new HashSet<Guid>();
        var prepared = new List<(ConsignmentLine Line, ReceiptLineInput Input, decimal Accepted, decimal Damaged, decimal Rejected, decimal Missing)>();
        for (var i = 0; i < input.Lines.Count; i++)
        {
            var li = input.Lines[i];
            if (!byLine.TryGetValue(li.ConsignmentLineId, out var line))
            {
                throw new DomainValidationException($"lines[{i}].consignmentLineId", "This is not a dispatched line of the consignment.");
            }
            if (!seen.Add(line.Id)) throw new DomainValidationException($"lines[{i}].consignmentLineId", "This line appears more than once.");

            var unit = units[line.ProductUnitId];
            var product = products[line.ProductId];
            var accepted = ConvertEntered(unit, product, li.AcceptedQuantity, $"lines[{i}].acceptedQuantity");
            var damaged = ConvertEntered(unit, product, li.DamagedQuantity, $"lines[{i}].damagedQuantity");
            var rejected = ConvertEntered(unit, product, li.RejectedQuantity, $"lines[{i}].rejectedQuantity");
            var missing = ConvertEntered(unit, product, li.MissingQuantity, $"lines[{i}].missingQuantity");
            var total = accepted + damaged + rejected + missing;
            if (total == 0) continue;

            var open = line.DispatchedBaseQty - Accounted(line);
            if (total > open)
            {
                throw new DomainValidationException($"lines[{i}].acceptedQuantity",
                    $"Only {StockQuantity.Format(open)} {product.BaseUnit.ShortCode} of {product.Name} is still to be accounted for ({StockQuantity.Format(line.DispatchedBaseQty)} dispatched).");
            }
            prepared.Add((line, li, accepted, damaged, rejected, missing));
        }
        if (prepared.Count == 0) throw new DomainValidationException("lines", "Enter an accepted, damaged, rejected or missing quantity for at least one line.");

        var existing = await db.ConsignmentReceipts.AsNoTracking().Where(r => r.ConsignmentId == consignment.Id).Select(r => new { r.Sequence, r.Status }).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var receipt = new ConsignmentReceipt
        {
            CompanyId = consignment.CompanyId,
            Id = Guid.NewGuid(),
            ConsignmentId = consignment.Id,
            Sequence = existing.Count == 0 ? 1 : existing.Max(r => r.Sequence) + 1,
            Status = ConsignmentReceiptStatus.Verified,
            VerifiedAt = now,
            VerifiedByUserId = userId,
            Notes = notes,
        };

        foreach (var (line, li, accepted, damaged, rejected, missing) in prepared)
        {
            receipt.Lines.Add(new ConsignmentReceiptLine
            {
                CompanyId = consignment.CompanyId,
                ReceiptId = receipt.Id,
                ConsignmentLineId = line.Id,
                ProductId = line.ProductId,
                ProductUnitId = line.ProductUnitId,
                UnitId = line.UnitId,
                UnitCode = line.UnitCode,
                ConversionFactorToBase = line.ConversionFactorToBase,
                AcceptedEnteredQty = li.AcceptedQuantity,
                AcceptedBaseQty = accepted,
                DamagedEnteredQty = li.DamagedQuantity,
                DamagedBaseQty = damaged,
                RejectedEnteredQty = li.RejectedQuantity,
                RejectedBaseQty = rejected,
                MissingEnteredQty = li.MissingQuantity,
                MissingBaseQty = missing,
            });
            line.AcceptedBaseQty += accepted;
            line.DamagedBaseQty += damaged;
            line.RejectedBaseQty += rejected;
            line.MissingBaseQty += missing;
            line.UpdatedAt = now;
        }
        db.ConsignmentReceipts.Add(receipt);

        var before = new { consignmentStatus = consignment.Status.ToString(), shipmentStatus = shipment.Status.ToString() };
        TransitStatus.Recompute(shipment, consignment, [.. existing.Select(r => r.Status), ConsignmentReceiptStatus.Verified], userId, now);
        Audit(userId, correlationId, AuditActions.ConsignmentReceiptVerified, consignment, notes, before, Snapshot(shipment, consignment, receipt));

        Idempotency.Record(db, userId, Idempotency.ConsignmentReceipt, input.IdempotencyKey, receipt.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return receipt;
    }

    // ------------------------------------------------------------------ post

    public async Task<ConsignmentReceipt?> PostAsync(Guid consignmentId, Guid receiptId, PostReceiptInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, userId, Idempotency.ConsignmentReceiptPost, input.IdempotencyKey, ct) is { } existingReceiptId)
        {
            if (existingReceiptId != receiptId) throw new DomainValidationException("IdempotencyKey", "This key was already used for a different receipt.");
            return await ReplayAsync(consignmentId, receiptId, ct);
        }

        var loaded = await LockAndLoadAsync(consignmentId, ct);
        if (loaded is null) return null;
        var (shipment, consignment, destination) = loaded.Value;
        if (!await CanReceiveAsync(destination, ct)) return null;

        var receipt = await db.ConsignmentReceipts.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == receiptId && r.ConsignmentId == consignmentId, ct);
        if (receipt is null) return null;
        if (receipt.Status == ConsignmentReceiptStatus.Posted) return receipt; // exactly once: a second post is a no-op (idempotent by state)
        EnsureReceivable(consignment);

        var companyId = db.CompanyContext.RequireCompanyId();
        var transit = await db.StockLocations.AsNoTracking().SingleOrDefaultAsync(l => l.CompanyId == companyId && l.LocationType == StockLocationType.Transit, ct)
            ?? throw new InvalidOperationException("The company has no Transit location.");

        var accepting = receipt.Lines.Where(l => l.AcceptedBaseQty > 0).OrderBy(l => l.ProductId).ThenBy(l => l.ConsignmentLineId).ToList();
        var lineById = consignment.Lines.ToDictionary(l => l.Id);
        var now = DateTimeOffset.UtcNow;

        if (accepting.Count > 0)
        {
            var transferIds = accepting.Select(l => lineById[l.ConsignmentLineId].StockTransferId).Distinct().Order().ToList();
            foreach (var tid in transferIds) await StockLocks.LockTransferAsync(db, tid, ct);
            var transfers = await db.StockTransfers.IgnoreQueryFilters([BranchQueryFilter.Name]).Include(t => t.Lines)
                .Where(t => transferIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

            var productIds = accepting.Select(l => l.ProductId).Distinct().ToList();
            await StockLocks.LockProductsKeyShareAsync(db, productIds, ct);
            await StockLocks.LockLocationsKeyShareAsync(db, [destination.Id, transit.Id], ct);
            var active = await db.StockLocations.AsNoTracking().Where(l => l.CompanyId == companyId && l.Id == destination.Id).Select(l => (bool?)l.IsActive).SingleOrDefaultAsync(ct);
            if (active != true) throw new DomainValidationException(DestinationField, InvalidLocation);
            var items = await StockLocks.EnsureAndLockStockItemsAsync(db, productIds.SelectMany(p => new[] { (destination.Id, p), (transit.Id, p) }), ct);

            var consignmentLineIds = accepting.Select(l => l.ConsignmentLineId).ToList();
            var rows = await db.StockTransferLineBatches.Where(b => b.ConsignmentLineId != null && consignmentLineIds.Contains(b.ConsignmentLineId.Value)).ToListAsync(ct);
            var batchIds = rows.Select(r => r.PurchaseBatchId).Distinct().ToList();
            var batches = await db.PurchaseBatches.Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);

            foreach (var rl in accepting)
            {
                var line = lineById[rl.ConsignmentLineId];
                var transfer = transfers[line.StockTransferId];
                var requestLine = transfer.Lines.Single(l => l.Id == line.StockTransferLineId);
                var transitItem = items[(transit.Id, rl.ProductId)];
                var destinationItem = items[(destination.Id, rl.ProductId)];

                // Only this consignment line's own batch rows, oldest batch first - never the shared Transit balance.
                var draws = Take(rows.Where(r => r.ConsignmentLineId == line.Id), batches, rl.AcceptedBaseQty, line.Id);
                foreach (var (row, batch, take) in draws)
                {
                    await StockPosting.MoveBatchAsync(db, transitItem, destinationItem, batch, take, ct);
                    row.ReceivedBaseQty += take;
                    var entered = draws.Count == 1 ? rl.AcceptedEnteredQty : (decimal?)null;
                    db.StockMovements.Add(Ledger(userId, consignment, receipt, transfer, requestLine, rl, StockMovementType.TransferOut, transit.Id, batch.Id, -take, entered));
                    db.StockMovements.Add(Ledger(userId, consignment, receipt, transfer, requestLine, rl, StockMovementType.TransferIn, destination.Id, batch.Id, take, entered));
                }
                line.PostedBaseQty += rl.AcceptedBaseQty;
                line.UpdatedAt = now;
                requestLine.ReceivedBaseQty += rl.AcceptedBaseQty;
            }
        }

        receipt.Status = ConsignmentReceiptStatus.Posted;
        receipt.PostedAt = now;
        receipt.PostedByUserId = userId;

        var statuses = await db.ConsignmentReceipts.AsNoTracking().Where(r => r.ConsignmentId == consignment.Id && r.Id != receipt.Id).Select(r => r.Status).ToListAsync(ct);
        var before = new { consignmentStatus = consignment.Status.ToString(), shipmentStatus = shipment.Status.ToString() };
        TransitStatus.Recompute(shipment, consignment, [.. statuses, ConsignmentReceiptStatus.Posted], userId, now);
        Audit(userId, correlationId, AuditActions.ConsignmentReceiptPosted, consignment, null, before, Snapshot(shipment, consignment, receipt));

        Idempotency.Record(db, userId, Idempotency.ConsignmentReceiptPost, input.IdempotencyKey, receipt.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return receipt;
    }

    // ------------------------------------------------------------------ helpers

    private static decimal Accounted(ConsignmentLine l) => l.AcceptedBaseQty + l.DamagedBaseQty + l.RejectedBaseQty + l.MissingBaseQty;

    private static decimal Outstanding(StockTransferLineBatch b) =>
        b.DispatchedBaseQty - b.ReceivedBaseQty - b.DamagedBaseQty - b.WrittenOffBaseQty - b.ReturnedBaseQty;

    private static List<(StockTransferLineBatch Row, PurchaseBatch Batch, decimal Qty)> Take(
        IEnumerable<StockTransferLineBatch> rows, IReadOnlyDictionary<Guid, PurchaseBatch> batches, decimal baseQty, Guid consignmentLineId)
    {
        var draws = new List<(StockTransferLineBatch, PurchaseBatch, decimal)>();
        var remaining = baseQty;
        foreach (var row in rows.OrderBy(b => batches[b.PurchaseBatchId].CreatedAt).ThenBy(b => batches[b.PurchaseBatchId].BatchSequence))
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
            throw new InvalidOperationException($"Consignment line {consignmentLineId} has {remaining} less in its dispatched batches than its counters claim.");
        }
        return draws;
    }

    private static void EnsureReceivable(Consignment consignment)
    {
        if (consignment.Status is not (ConsignmentStatus.InTransit or ConsignmentStatus.PartiallyReceived))
        {
            throw new DomainValidationException("status", $"This consignment is {consignment.Status}; only a consignment in transit can be received.");
        }
    }

    // Shipment header FOR UPDATE, consignment FOR UPDATE, then the tracked graph. Null = unknown / other company.
    private async Task<(Shipment Shipment, Consignment Consignment, StockLocation Destination)?> LockAndLoadAsync(Guid consignmentId, CancellationToken ct)
    {
        var shipmentId = await db.Consignments.AsNoTracking().Where(c => c.Id == consignmentId).Select(c => (Guid?)c.ShipmentId).SingleOrDefaultAsync(ct);
        if (shipmentId is null) return null;
        if (!await StockLocks.LockShipmentAsync(db, shipmentId.Value, ct)) return null;
        if (!await StockLocks.LockConsignmentAsync(db, consignmentId, ct)) return null;

        var shipment = await db.Shipments.IgnoreQueryFilters([BranchQueryFilter.Name])
            .Include(s => s.Consignments).ThenInclude(c => c.Lines).AsSplitQuery()
            .SingleOrDefaultAsync(s => s.Id == shipmentId.Value, ct);
        var consignment = shipment?.Consignments.SingleOrDefault(c => c.Id == consignmentId);
        if (shipment is null || consignment is null || consignment.Status == ConsignmentStatus.Cancelled || shipment.Status == ShipmentStatus.Cancelled) return null;

        var companyId = db.CompanyContext.RequireCompanyId();
        var destination = await db.StockLocations.AsNoTracking().SingleAsync(l => l.CompanyId == companyId && l.Id == consignment.DestinationLocationId, ct);
        return (shipment, consignment, destination);
    }

    private async Task<ConsignmentReceipt?> ReplayAsync(Guid consignmentId, Guid receiptId, CancellationToken ct)
    {
        var receipt = await db.ConsignmentReceipts.AsNoTracking().Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == receiptId, ct);
        if (receipt is null || receipt.ConsignmentId != consignmentId)
        {
            throw new DomainValidationException("IdempotencyKey", "This key was already used for a different consignment.");
        }

        var companyId = db.CompanyContext.RequireCompanyId();
        var destinationId = await db.Consignments.AsNoTracking().Where(c => c.Id == consignmentId).Select(c => c.DestinationLocationId).SingleAsync(ct);
        var destination = await db.StockLocations.AsNoTracking().SingleAsync(l => l.CompanyId == companyId && l.Id == destinationId, ct);
        if (!await CanReceiveAsync(destination, ct))
        {
            throw new DomainValidationException("IdempotencyKey", "This request was already processed, but its result is no longer visible with your current branch access.");
        }
        return receipt;
    }

    private static decimal ConvertEntered(ProductUnit unit, Product product, decimal enteredQty, string field)
    {
        StockQuantity.EnsureEnteredDecimals(enteredQty, unit.Unit.DecimalPlaces, unit.Label ?? unit.Unit.Name, field);
        var baseQty = enteredQty * unit.ConversionFactorToBase;
        StockQuantity.EnsureStorable(baseQty, field);
        StockQuantity.EnsureWithinBaseDecimals(enteredQty, unit.Unit.ShortCode, baseQty, product.BaseUnit, field);
        return baseQty;
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

    // StockTransfers.Receive at the destination: held company-wide it opens the branches of the membership (and company-level locations); otherwise the working branch must fit.
    private async Task<bool> CanReceiveAsync(StockLocation destination, CancellationToken ct)
    {
        if (await StockLocationRules.HasCompanyLevelAsync(permissions, "StockTransfers.Receive", ct))
        {
            if (db.BranchContext.AllowedBranchIds is null) return true;
            var owner = destination.BranchId;
            if (owner is null && destination.LocationType == StockLocationType.Vehicle)
            {
                var companyId = db.CompanyContext.RequireCompanyId();
                owner = await db.Vehicles.AsNoTracking().Where(v => v.CompanyId == companyId && v.StockLocationId == destination.Id)
                    .Select(v => v.AssignedBranchId).SingleOrDefaultAsync(ct);
            }
            return owner is not { } b || db.BranchContext.CanAccess(b);
        }

        var branch = await BranchRules.ResolveWorkingBranchAsync(db, ct);
        return await StockLocationRules.FitsAsync(db, destination, branch.Id, false, ct);
    }

    private static StockMovement Ledger(
        Guid userId, Consignment consignment, ConsignmentReceipt receipt, StockTransfer transfer, StockTransferLine requestLine, ConsignmentReceiptLine line,
        StockMovementType type, Guid locationId, Guid batchId, decimal signedQty, decimal? entered)
    {
        var movement = StockTransferService.Movement(userId, transfer, requestLine, type, locationId, batchId, signedQty, line.ProductUnitId, line.UnitCode, line.ConversionFactorToBase, entered, null);
        movement.Notes = $"{consignment.ConsignmentNo} receipt {receipt.Sequence}";
        return movement;
    }

    private void Audit(Guid userId, string? correlationId, string action, Consignment consignment, string? reason, object before, object after) =>
        AuditLog.Append(db, userId, correlationId, action, AuditTargets.Consignment, consignment.Id, reason, before, after);

    private static object Snapshot(Shipment shipment, Consignment consignment, ConsignmentReceipt receipt) => new
    {
        shipmentId = shipment.Id,
        shipmentNo = shipment.ShipmentNo,
        shipmentStatus = shipment.Status.ToString(),
        consignmentNo = consignment.ConsignmentNo,
        consignmentStatus = consignment.Status.ToString(),
        receiptId = receipt.Id,
        receiptSequence = receipt.Sequence,
        receiptStatus = receipt.Status.ToString(),
        lines = receipt.Lines.Select(l => new
        {
            l.ConsignmentLineId, l.ProductId, acceptedBaseQty = l.AcceptedBaseQty, damagedBaseQty = l.DamagedBaseQty,
            rejectedBaseQty = l.RejectedBaseQty, missingBaseQty = l.MissingBaseQty,
        }).ToList(),
    };
}

/// <summary>FI-D36: the consignment and shipment status machines after dispatch, as pure functions of line counters, receipt statuses and consignment statuses.</summary>
internal static class TransitStatus
{
    public static void Recompute(Shipment shipment, Consignment consignment, IReadOnlyCollection<ConsignmentReceiptStatus> receipts, Guid userId, DateTimeOffset now)
    {
        if (consignment.Status is ConsignmentStatus.InTransit or ConsignmentStatus.PartiallyReceived or ConsignmentStatus.Received)
        {
            var lines = consignment.Lines.Where(l => l.Status == ConsignmentLineStatus.Active && l.DispatchedBaseQty > 0).ToList();
            decimal Accounted(ConsignmentLine l) => l.AcceptedBaseQty + l.DamagedBaseQty + l.RejectedBaseQty + l.MissingBaseQty;
            var allAccounted = lines.Count > 0 && lines.All(l => Accounted(l) == l.DispatchedBaseQty);
            var any = lines.Any(l => Accounted(l) > 0);
            var next = allAccounted && receipts.Count > 0 && receipts.All(r => r == ConsignmentReceiptStatus.Posted) ? ConsignmentStatus.Received
                : any ? ConsignmentStatus.PartiallyReceived
                : ConsignmentStatus.InTransit;
            if (next != consignment.Status)
            {
                consignment.Status = next;
            }
            consignment.UpdatedAt = now;
            consignment.UpdatedByUserId = userId;
        }

        if (shipment.Status is ShipmentStatus.Dispatched or ShipmentStatus.PartiallyDelivered or ShipmentStatus.Delivered)
        {
            var flown = shipment.Consignments.Where(c => c.Status is ConsignmentStatus.InTransit or ConsignmentStatus.PartiallyReceived or ConsignmentStatus.Received).ToList();
            if (flown.Count > 0)
            {
                shipment.Status = flown.All(c => c.Status == ConsignmentStatus.Received) ? ShipmentStatus.Delivered
                    : flown.Any(c => c.Status == ConsignmentStatus.Received) ? ShipmentStatus.PartiallyDelivered
                    : ShipmentStatus.Dispatched;
                shipment.UpdatedAt = now;
                shipment.UpdatedByUserId = userId;
            }
        }
    }
}
