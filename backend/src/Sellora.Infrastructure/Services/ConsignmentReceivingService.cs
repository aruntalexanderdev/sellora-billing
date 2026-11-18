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
            return await ReplayAsync(consignmentId, existingReceiptId, "StockTransfers.Receive", ct);
        }

        var loaded = await LockAndLoadAsync(consignmentId, ct);
        if (loaded is null) return null;
        var (shipment, consignment, destination) = loaded.Value;
        if (!await FitsAsync("StockTransfers.Receive", destination, ct)) return null;
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

        // FI-D39: the company policy is snapshotted here; later setting changes never alter this receipt.
        var policy = await db.BusinessSettings.AsNoTracking().Where(s => s.CompanyId == consignment.CompanyId)
            .Select(s => new { s.ReceiptPostingMode, s.ReceiptSeparationOfDuties }).SingleOrDefaultAsync(ct);
        var mode = policy?.ReceiptPostingMode ?? ReceiptPostingMode.Direct;
        var initial = mode == ReceiptPostingMode.Direct ? ConsignmentReceiptStatus.Verified : ConsignmentReceiptStatus.PendingApproval;
        var receipt = new ConsignmentReceipt
        {
            CompanyId = consignment.CompanyId,
            Id = Guid.NewGuid(),
            ConsignmentId = consignment.Id,
            Sequence = existing.Count == 0 ? 1 : existing.Max(r => r.Sequence) + 1,
            Status = initial,
            PolicyMode = mode,
            RequireSeparation = policy?.ReceiptSeparationOfDuties ?? false,
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
        TransitStatus.Recompute(shipment, consignment, [.. existing.Select(r => r.Status), initial], userId, now);
        Audit(userId, correlationId, AuditActions.ConsignmentReceiptVerified, consignment, notes, before, Snapshot(shipment, consignment, receipt));

        Idempotency.Record(db, userId, Idempotency.ConsignmentReceipt, input.IdempotencyKey, receipt.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return receipt;
    }

    // ------------------------------------------------------------------ post / approve

    /// <summary>Direct-mode post: a Receive holder at the destination posts a Verified receipt. SupervisorApproval receipts are refused (use approve).</summary>
    public Task<ConsignmentReceipt?> PostAsync(Guid consignmentId, Guid receiptId, PostReceiptInput input, Guid userId, string? correlationId = null, CancellationToken ct = default) =>
        PostCoreAsync(consignmentId, receiptId, input.IdempotencyKey, Idempotency.ConsignmentReceiptPost, "StockTransfers.Receive", approve: false, userId, correlationId, ct);

    /// <summary>Supervisor-mode approval: an ApproveReceipt holder at the destination posts a PendingApproval receipt; identical stock effect to a direct post.</summary>
    public Task<ConsignmentReceipt?> ApproveAsync(Guid consignmentId, Guid receiptId, ApproveReceiptInput input, Guid userId, string? correlationId = null, CancellationToken ct = default) =>
        PostCoreAsync(consignmentId, receiptId, input.IdempotencyKey, Idempotency.ConsignmentReceiptApprove, "StockTransfers.ApproveReceipt", approve: true, userId, correlationId, ct);

    private async Task<ConsignmentReceipt?> PostCoreAsync(
        Guid consignmentId, Guid receiptId, string? idempotencyKey, string idempotencyScope, string permissionCode, bool approve, Guid userId, string? correlationId, CancellationToken ct)
    {
        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, userId, idempotencyScope, idempotencyKey, ct) is { } existingReceiptId)
        {
            if (existingReceiptId != receiptId) throw new DomainValidationException("IdempotencyKey", "This key was already used for a different receipt.");
            return await ReplayAsync(consignmentId, receiptId, permissionCode, ct);
        }

        var loaded = await LockAndLoadAsync(consignmentId, ct);
        if (loaded is null) return null;
        var (shipment, consignment, destination) = loaded.Value;
        if (!await FitsAsync(permissionCode, destination, ct)) return null;

        var receipt = await db.ConsignmentReceipts.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == receiptId && r.ConsignmentId == consignmentId, ct);
        if (receipt is null) return null;
        if (receipt.Status == ConsignmentReceiptStatus.Posted) return receipt; // exactly once: a second post is a no-op (idempotent by state)
        if (receipt.Status == ConsignmentReceiptStatus.RejectedForCorrection)
        {
            throw new DomainValidationException("status", "This receipt was rejected for correction; record a corrected receipt instead.");
        }
        if (approve && receipt.PolicyMode != ReceiptPostingMode.SupervisorApproval)
        {
            throw new DomainValidationException("status", "This receipt does not need approval; post it directly.");
        }
        if (!approve && receipt.PolicyMode == ReceiptPostingMode.SupervisorApproval)
        {
            throw new DomainValidationException("status", "This receipt needs supervisor approval; it cannot be posted directly.");
        }
        if (receipt.RequireSeparation && receipt.VerifiedByUserId == userId)
        {
            throw new DomainValidationException("receiptId", approve
                ? "Separation of duties: the person who recorded this receipt cannot approve it."
                : "Separation of duties: the person who recorded this receipt cannot post it.");
        }
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
        Audit(userId, correlationId, approve ? AuditActions.ConsignmentReceiptApproved : AuditActions.ConsignmentReceiptPosted, consignment, null, before, Snapshot(shipment, consignment, receipt));

        Idempotency.Record(db, userId, idempotencyScope, idempotencyKey, receipt.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return receipt;
    }

    // ------------------------------------------------------------------ reject

    // FI-D39: Direct receipts are rejected by a Receive holder, SupervisorApproval receipts by an ApproveReceipt holder.
    private static string RejectPermission(ConsignmentReceipt r) => r.PolicyMode == ReceiptPostingMode.SupervisorApproval ? "StockTransfers.ApproveReceipt" : "StockTransfers.Receive";

    public async Task<ConsignmentReceipt?> RejectAsync(Guid consignmentId, Guid receiptId, RejectReceiptInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var reason = AuditLog.RequireReason(input.Reason);
        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, userId, Idempotency.ConsignmentReceiptReject, input.IdempotencyKey, ct) is { } existingReceiptId)
        {
            if (existingReceiptId != receiptId) throw new DomainValidationException("IdempotencyKey", "This key was already used for a different receipt.");
            return await ReplayAsync(consignmentId, receiptId, null, ct);
        }

        var loaded = await LockAndLoadAsync(consignmentId, ct);
        if (loaded is null) return null;
        var (shipment, consignment, destination) = loaded.Value;

        var receipt = await db.ConsignmentReceipts.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == receiptId && r.ConsignmentId == consignmentId, ct);
        if (receipt is null) return null;
        if (!await FitsAsync(RejectPermission(receipt), destination, ct)) return null;

        if (receipt.Status == ConsignmentReceiptStatus.RejectedForCorrection) return receipt; // idempotent by state
        if (receipt.Status == ConsignmentReceiptStatus.Posted)
        {
            throw new DomainValidationException("status", "A posted receipt is immutable and cannot be rejected.");
        }
        EnsureReceivable(consignment);

        // Under the consignment lock: the receipt's quantities stop counting as accounted so a corrected receipt can be recorded.
        var now = DateTimeOffset.UtcNow;
        var lineById = consignment.Lines.ToDictionary(l => l.Id);
        foreach (var rl in receipt.Lines)
        {
            var line = lineById[rl.ConsignmentLineId];
            line.AcceptedBaseQty -= rl.AcceptedBaseQty;
            line.DamagedBaseQty -= rl.DamagedBaseQty;
            line.RejectedBaseQty -= rl.RejectedBaseQty;
            line.MissingBaseQty -= rl.MissingBaseQty;
            line.UpdatedAt = now;
        }
        receipt.Status = ConsignmentReceiptStatus.RejectedForCorrection;
        receipt.RejectedAt = now;
        receipt.RejectedByUserId = userId;
        receipt.RejectionReason = reason;

        var statuses = await db.ConsignmentReceipts.AsNoTracking().Where(r => r.ConsignmentId == consignment.Id && r.Id != receipt.Id).Select(r => r.Status).ToListAsync(ct);
        var before = new { consignmentStatus = consignment.Status.ToString(), shipmentStatus = shipment.Status.ToString() };
        TransitStatus.Recompute(shipment, consignment, [.. statuses, ConsignmentReceiptStatus.RejectedForCorrection], userId, now);
        Audit(userId, correlationId, AuditActions.ConsignmentReceiptRejected, consignment, reason, before, Snapshot(shipment, consignment, receipt));

        Idempotency.Record(db, userId, Idempotency.ConsignmentReceiptReject, input.IdempotencyKey, receipt.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return receipt;
    }

    // ------------------------------------------------------------------ dispositions (FI-D40)

    public async Task<ConsignmentDisposition?> DispositionAsync(Guid consignmentId, RecordDispositionInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var reason = AuditLog.RequireReason(input.Reason);
        if (input.Lines is null || input.Lines.Count == 0) throw new DomainValidationException("lines", "Enter at least one disposition line.");
        var duplicates = new HashSet<(Guid, DispositionBucket, DispositionAction)>();
        for (var i = 0; i < input.Lines.Count; i++)
        {
            var li = input.Lines[i];
            if (!Enum.IsDefined(li.Bucket)) throw new DomainValidationException($"lines[{i}].bucket", "Choose damaged, rejected or missing.");
            if (!Enum.IsDefined(li.Action)) throw new DomainValidationException($"lines[{i}].action", "Choose write-off, return to source or accept to destination.");
            if (li.Quantity <= 0) throw new DomainValidationException($"lines[{i}].quantity", "Enter a quantity above zero.");
            if (!duplicates.Add((li.ConsignmentLineId, li.Bucket, li.Action)))
            {
                throw new DomainValidationException($"lines[{i}].consignmentLineId", "This line, bucket and action appear more than once.");
            }
        }

        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, userId, Idempotency.ConsignmentDisposition, input.IdempotencyKey, ct) is { } existingId)
        {
            return await ReplayDispositionAsync(consignmentId, existingId, ct);
        }

        // A company-level decision (like the legacy resolve): a caller without company-wide Resolve gets the same null (404) as for an unknown id, so the
        // answer never reveals whether the consignment exists (the receive / post / approve stance). Checked before any lock.
        if (!await StockLocationRules.HasCompanyLevelAsync(permissions, "StockTransfers.Resolve", ct)) return null;

        var loaded = await LockAndLoadAsync(consignmentId, ct);
        if (loaded is null) return null;
        var (shipment, consignment, destination) = loaded.Value;

        // The resolver's branch allocation must still cover both endpoints (400 otherwise).
        await EnsureResolverAsync(shipment, destination, ct);
        if (consignment.Status is not (ConsignmentStatus.InTransit or ConsignmentStatus.PartiallyReceived or ConsignmentStatus.Received))
        {
            throw new DomainValidationException("status", $"This consignment is {consignment.Status}; only a dispatched consignment with open discrepancies can be dispositioned.");
        }

        var byLine = consignment.Lines.Where(l => l.Status == ConsignmentLineStatus.Active && l.DispatchedBaseQty > 0).ToDictionary(l => l.Id);
        var (products, units) = await LoadProductsAndUnitsAsync(byLine.Values.Select(l => l.ProductId), byLine.Values.Select(l => l.ProductUnitId), ct);

        var prepared = new List<(int Index, ConsignmentLine Line, DispositionLineInput Input, decimal BaseQty)>();
        for (var i = 0; i < input.Lines.Count; i++)
        {
            var li = input.Lines[i];
            if (!byLine.TryGetValue(li.ConsignmentLineId, out var line))
            {
                throw new DomainValidationException($"lines[{i}].consignmentLineId", "This is not a dispatched line of the consignment.");
            }
            prepared.Add((i, line, li, ConvertEntered(units[line.ProductUnitId], products[line.ProductId], li.Quantity, $"lines[{i}].quantity")));
        }

        // Only quantity of POSTED receipts is dispositionable (FI-D40); the line counters also hold unposted receipts.
        var postedRows = await (from rl in db.ConsignmentReceiptLines.AsNoTracking()
                                join r in db.ConsignmentReceipts.AsNoTracking() on rl.ReceiptId equals r.Id
                                where r.ConsignmentId == consignment.Id && r.Status == ConsignmentReceiptStatus.Posted
                                select new { rl.ConsignmentLineId, rl.DamagedBaseQty, rl.RejectedBaseQty, rl.MissingBaseQty }).ToListAsync(ct);
        decimal PostedIn(Guid lineId, DispositionBucket b) => postedRows.Where(r => r.ConsignmentLineId == lineId)
            .Sum(r => b == DispositionBucket.Damaged ? r.DamagedBaseQty : b == DispositionBucket.Rejected ? r.RejectedBaseQty : r.MissingBaseQty);

        foreach (var group in prepared.GroupBy(p => (p.Line.Id, p.Input.Bucket)))
        {
            var line = group.First().Line;
            var available = PostedIn(line.Id, group.Key.Bucket) - Dispositioned(line, group.Key.Bucket);
            if (group.Sum(g => g.BaseQty) > available)
            {
                var product = products[line.ProductId];
                throw new DomainValidationException($"lines[{group.First().Index}].quantity",
                    $"Only {StockQuantity.Format(Math.Max(available, 0))} {product.BaseUnit.ShortCode} of {product.Name} {group.Key.Bucket.ToString().ToLowerInvariant()} is posted and still open to disposition.");
            }
        }

        var companyId = db.CompanyContext.RequireCompanyId();
        var transit = await db.StockLocations.AsNoTracking().SingleOrDefaultAsync(l => l.CompanyId == companyId && l.LocationType == StockLocationType.Transit, ct)
            ?? throw new InvalidOperationException("The company has no Transit location.");
        var returning = prepared.Any(p => p.Input.Action == DispositionAction.ReturnToSource);
        var accepting = prepared.Any(p => p.Input.Action == DispositionAction.AcceptToDestination);

        var transferIds = prepared.Select(p => p.Line.StockTransferId).Distinct().Order().ToList();
        foreach (var tid in transferIds) await StockLocks.LockTransferAsync(db, tid, ct);
        var transfers = await db.StockTransfers.IgnoreQueryFilters([BranchQueryFilter.Name]).Include(t => t.Lines)
            .Where(t => transferIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        var productIds = prepared.Select(p => p.Line.ProductId).Distinct().ToList();
        await StockLocks.LockProductsKeyShareAsync(db, productIds, ct);
        var lockLocations = new List<Guid> { transit.Id };
        if (returning) lockLocations.Add(shipment.SourceLocationId);
        if (accepting) lockLocations.Add(destination.Id);
        await StockLocks.LockLocationsKeyShareAsync(db, lockLocations, ct);
        foreach (var (needed, locationId) in new[] { (returning, shipment.SourceLocationId), (accepting, destination.Id) })
        {
            if (!needed) continue;
            var active = await db.StockLocations.AsNoTracking().Where(l => l.CompanyId == companyId && l.Id == locationId).Select(l => (bool?)l.IsActive).SingleOrDefaultAsync(ct);
            if (active != true) throw new DomainValidationException(DestinationField, InvalidLocation);
        }

        var keys = new List<(Guid, Guid)>();
        foreach (var p in prepared)
        {
            keys.Add((transit.Id, p.Line.ProductId));
            if (p.Input.Action == DispositionAction.ReturnToSource) keys.Add((shipment.SourceLocationId, p.Line.ProductId));
            if (p.Input.Action == DispositionAction.AcceptToDestination) keys.Add((destination.Id, p.Line.ProductId));
        }
        var items = await StockLocks.EnsureAndLockStockItemsAsync(db, keys, ct);

        var lineIds = prepared.Select(p => p.Line.Id).Distinct().ToList();
        var rows = await db.StockTransferLineBatches.Where(b => b.ConsignmentLineId != null && lineIds.Contains(b.ConsignmentLineId.Value)).ToListAsync(ct);
        var batchIds = rows.Select(r => r.PurchaseBatchId).Distinct().ToList();
        var batches = await db.PurchaseBatches.Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);

        var now = DateTimeOffset.UtcNow;
        var sequence = (await db.ConsignmentDispositions.AsNoTracking().Where(d => d.ConsignmentId == consignment.Id).Select(d => (int?)d.Sequence).MaxAsync(ct) ?? 0) + 1;
        var disposition = new ConsignmentDisposition
        {
            CompanyId = consignment.CompanyId,
            Id = Guid.NewGuid(),
            ConsignmentId = consignment.Id,
            Sequence = sequence,
            Reason = reason,
            At = now,
            ByUserId = userId,
        };

        foreach (var (_, line, li, baseQty) in prepared)
        {
            var transfer = transfers[line.StockTransferId];
            var requestLine = transfer.Lines.Single(l => l.Id == line.StockTransferLineId);
            var transitItem = items[(transit.Id, line.ProductId)];

            // The consignment line's own batch rows, oldest batch first; counters are bumped per draw so later lines of this request see them.
            var draws = Take(rows.Where(r => r.ConsignmentLineId == line.Id), batches, baseQty, line.Id);
            foreach (var (row, batch, take) in draws)
            {
                var entered = draws.Count == 1 ? li.Quantity : (decimal?)null;
                switch (li.Action)
                {
                    case DispositionAction.WriteOff:
                        await StockPosting.IssueFromBatchAsync(db, transitItem, batch, take, ct);
                        row.WrittenOffBaseQty += take;
                        db.StockMovements.Add(DispositionLedger(userId, consignment, disposition, transfer, requestLine, line, StockMovementType.Adjustment, transit.Id, batch.Id, -take, entered, AdjustmentType.Loss));
                        break;
                    case DispositionAction.ReturnToSource:
                        await StockPosting.MoveBatchAsync(db, transitItem, items[(shipment.SourceLocationId, line.ProductId)], batch, take, ct);
                        row.ReturnedBaseQty += take;
                        db.StockMovements.Add(DispositionLedger(userId, consignment, disposition, transfer, requestLine, line, StockMovementType.TransferOut, transit.Id, batch.Id, -take, entered, null));
                        db.StockMovements.Add(DispositionLedger(userId, consignment, disposition, transfer, requestLine, line, StockMovementType.TransferIn, shipment.SourceLocationId, batch.Id, take, entered, null));
                        break;
                    default:
                        await StockPosting.MoveBatchAsync(db, transitItem, items[(destination.Id, line.ProductId)], batch, take, ct);
                        row.AcceptedCorrectionBaseQty += take;
                        db.StockMovements.Add(DispositionLedger(userId, consignment, disposition, transfer, requestLine, line, StockMovementType.TransferOut, transit.Id, batch.Id, -take, entered, null));
                        db.StockMovements.Add(DispositionLedger(userId, consignment, disposition, transfer, requestLine, line, StockMovementType.TransferIn, destination.Id, batch.Id, take, entered, null));
                        break;
                }
            }

            switch (li.Bucket)
            {
                case DispositionBucket.Damaged: line.DispositionedDamagedBaseQty += baseQty; break;
                case DispositionBucket.Rejected: line.DispositionedRejectedBaseQty += baseQty; break;
                default: line.DispositionedMissingBaseQty += baseQty; break;
            }
            // The request line keeps its own written-off / returned / accepted-correction counters in step with the batch rows (stock-transfers-validation, outstanding).
            if (li.Action == DispositionAction.WriteOff) requestLine.WrittenOffBaseQty += baseQty;
            else if (li.Action == DispositionAction.ReturnToSource) requestLine.ReturnedBaseQty += baseQty;
            else requestLine.AcceptedCorrectionBaseQty += baseQty;
            line.UpdatedAt = now;

            disposition.Lines.Add(new ConsignmentDispositionLine
            {
                CompanyId = consignment.CompanyId,
                DispositionId = disposition.Id,
                ConsignmentLineId = line.Id,
                ProductId = line.ProductId,
                ProductUnitId = line.ProductUnitId,
                UnitId = line.UnitId,
                UnitCode = line.UnitCode,
                ConversionFactorToBase = line.ConversionFactorToBase,
                Bucket = li.Bucket,
                Action = li.Action,
                EnteredQty = li.Quantity,
                BaseQty = baseQty,
            });
        }
        db.ConsignmentDispositions.Add(disposition);

        var statuses = await db.ConsignmentReceipts.AsNoTracking().Where(r => r.ConsignmentId == consignment.Id).Select(r => r.Status).ToListAsync(ct);
        var before = new { consignmentStatus = consignment.Status.ToString(), shipmentStatus = shipment.Status.ToString() };
        TransitStatus.Recompute(shipment, consignment, statuses, userId, now);
        Audit(userId, correlationId, AuditActions.ConsignmentDispositioned, consignment, reason, before, new
        {
            shipmentNo = shipment.ShipmentNo,
            shipmentStatus = shipment.Status.ToString(),
            consignmentNo = consignment.ConsignmentNo,
            consignmentStatus = consignment.Status.ToString(),
            dispositionId = disposition.Id,
            sequence = disposition.Sequence,
            lines = disposition.Lines.Select(l => new { l.ConsignmentLineId, l.ProductId, bucket = l.Bucket.ToString(), action = l.Action.ToString(), baseQty = l.BaseQty }).ToList(),
        });

        Idempotency.Record(db, userId, Idempotency.ConsignmentDisposition, input.IdempotencyKey, disposition.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return disposition;
    }

    private async Task EnsureResolverAsync(Shipment shipment, StockLocation destination, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var source = await db.StockLocations.AsNoTracking().SingleAsync(l => l.CompanyId == companyId && l.Id == shipment.SourceLocationId, ct);
        if (!await BranchAllowsAsync(source, ct) || !await BranchAllowsAsync(destination, ct)) throw new DomainValidationException(DestinationField, InvalidLocation);
    }

    private async Task<ConsignmentDisposition?> ReplayDispositionAsync(Guid consignmentId, Guid dispositionId, CancellationToken ct)
    {
        var disposition = await db.ConsignmentDispositions.AsNoTracking().Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == dispositionId, ct);
        if (disposition is null || disposition.ConsignmentId != consignmentId)
        {
            throw new DomainValidationException("IdempotencyKey", "This key was already used for a different consignment.");
        }
        if (!await StockLocationRules.HasCompanyLevelAsync(permissions, "StockTransfers.Resolve", ct))
        {
            throw new DomainValidationException("IdempotencyKey", "This request was already processed, but its result is no longer visible with your current access.");
        }
        return disposition;
    }

    private static decimal Dispositioned(ConsignmentLine l, DispositionBucket b) =>
        b == DispositionBucket.Damaged ? l.DispositionedDamagedBaseQty : b == DispositionBucket.Rejected ? l.DispositionedRejectedBaseQty : l.DispositionedMissingBaseQty;

    private static StockMovement DispositionLedger(
        Guid userId, Consignment consignment, ConsignmentDisposition disposition, StockTransfer transfer, StockTransferLine requestLine, ConsignmentLine line,
        StockMovementType type, Guid locationId, Guid batchId, decimal signedQty, decimal? entered, AdjustmentType? reason)
    {
        var movement = StockTransferService.Movement(userId, transfer, requestLine, type, locationId, batchId, signedQty, line.ProductUnitId, line.UnitCode, line.ConversionFactorToBase, entered, reason);
        movement.Notes = $"{consignment.ConsignmentNo} disposition {disposition.Sequence}";
        return movement;
    }

    // ------------------------------------------------------------------ helpers

    private static decimal Accounted(ConsignmentLine l) => l.AcceptedBaseQty + l.DamagedBaseQty + l.RejectedBaseQty + l.MissingBaseQty;

    // Transit left on a shipment batch row (FI-D40): dispatched - posted accepted - written off - returned - accepted by a disposition.
    private static decimal Outstanding(StockTransferLineBatch b) =>
        b.DispatchedBaseQty - b.ReceivedBaseQty - b.DamagedBaseQty - b.WrittenOffBaseQty - b.ReturnedBaseQty - b.AcceptedCorrectionBaseQty;

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

    private async Task<ConsignmentReceipt?> ReplayAsync(Guid consignmentId, Guid receiptId, string? permissionCode, CancellationToken ct)
    {
        var receipt = await db.ConsignmentReceipts.AsNoTracking().Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == receiptId, ct);
        if (receipt is null || receipt.ConsignmentId != consignmentId)
        {
            throw new DomainValidationException("IdempotencyKey", "This key was already used for a different consignment.");
        }

        var companyId = db.CompanyContext.RequireCompanyId();
        var destinationId = await db.Consignments.AsNoTracking().Where(c => c.Id == consignmentId).Select(c => c.DestinationLocationId).SingleAsync(ct);
        var destination = await db.StockLocations.AsNoTracking().SingleAsync(l => l.CompanyId == companyId && l.Id == destinationId, ct);
        if (!await FitsAsync(permissionCode ?? RejectPermission(receipt), destination, ct))
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

    // <paramref name="code"/> (Receive / ApproveReceipt) at the destination: held company-wide it opens the branches of the membership (and company-level locations);
    // otherwise it must be held at the working branch, which must fit the destination.
    private async Task<bool> FitsAsync(string code, StockLocation destination, CancellationToken ct)
    {
        if (await StockLocationRules.HasCompanyLevelAsync(permissions, code, ct))
        {
            return await BranchAllowsAsync(destination, ct);
        }

        var branch = await BranchRules.ResolveWorkingBranchAsync(db, ct);
        if (permissions is not null && !(await permissions.GetCurrentAsync(ct)).HasAtBranch(code, branch.Id)) return false;
        return await StockLocationRules.FitsAsync(db, destination, branch.Id, false, ct);
    }

    private async Task<bool> BranchAllowsAsync(StockLocation location, CancellationToken ct)
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

/// <summary>
/// FI-D36/D41: the consignment and shipment status machines after dispatch, as pure functions of line counters, receipt statuses and consignment statuses.
/// Received = every dispatched unit accounted and all live receipts posted (discrepancies may be open); Reconciled = Received AND every damaged / rejected /
/// missing unit dispositioned (a consignment without discrepancies goes straight to Reconciled at its last post). Delivered = every flown consignment Received or
/// Reconciled; Completed = every flown consignment Reconciled. Rejected receipts do not count.
/// </summary>
internal static class TransitStatus
{
    public static decimal OpenDiscrepancy(ConsignmentLine l) =>
        l.DamagedBaseQty + l.RejectedBaseQty + l.MissingBaseQty - l.DispositionedDamagedBaseQty - l.DispositionedRejectedBaseQty - l.DispositionedMissingBaseQty;

    public static void Recompute(Shipment shipment, Consignment consignment, IReadOnlyCollection<ConsignmentReceiptStatus> receipts, Guid userId, DateTimeOffset now)
    {
        if (consignment.Status is ConsignmentStatus.InTransit or ConsignmentStatus.PartiallyReceived or ConsignmentStatus.Received or ConsignmentStatus.Reconciled)
        {
            var lines = consignment.Lines.Where(l => l.Status == ConsignmentLineStatus.Active && l.DispatchedBaseQty > 0).ToList();
            decimal Accounted(ConsignmentLine l) => l.AcceptedBaseQty + l.DamagedBaseQty + l.RejectedBaseQty + l.MissingBaseQty;
            var allAccounted = lines.Count > 0 && lines.All(l => Accounted(l) == l.DispatchedBaseQty);
            var any = lines.Any(l => Accounted(l) > 0);
            var live = receipts.Where(r => r != ConsignmentReceiptStatus.RejectedForCorrection).ToList();
            var settled = allAccounted && live.Count > 0 && live.All(r => r == ConsignmentReceiptStatus.Posted);
            var next = settled && lines.All(l => OpenDiscrepancy(l) == 0) ? ConsignmentStatus.Reconciled
                : settled ? ConsignmentStatus.Received
                : any ? ConsignmentStatus.PartiallyReceived
                : ConsignmentStatus.InTransit;
            if (next != consignment.Status)
            {
                consignment.Status = next;
            }
            consignment.UpdatedAt = now;
            consignment.UpdatedByUserId = userId;
        }

        if (shipment.Status is ShipmentStatus.Dispatched or ShipmentStatus.PartiallyDelivered or ShipmentStatus.Delivered or ShipmentStatus.Completed)
        {
            var flown = shipment.Consignments.Where(c => c.Status is ConsignmentStatus.InTransit or ConsignmentStatus.PartiallyReceived or ConsignmentStatus.Received or ConsignmentStatus.Reconciled).ToList();
            if (flown.Count > 0)
            {
                static bool Done(Consignment c) => c.Status is ConsignmentStatus.Received or ConsignmentStatus.Reconciled;
                shipment.Status = flown.All(c => c.Status == ConsignmentStatus.Reconciled) ? ShipmentStatus.Completed
                    : flown.All(Done) ? ShipmentStatus.Delivered
                    : flown.Any(Done) ? ShipmentStatus.PartiallyDelivered
                    : ShipmentStatus.Dispatched;
                shipment.UpdatedAt = now;
                shipment.UpdatedByUserId = userId;
            }
        }
    }
}
