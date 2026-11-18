using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.TestData;

/// <summary>
/// Phase 05 (FI-D3): the legacy direct dispatch (<c>POST /stock-transfers/{id}/dispatch</c>) is refused by production code, but transfers that were
/// dispatched that way before the cut-over must still receive and resolve. This fixture writes exactly the state the removed dispatch produced
/// (FIFO source -> Transit through <see cref="StockPosting"/>, legacy batch rows with no ConsignmentLineId, TransferOut/TransferIn movements, dispatch
/// snapshot on the line, status Dispatched) so the receive / resolve / read tests keep running against realistic legacy data. Fixtures only: it applies
/// no permission or availability policy of its own.
/// </summary>
public static class LegacyTransferSeed
{
    public static async Task<StockTransfer> DispatchAsync(SelloraDbContext db, Guid userId, Guid transferId, IReadOnlyList<DispatchStockTransferLineInput> lines)
    {
        var transfer = await db.StockTransfers.Include(t => t.Lines).ThenInclude(l => l.Batches).SingleAsync(t => t.Id == transferId);
        if (transfer.Status != StockTransferStatus.Approved) throw new DomainValidationException("status", $"Seed: transfer is {transfer.Status}, not Approved.");

        var byLine = transfer.Lines.ToDictionary(l => l.Id);
        var inputs = lines.ToDictionary(l => l.LineId);
        var units = await db.ProductUnits.Include(u => u.Unit).Where(u => lines.Select(l => l.ProductUnitId).Contains(u.Id)).ToDictionaryAsync(u => u.Id);
        var shipping = transfer.Lines.Where(l => inputs.ContainsKey(l.Id) && inputs[l.Id].Quantity > 0).ToList();

        var keys = shipping.SelectMany(l => new[] { (transfer.SourceLocationId, l.ProductId), (transfer.TransitLocationId, l.ProductId) });
        var items = await StockLocks.EnsureAndLockStockItemsAsync(db, keys, default);

        foreach (var line in transfer.Lines.OrderBy(l => l.LineNo))
        {
            var input = inputs[line.Id];
            var unit = units[input.ProductUnitId];
            var baseQty = input.Quantity * unit.ConversionFactorToBase;
            line.DispatchProductUnitId = unit.Id;
            line.DispatchUnitId = unit.UnitId;
            line.DispatchUnitCode = unit.Unit.ShortCode;
            line.DispatchFactor = unit.ConversionFactorToBase;
            line.DispatchedQty = input.Quantity;
            line.DispatchedBaseQty = baseQty;
            if (baseQty == 0) continue;

            var allocations = await StockPosting.MoveFifoAsync(db, items[(transfer.SourceLocationId, line.ProductId)], items[(transfer.TransitLocationId, line.ProductId)], baseQty,
                shortBy => new DomainValidationException("lines", $"Seed: batch stock is short by {shortBy}."), default);
            foreach (var allocation in allocations)
            {
                line.Batches.Add(new StockTransferLineBatch { LineId = line.Id, PurchaseBatchId = allocation.Batch.Id, DispatchedBaseQty = allocation.QtyBaseUnits, UnitCost = allocation.UnitCost });
                var entered = allocations.Count == 1 ? input.Quantity : (decimal?)null;
                db.StockMovements.Add(StockTransferService.Movement(userId, transfer, line, StockMovementType.TransferOut, transfer.SourceLocationId, allocation.Batch.Id, -allocation.QtyBaseUnits, unit.Id, unit.Unit.ShortCode, unit.ConversionFactorToBase, entered, null));
                db.StockMovements.Add(StockTransferService.Movement(userId, transfer, line, StockMovementType.TransferIn, transfer.TransitLocationId, allocation.Batch.Id, allocation.QtyBaseUnits, unit.Id, unit.Unit.ShortCode, unit.ConversionFactorToBase, entered, null));
            }
        }

        transfer.Status = StockTransferStatus.Dispatched;
        transfer.DispatchedAt = DateTimeOffset.UtcNow;
        transfer.DispatchedByUserId = userId;
        AuditLog.Append(db, userId, null, AuditActions.StockTransferDispatched, AuditTargets.StockTransfer, transfer.Id, null, new { status = StockTransferStatus.Approved.ToString() }, new { transferNo = transfer.TransferNo, status = transfer.Status.ToString() });
        await db.SaveChangesAsync();
        return transfer;
    }
}
