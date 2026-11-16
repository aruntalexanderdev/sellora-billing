using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Phase 04 (FI-D29..D33) shipment planning, allocation, reservations and loading. Entered quantities are converted to base units once
/// (the allocation's unit snapshot) and everything stateful is base units. Nothing here posts stock: allocation moves
/// <c>StockItem.ReservedQuantity</c> and the Active <see cref="StockReservation"/> in step, and the request line's AllocatedBaseQty with them.
/// Lock order for every operation: idempotency advisory lock → shipment header FOR UPDATE → transfer headers FOR UPDATE (id order) →
/// products KEY SHARE → locations KEY SHARE → StockItems ensure + FOR UPDATE (location, product order). Loading, vehicles and header edits
/// stop after the shipment header (no stock is touched).
/// Status decisions: a shipment stays Draft while it is only being planned (allocations, vehicles); the first recorded loading makes it Loading;
/// it becomes ReadyToDispatch when every live consignment is Loaded and the mode reference is valid, and falls back to Loading / Draft when
/// loading is corrected downwards or the plan changes. ReadyToDispatch accepts loading corrections, consignment/shipment cancellation and
/// reservation release only; planning edits need Draft / Loading.
/// </summary>
public class ShipmentService(SelloraDbContext db, IEffectivePermissionService? permissions = null) : IShipmentService
{
    private const int MaxText = 120;
    private const string SourceField = "sourceLocationId";
    private const string InvalidLocation = "Select a valid, active stock location.";

    // ------------------------------------------------------------------ create / header

    public async Task<Shipment> CreateAsync(CreateShipmentInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var header = NormalizeHeader(input.TransportMode, input.TransportVia, input.ProviderName, input.ProviderContact, input.TransportReference, input.Notes);

        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, userId, Idempotency.Shipment, input.IdempotencyKey, ct) is { } existingId)
        {
            return await LoadGraphAsync(existingId, ct)
                ?? throw new DomainValidationException("IdempotencyKey", "This request was already processed, but its result is no longer visible with your current branch access.");
        }

        var branch = await BranchRules.ResolveWorkingBranchAsync(db, ct);
        var (source, effectiveBranch) = await ResolveSourceAsync(input.SourceLocationId, ct);
        await EnsureFitAsync("Shipments.Plan", source, ct);
        await StockLocks.LockLocationsKeyShareAsync(db, [source.Id], ct);
        await ResolveSourceAsync(input.SourceLocationId, ct);

        var now = DateTimeOffset.UtcNow;
        var shipment = new Shipment
        {
            Id = Guid.NewGuid(),
            ShipmentNo = await DocumentNumbers.NextNumberAsync(db, DocumentSeries.Shipment, branch, ct),
            BranchId = effectiveBranch,
            SourceLocationId = source.Id,
            Status = ShipmentStatus.Draft,
            TransportMode = input.TransportMode,
            TransportVia = input.TransportVia,
            ProviderName = header.Provider,
            ProviderContact = header.Contact,
            TransportReference = header.Reference,
            PlannedDispatchDate = input.PlannedDispatchDate,
            Notes = header.Notes,
            CreatedAt = now,
            CreatedByUserId = userId,
        };
        db.Shipments.Add(shipment);
        Audit(userId, correlationId, AuditActions.ShipmentCreated, AuditTargets.Shipment, shipment.Id, null, null, Snapshot(shipment));

        Idempotency.Record(db, userId, Idempotency.Shipment, input.IdempotencyKey, shipment.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return shipment;
    }

    public async Task<Shipment?> UpdateAsync(Guid id, UpdateShipmentInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var header = NormalizeHeader(input.TransportMode, input.TransportVia, input.ProviderName, input.ProviderContact, input.TransportReference, input.Notes);

        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var shipment = await LockAndLoadAsync(id, ct);
        if (shipment is null) return null;
        EnsureStatus(shipment, ShipmentStatus.Draft, ShipmentStatus.Loading);
        await EnsureFitAsync("Shipments.Plan", await LoadSourceAsync(shipment, ct), ct);

        // Switching own fleet / external provider is only possible while no vehicle of the other kind is attached.
        if (input.TransportVia != shipment.TransportVia && shipment.Vehicles.Count > 0)
        {
            throw new DomainValidationException("transportVia", "Remove the vehicles before changing between own fleet and an external provider.");
        }

        var before = Snapshot(shipment);
        shipment.TransportMode = input.TransportMode;
        shipment.TransportVia = input.TransportVia;
        shipment.ProviderName = header.Provider;
        shipment.ProviderContact = header.Contact;
        shipment.TransportReference = header.Reference;
        shipment.PlannedDispatchDate = input.PlannedDispatchDate;
        shipment.Notes = header.Notes;

        var now = DateTimeOffset.UtcNow;
        Touch(shipment, userId, now);
        RecomputeShipment(shipment, userId, now);
        Audit(userId, correlationId, AuditActions.ShipmentUpdated, AuditTargets.Shipment, shipment.Id, null, before, Snapshot(shipment));
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Finish(shipment);
    }

    // ------------------------------------------------------------------ vehicles

    public async Task<Shipment?> AddVehicleAsync(Guid id, ShipmentVehicleInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var driverName = Text(input.DriverName, "driverName", false);
        var driverPhone = Text(input.DriverPhone, "driverPhone", false, 30);
        var externalNumber = Text(input.ExternalVehicleNumber, "externalVehicleNumber", false, 40);
        var externalDescription = Text(input.ExternalDescription, "externalDescription", false);

        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var shipment = await LockAndLoadAsync(id, ct);
        if (shipment is null) return null;
        EnsureStatus(shipment, ShipmentStatus.Draft, ShipmentStatus.Loading);
        await EnsureFitAsync("Shipments.Plan", await LoadSourceAsync(shipment, ct), ct);

        if (shipment.TransportVia == TransportVia.OwnFleet)
        {
            if (input.VehicleId is not { } vehicleId) throw new DomainValidationException("vehicleId", "Select a vehicle of the own fleet.");
            if (externalNumber is not null) throw new DomainValidationException("externalVehicleNumber", "An own-fleet shipment uses vehicles from the vehicle master.");

            var companyId = db.CompanyContext.RequireCompanyId();
            var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(v => v.CompanyId == companyId && v.Id == vehicleId, ct);
            // Scope: active, and central (unassigned) or assigned to the shipment's source branch. Unknown, foreign and out-of-scope share one message.
            if (vehicle is null || !vehicle.IsActive || (vehicle.AssignedBranchId is { } assigned && assigned != shipment.BranchId))
            {
                throw new DomainValidationException("vehicleId", "Select a valid, active vehicle available to the source branch.");
            }
            if (shipment.Vehicles.Any(v => v.VehicleId == vehicleId))
            {
                throw new DomainValidationException("vehicleId", "This vehicle is already on the shipment.");
            }
        }
        else
        {
            if (input.VehicleId is not null) throw new DomainValidationException("vehicleId", "An external-provider shipment uses external vehicle details.");
            if (externalNumber is null) throw new DomainValidationException("externalVehicleNumber", "Enter the external vehicle number.");
        }

        var now = DateTimeOffset.UtcNow;
        var row = new ShipmentVehicle
        {
            // Children of a tracked principal are added explicitly with their company set: a Guid key set by us would make navigation fix-up
            // treat the row as existing, and CompanyId (part of the composite FK) must already match the principal.
            CompanyId = shipment.CompanyId,
            Id = Guid.NewGuid(),
            ShipmentId = shipment.Id,
            Sequence = shipment.Vehicles.Count == 0 ? 1 : shipment.Vehicles.Max(v => v.Sequence) + 1,
            VehicleId = shipment.TransportVia == TransportVia.OwnFleet ? input.VehicleId : null,
            ExternalVehicleNumber = shipment.TransportVia == TransportVia.OwnFleet ? null : externalNumber,
            ExternalDescription = shipment.TransportVia == TransportVia.OwnFleet ? null : externalDescription,
            DriverName = driverName,
            DriverPhone = driverPhone,
            CreatedAt = now,
        };
        db.ShipmentVehicles.Add(row);
        Touch(shipment, userId, now);
        RecomputeShipment(shipment, userId, now);
        Audit(userId, correlationId, AuditActions.ShipmentVehicleAdded, AuditTargets.Shipment, shipment.Id, null, null,
            new { shipmentNo = shipment.ShipmentNo, shipmentVehicleId = row.Id, row.Sequence, row.VehicleId, row.ExternalVehicleNumber });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Finish(shipment);
    }

    public async Task<Shipment?> RemoveVehicleAsync(Guid id, Guid shipmentVehicleId, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var shipment = await LockAndLoadAsync(id, ct);
        if (shipment is null) return null;
        EnsureStatus(shipment, ShipmentStatus.Draft, ShipmentStatus.Loading);
        await EnsureFitAsync("Shipments.Plan", await LoadSourceAsync(shipment, ct), ct);

        var row = shipment.Vehicles.SingleOrDefault(v => v.Id == shipmentVehicleId)
            ?? throw new DomainValidationException("shipmentVehicleId", "This vehicle is not on the shipment.");
        if (shipment.Consignments.Any(c => c.Status != ConsignmentStatus.Cancelled && c.ShipmentVehicleId == row.Id))
        {
            throw new DomainValidationException("shipmentVehicleId", "A consignment is assigned to this vehicle; assign it to another vehicle first.");
        }

        var now = DateTimeOffset.UtcNow;
        shipment.Vehicles.Remove(row);
        db.ShipmentVehicles.Remove(row);
        Touch(shipment, userId, now);
        RecomputeShipment(shipment, userId, now);
        Audit(userId, correlationId, AuditActions.ShipmentVehicleRemoved, AuditTargets.Shipment, shipment.Id, null,
            new { shipmentVehicleId = row.Id, row.Sequence }, new { shipmentNo = shipment.ShipmentNo });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Finish(shipment);
    }

    // ------------------------------------------------------------------ cancel

    public async Task<Shipment?> CancelAsync(Guid id, string reason, Guid userId, string? idempotencyKey = null, string? correlationId = null, CancellationToken ct = default)
    {
        var text = Text(reason, "reason", required: true, 300)!;

        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, userId, Idempotency.ShipmentCancel, idempotencyKey, ct) is { } existingId)
        {
            if (existingId != id) throw new DomainValidationException("IdempotencyKey", "This key was already used for a different shipment.");
            return await LoadGraphAsync(id, ct) ?? throw ReplayHidden();
        }

        var shipment = await LockAndLoadAsync(id, ct);
        if (shipment is null) return null;
        var source = await LoadSourceAsync(shipment, ct);
        await EnsureFitAsync("Shipments.Plan", source, ct);
        if (shipment.Status == ShipmentStatus.Cancelled) return Finish(shipment); // idempotent by state

        var now = DateTimeOffset.UtcNow;
        var before = Snapshot(shipment);
        var lines = shipment.Consignments.SelectMany(c => c.Lines).Where(l => l.Status == ConsignmentLineStatus.Active).ToList();
        var ctx = await PrepareAsync(shipment, lines.Select(l => l.StockTransferId), lines.Select(l => l.ProductId), lines.Select(l => l.Id), ct);

        foreach (var consignment in shipment.Consignments.Where(c => c.Status != ConsignmentStatus.Cancelled))
        {
            CancelConsignmentCore(ctx, consignment, $"Shipment cancelled: {text}", text, userId, now);
        }

        shipment.Status = ShipmentStatus.Cancelled;
        shipment.CancelledAt = now;
        shipment.CancelledByUserId = userId;
        shipment.CancellationReason = text;
        Touch(shipment, userId, now);
        Audit(userId, correlationId, AuditActions.ShipmentCancelled, AuditTargets.Shipment, shipment.Id, text, before, Snapshot(shipment));

        Idempotency.Record(db, userId, Idempotency.ShipmentCancel, idempotencyKey, shipment.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Finish(shipment);
    }

    public async Task<Shipment?> CancelConsignmentAsync(Guid id, Guid consignmentId, string reason, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var text = Text(reason, "reason", required: true, 300)!;

        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var shipment = await LockAndLoadAsync(id, ct);
        if (shipment is null) return null;
        await EnsureFitAsync("Shipments.Plan", await LoadSourceAsync(shipment, ct), ct);

        var consignment = shipment.Consignments.SingleOrDefault(c => c.Id == consignmentId)
            ?? throw new DomainValidationException("consignmentId", "This consignment is not on the shipment.");
        if (consignment.Status == ConsignmentStatus.Cancelled) return Finish(shipment); // idempotent by state, also on a cancelled shipment
        EnsureStatus(shipment, ShipmentStatus.Draft, ShipmentStatus.Loading, ShipmentStatus.ReadyToDispatch);

        var now = DateTimeOffset.UtcNow;
        var lines = consignment.Lines.Where(l => l.Status == ConsignmentLineStatus.Active).ToList();
        var ctx = await PrepareAsync(shipment, lines.Select(l => l.StockTransferId), lines.Select(l => l.ProductId), lines.Select(l => l.Id), ct);

        CancelConsignmentCore(ctx, consignment, $"Consignment cancelled: {text}", text, userId, now);
        Touch(shipment, userId, now);
        RecomputeShipment(shipment, userId, now);
        Audit(userId, correlationId, AuditActions.ConsignmentCancelled, AuditTargets.Shipment, shipment.Id, text,
            null, new { shipmentNo = shipment.ShipmentNo, consignmentId = consignment.Id, consignment.ConsignmentNo, status = shipment.Status.ToString() });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Finish(shipment);
    }

    // ------------------------------------------------------------------ allocation

    public async Task<Shipment?> AllocateAsync(Guid id, AllocateShipmentInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        if (input.Quantity <= 0) throw new DomainValidationException("quantity", "Quantity must be greater than zero.");

        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, userId, Idempotency.ShipmentAllocate, input.IdempotencyKey, ct) is { } existingId)
        {
            if (existingId != id) throw new DomainValidationException("IdempotencyKey", "This key was already used for a different shipment.");
            return await LoadGraphAsync(id, ct) ?? throw ReplayHidden();
        }

        var shipment = await LockAndLoadAsync(id, ct);
        if (shipment is null) return null;
        EnsureStatus(shipment, ShipmentStatus.Draft, ShipmentStatus.Loading);
        var source = await LoadSourceAsync(shipment, ct);
        await EnsureFitAsync("Shipments.Plan", source, ct);
        if (!source.IsActive) throw new DomainValidationException(SourceField, InvalidLocation);

        // The request line names its transfer (and, immutably once approved, its destination): header lock after the shipment lock, before any stock lock.
        var companyId = db.CompanyContext.RequireCompanyId();
        var route = await (from l in db.StockTransferLines.AsNoTracking()
                           join t in db.StockTransfers.AsNoTracking().IgnoreQueryFilters([BranchQueryFilter.Name]) on l.TransferId equals t.Id
                           where l.CompanyId == companyId && l.Id == input.StockTransferLineId
                           select new { l.TransferId, l.ProductId, t.DestinationLocationId }).SingleOrDefaultAsync(ct)
            ?? throw new DomainValidationException("stockTransferLineId", "Select a valid transfer request line.");
        var (transferId, productId) = (route.TransferId, route.ProductId);

        var ctx = await PrepareAsync(shipment, [transferId], [productId], [], ct, extraLocations: [route.DestinationLocationId]);
        var transfer = ctx.Transfers[transferId];
        var requestLine = transfer.Lines.Single(l => l.Id == input.StockTransferLineId);

        if (transfer.Status is not (StockTransferStatus.Approved or StockTransferStatus.PartiallyFulfilled))
        {
            throw new DomainValidationException("stockTransferLineId", $"This request is {transfer.Status}; only Approved or partially fulfilled requests can be allocated.");
        }
        if (transfer.SourceLocationId != shipment.SourceLocationId)
        {
            throw new DomainValidationException("stockTransferLineId", "Multi-pickup is not supported: this request's source location differs from the shipment's source location.");
        }
        if (transfer.DestinationLocationId == shipment.SourceLocationId)
        {
            throw new DomainValidationException("stockTransferLineId", "The request's destination is the shipment's source location.");
        }

        var (products, units) = await LoadProductsAndUnitsAsync([productId], [input.ProductUnitId], ct);
        var product = products[productId];
        var unit = RequireUnit(units, input.ProductUnitId, productId, "productUnitId", requireActive: true);
        var baseQty = ConvertEntered(unit, product, input.Quantity, "quantity");

        // Find or create the consignment of this destination, then the line.
        var consignment = shipment.Consignments.SingleOrDefault(c => c.Status != ConsignmentStatus.Cancelled && c.DestinationLocationId == transfer.DestinationLocationId);
        var line = consignment?.Lines.SingleOrDefault(l => l.Status == ConsignmentLineStatus.Active && l.StockTransferLineId == requestLine.Id);
        if (line is not null && line.ProductUnitId != unit.Id)
        {
            throw new DomainValidationException("productUnitId", $"This request line is already allocated in {line.UnitCode}; use the same unit.");
        }

        await EnsureCapacityAsync(ctx, requestLine, shipment.SourceLocationId, baseQty, product, "quantity", ct);

        var now = DateTimeOffset.UtcNow;
        var created = false;
        if (consignment is null)
        {
            await EnsureLocationActiveAsync(transfer.DestinationLocationId, ct);

            var numberBranch = shipment.BranchId is { } b
                ? await db.Branches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == b, ct) ?? await BranchRules.ResolveWorkingBranchAsync(db, ct)
                : await BranchRules.ResolveWorkingBranchAsync(db, ct);
            consignment = new Consignment
            {
                CompanyId = shipment.CompanyId,
                Id = Guid.NewGuid(),
                ShipmentId = shipment.Id,
                ConsignmentNo = await DocumentNumbers.NextNumberAsync(db, DocumentSeries.Consignment, numberBranch, ct),
                DestinationLocationId = transfer.DestinationLocationId,
                DestinationBranchId = transfer.DestinationBranchId,
                Status = ConsignmentStatus.Planned,
                CreatedAt = now,
                CreatedByUserId = userId,
            };
            db.Consignments.Add(consignment);
        }

        var item = ctx.Items[(shipment.SourceLocationId, productId)];
        if (line is null)
        {
            created = true;
            line = new ConsignmentLine
            {
                CompanyId = shipment.CompanyId,
                Id = Guid.NewGuid(),
                ConsignmentId = consignment.Id,
                StockTransferId = transfer.Id,
                StockTransferLineId = requestLine.Id,
                ProductId = productId,
                ProductUnitId = unit.Id,
                UnitId = unit.UnitId,
                UnitCode = unit.Unit.ShortCode,
                ConversionFactorToBase = unit.ConversionFactorToBase,
                EnteredQty = input.Quantity,
                AllocatedBaseQty = baseQty,
                CreatedAt = now,
            };
            db.ConsignmentLines.Add(line);
            db.StockReservations.Add(new StockReservation
            {
                CompanyId = shipment.CompanyId,
                Id = Guid.NewGuid(),
                SourceLocationId = shipment.SourceLocationId,
                ProductId = productId,
                BaseQty = baseQty,
                ConsignmentLineId = line.Id,
                CreatedAt = now,
                CreatedByUserId = userId,
            });
        }
        else
        {
            line.EnteredQty += input.Quantity;
            line.AllocatedBaseQty = line.EnteredQty * line.ConversionFactorToBase;
            line.UpdatedAt = now;
            var reservation = await db.StockReservations.SingleAsync(r => r.ConsignmentLineId == line.Id && r.Status == StockReservationStatus.Active, ct);
            reservation.BaseQty += baseQty;
        }

        // StockItem first (its row is locked), then the reservation / request line that the DB constraint reads against it.
        item.ReservedQuantity += baseQty;
        requestLine.AllocatedBaseQty += baseQty;

        Touch(consignment, userId, now);
        RecomputeConsignment(shipment, consignment, userId, now);
        Touch(shipment, userId, now);
        RecomputeShipment(shipment, userId, now);

        Audit(userId, correlationId, AuditActions.ShipmentAllocated, AuditTargets.Shipment, shipment.Id, null, null, new
        {
            shipmentNo = shipment.ShipmentNo, consignmentNo = consignment.ConsignmentNo, consignmentId = consignment.Id, consignmentLineId = line.Id,
            stockTransferId = transfer.Id, stockTransferLineId = requestLine.Id, productId, productUnitId = unit.Id,
            addedQty = input.Quantity, addedBaseQty = baseQty, allocatedBaseQty = line.AllocatedBaseQty, newLine = created,
        });

        Idempotency.Record(db, userId, Idempotency.ShipmentAllocate, input.IdempotencyKey, shipment.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Finish(shipment);
    }

    public async Task<Shipment?> ChangeAllocationAsync(Guid id, Guid consignmentLineId, ChangeAllocationInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        if (input.Quantity < 0) throw new DomainValidationException("quantity", "Quantity cannot be negative.");

        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, userId, Idempotency.ShipmentAllocate, input.IdempotencyKey, ct) is { } existingId)
        {
            if (existingId != id) throw new DomainValidationException("IdempotencyKey", "This key was already used for a different shipment.");
            return await LoadGraphAsync(id, ct) ?? throw ReplayHidden();
        }

        var shipment = await LockAndLoadAsync(id, ct);
        if (shipment is null) return null;
        EnsureStatus(shipment, ShipmentStatus.Draft, ShipmentStatus.Loading);
        var source = await LoadSourceAsync(shipment, ct);
        await EnsureFitAsync("Shipments.Plan", source, ct);

        var (consignment, line) = FindActiveLine(shipment, consignmentLineId);
        var ctx = await PrepareAsync(shipment, [line.StockTransferId], [line.ProductId], [line.Id], ct);
        var requestLine = ctx.Transfers[line.StockTransferId].Lines.Single(l => l.Id == line.StockTransferLineId);

        var (products, units) = await LoadProductsAndUnitsAsync([line.ProductId], [line.ProductUnitId], ct);
        var unit = RequireUnit(units, line.ProductUnitId, line.ProductId, "productUnitId", requireActive: false);
        var newBase = input.Quantity == 0 ? 0m : ConvertEntered(unit, products[line.ProductId], input.Quantity, "quantity");
        var delta = newBase - line.AllocatedBaseQty;
        // Reducing may use a since-deactivated unit; adding stock to the allocation needs an active one.
        if (delta > 0) RequireUnit(units, line.ProductUnitId, line.ProductId, "productUnitId", requireActive: true);

        var now = DateTimeOffset.UtcNow;
        string action;
        if (input.Quantity == 0)
        {
            if (line.LoadedBaseQty > 0) throw new DomainValidationException("quantity", "Unload this line before cancelling its allocation.");
            ReduceLine(ctx, line, 0m, "Allocation cancelled", userId, now);
            action = AuditActions.ShipmentAllocationCancelled;
        }
        else
        {
            if (newBase < line.LoadedBaseQty)
            {
                throw new DomainValidationException("quantity", $"{StockQuantity.Format(line.LoadedBaseQty)} is already loaded; unload before reducing below it.");
            }
            if (delta > 0) await EnsureCapacityAsync(ctx, requestLine, shipment.SourceLocationId, delta, products[line.ProductId], "quantity", ct);
            if (delta > 0) IncreaseLine(ctx, line, input.Quantity, newBase, now); else ReduceLine(ctx, line, input.Quantity, null, userId, now);
            action = AuditActions.ShipmentAllocationChanged;
        }

        Touch(consignment, userId, now);
        RecomputeConsignment(shipment, consignment, userId, now);
        Touch(shipment, userId, now);
        RecomputeShipment(shipment, userId, now);
        Audit(userId, correlationId, action, AuditTargets.Shipment, shipment.Id, null, null, new
        {
            shipmentNo = shipment.ShipmentNo, consignmentNo = consignment.ConsignmentNo, consignmentLineId = line.Id,
            stockTransferLineId = line.StockTransferLineId, enteredQty = line.EnteredQty, allocatedBaseQty = line.AllocatedBaseQty,
        });

        Idempotency.Record(db, userId, Idempotency.ShipmentAllocate, input.IdempotencyKey, shipment.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Finish(shipment);
    }

    public Task<Shipment?> CancelAllocationAsync(Guid id, Guid consignmentLineId, Guid userId, string? correlationId = null, CancellationToken ct = default) =>
        ChangeAllocationAsync(id, consignmentLineId, new ChangeAllocationInput(0m), userId, correlationId, ct);

    // ------------------------------------------------------------------ legs and loading

    public async Task<Shipment?> AssignVehicleAsync(Guid id, Guid consignmentId, Guid? shipmentVehicleId, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var shipment = await LockAndLoadAsync(id, ct);
        if (shipment is null) return null;
        EnsureStatus(shipment, ShipmentStatus.Draft, ShipmentStatus.Loading);
        await EnsureFitAsync("Shipments.Plan", await LoadSourceAsync(shipment, ct), ct);

        var consignment = FindConsignment(shipment, consignmentId);
        if (shipmentVehicleId is { } vehicleId && shipment.Vehicles.All(v => v.Id != vehicleId))
        {
            throw new DomainValidationException("shipmentVehicleId", "This vehicle is not on the shipment.");
        }

        var now = DateTimeOffset.UtcNow;
        consignment.ShipmentVehicleId = shipmentVehicleId;
        Touch(consignment, userId, now);
        RecomputeConsignment(shipment, consignment, userId, now);
        Touch(shipment, userId, now);
        RecomputeShipment(shipment, userId, now);
        Audit(userId, correlationId, AuditActions.ShipmentVehicleAssigned, AuditTargets.Shipment, shipment.Id, null, null,
            new { shipmentNo = shipment.ShipmentNo, consignmentId = consignment.Id, consignment.ConsignmentNo, shipmentVehicleId, consignmentStatus = consignment.Status.ToString() });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Finish(shipment);
    }

    public async Task<Shipment?> LoadAsync(Guid id, Guid consignmentId, LoadConsignmentInput input, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        if (input.Lines is null || input.Lines.Count == 0) throw new DomainValidationException("lines", "Enter the loaded quantity of at least one line.");
        for (var i = 0; i < input.Lines.Count; i++)
        {
            if (input.Lines[i].LoadedQuantity < 0) throw new DomainValidationException($"lines[{i}].loadedQuantity", "Loaded quantity cannot be negative.");
        }

        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, userId, Idempotency.ShipmentLoad, input.IdempotencyKey, ct) is { } existingId)
        {
            if (existingId != id) throw new DomainValidationException("IdempotencyKey", "This key was already used for a different shipment.");
            return await LoadGraphAsync(id, ct) ?? throw ReplayHidden();
        }

        var shipment = await LockAndLoadAsync(id, ct);
        if (shipment is null) return null;
        EnsureStatus(shipment, ShipmentStatus.Draft, ShipmentStatus.Loading, ShipmentStatus.ReadyToDispatch);
        await EnsureFitAsync("Shipments.Load", await LoadSourceAsync(shipment, ct), ct);

        var consignment = FindConsignment(shipment, consignmentId);
        var active = consignment.Lines.Where(l => l.Status == ConsignmentLineStatus.Active).ToDictionary(l => l.Id);

        var seen = new HashSet<Guid>();
        var (_, units) = await LoadProductsAndUnitsAsync(active.Values.Select(l => l.ProductId), active.Values.Select(l => l.ProductUnitId), ct);
        var updates = new List<(ConsignmentLine Line, decimal Base)>();
        for (var i = 0; i < input.Lines.Count; i++)
        {
            var li = input.Lines[i];
            if (!active.TryGetValue(li.ConsignmentLineId, out var line))
            {
                throw new DomainValidationException($"lines[{i}].consignmentLineId", "This line is not an active line of the consignment.");
            }
            if (!seen.Add(line.Id)) throw new DomainValidationException($"lines[{i}].consignmentLineId", "This line appears more than once.");

            var unit = units[line.ProductUnitId];
            StockQuantity.EnsureEnteredDecimals(li.LoadedQuantity, unit.Unit.DecimalPlaces, unit.Label ?? unit.Unit.Name, $"lines[{i}].loadedQuantity");
            var baseQty = li.LoadedQuantity == line.EnteredQty ? line.AllocatedBaseQty : li.LoadedQuantity * line.ConversionFactorToBase;
            StockQuantity.EnsureStorable(baseQty, $"lines[{i}].loadedQuantity");
            if (baseQty > line.AllocatedBaseQty)
            {
                throw new DomainValidationException($"lines[{i}].loadedQuantity",
                    $"Cannot load more than was allocated ({StockQuantity.Format(line.EnteredQty)} {line.UnitCode}).");
            }
            updates.Add((line, baseQty));
        }

        var now = DateTimeOffset.UtcNow;
        var before = new { status = shipment.Status.ToString(), consignmentStatus = consignment.Status.ToString() };
        foreach (var (line, baseQty) in updates)
        {
            line.LoadedBaseQty = baseQty;
            line.UpdatedAt = now;
        }

        Touch(consignment, userId, now);
        RecomputeConsignment(shipment, consignment, userId, now);
        Touch(shipment, userId, now);
        RecomputeShipment(shipment, userId, now);
        Audit(userId, correlationId, AuditActions.ShipmentLoaded, AuditTargets.Shipment, shipment.Id, null, before, new
        {
            shipmentNo = shipment.ShipmentNo, status = shipment.Status.ToString(), consignmentId = consignment.Id, consignment.ConsignmentNo,
            consignmentStatus = consignment.Status.ToString(),
            lines = updates.Select(u => new { consignmentLineId = u.Line.Id, loadedBaseQty = u.Line.LoadedBaseQty }).ToList(),
        });

        Idempotency.Record(db, userId, Idempotency.ShipmentLoad, input.IdempotencyKey, shipment.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Finish(shipment);
    }

    // ------------------------------------------------------------------ reservation release

    public async Task<StockReservation?> ReleaseReservationAsync(Guid reservationId, string reason, Guid userId, string? correlationId = null, CancellationToken ct = default)
    {
        var text = Text(reason, "reason", required: true, 300)!;

        var companyId = db.CompanyContext.RequireCompanyId();
        var route = await (from r in db.StockReservations.AsNoTracking()
                           join l in db.ConsignmentLines.AsNoTracking() on r.ConsignmentLineId equals l.Id
                           join c in db.Consignments.AsNoTracking() on l.ConsignmentId equals c.Id
                           where r.CompanyId == companyId && r.Id == reservationId
                           select new { c.ShipmentId, LineId = l.Id }).SingleOrDefaultAsync(ct);
        if (route is null) return null;

        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var shipment = await LockAndLoadAsync(route.ShipmentId, ct);
        if (shipment is null) return null; // outside the caller's source scope
        await EnsureFitAsync("Reservations.Release", await LoadSourceAsync(shipment, ct), ct);

        var reservation = await db.StockReservations.SingleAsync(r => r.Id == reservationId, ct);
        if (reservation.Status == StockReservationStatus.Released) return reservation; // idempotent by state
        if (reservation.Status != StockReservationStatus.Active)
        {
            throw new DomainValidationException("status", $"This reservation is {reservation.Status}; only an Active reservation can be released.");
        }

        var consignment = shipment.Consignments.Single(c => c.Lines.Any(l => l.Id == route.LineId));
        var line = consignment.Lines.Single(l => l.Id == route.LineId);
        var ctx = await PrepareAsync(shipment, [line.StockTransferId], [line.ProductId], [line.Id], ct);

        var now = DateTimeOffset.UtcNow;
        ReduceLine(ctx, line, 0m, text, userId, now, force: true);
        Touch(consignment, userId, now);
        RecomputeConsignment(shipment, consignment, userId, now);
        Touch(shipment, userId, now);
        RecomputeShipment(shipment, userId, now);
        Audit(userId, correlationId, AuditActions.StockReservationReleased, AuditTargets.StockReservation, reservation.Id, text,
            new { status = StockReservationStatus.Active.ToString() },
            new { status = reservation.Status.ToString(), shipmentNo = shipment.ShipmentNo, consignmentLineId = line.Id, reservation.ProductId, reservation.SourceLocationId });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return reservation;
    }

    // ------------------------------------------------------------------ allocation core (caller holds every lock)

    private sealed class Ctx
    {
        public required Shipment Shipment { get; init; }
        public required Dictionary<Guid, StockTransfer> Transfers { get; init; }
        public required IReadOnlyDictionary<(Guid LocationId, Guid ProductId), StockItem> Items { get; init; }
        public required Dictionary<Guid, StockReservation> Reservations { get; init; }
    }

    private async Task<Ctx> PrepareAsync(
        Shipment shipment, IEnumerable<Guid> transferIds, IEnumerable<Guid> productIds, IEnumerable<Guid> lineIds, CancellationToken ct,
        IEnumerable<Guid>? extraLocations = null)
    {
        var tids = transferIds.Distinct().Order().ToList();
        foreach (var tid in tids) await StockLocks.LockTransferAsync(db, tid, ct);

        // The Branch filter would hide a request of another creating branch; the planner's authority is the source-location fit already checked.
        var transfers = await db.StockTransfers.IgnoreQueryFilters([BranchQueryFilter.Name]).Include(t => t.Lines)
            .Where(t => tids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        var pids = productIds.Distinct().ToList();
        await StockLocks.LockProductsKeyShareAsync(db, pids, ct);
        await StockLocks.LockLocationsKeyShareAsync(db, [shipment.SourceLocationId, .. extraLocations ?? []], ct);
        var items = await StockLocks.EnsureAndLockStockItemsAsync(db, pids.Select(p => (shipment.SourceLocationId, p)), ct);

        var lids = lineIds.Distinct().ToList();
        var reservations = lids.Count == 0
            ? new Dictionary<Guid, StockReservation>()
            : await db.StockReservations.Where(r => lids.Contains(r.ConsignmentLineId) && r.Status == StockReservationStatus.Active).ToDictionaryAsync(r => r.ConsignmentLineId, ct);

        return new Ctx { Shipment = shipment, Transfers = transfers, Items = items, Reservations = reservations };
    }

    // limits: base <= request remaining (Requested - Closed - Dispatched - (Allocated - Dispatched)) and <= available (on hand - reserved, under the lock).
    private async Task EnsureCapacityAsync(Ctx ctx, StockTransferLine requestLine, Guid sourceLocation, decimal baseQty, Product product, string field, CancellationToken ct)
    {
        var remaining = requestLine.RequestedBaseQty - requestLine.ClosedBaseQty - requestLine.DispatchedBaseQty - (requestLine.AllocatedBaseQty - requestLine.DispatchedBaseQty);
        var unitCode = product.BaseUnit.ShortCode;
        if (baseQty > remaining)
        {
            throw new DomainValidationException(field, $"Only {StockQuantity.Format(Math.Max(0, remaining))} {unitCode} of this request line remains to be allocated.");
        }

        var shortage = await StockAvailability.FindShortageAsync(db, ctx.Items, [new StockAvailability.Requirement(sourceLocation, product.Id, baseQty)], ct);
        if (shortage is { } s)
        {
            throw new DomainValidationException(field, $"Only {StockQuantity.Format(Math.Max(0, s.Available))} {unitCode} of {product.Name} is available at the source (stock on hand less reserved).");
        }
    }

    private static void IncreaseLine(Ctx ctx, ConsignmentLine line, decimal newEntered, decimal newBase, DateTimeOffset now)
    {
        var delta = newBase - line.AllocatedBaseQty;
        var item = ctx.Items[(ctx.Shipment.SourceLocationId, line.ProductId)];
        var requestLine = ctx.Transfers[line.StockTransferId].Lines.Single(l => l.Id == line.StockTransferLineId);

        item.ReservedQuantity += delta;
        requestLine.AllocatedBaseQty += delta;
        ctx.Reservations[line.Id].BaseQty = newBase;
        line.EnteredQty = newEntered;
        line.AllocatedBaseQty = newBase;
        line.UpdatedAt = now;
    }

    /// <summary>Reduces a line's allocation to <paramref name="newEntered"/> (0 cancels the line and releases its reservation). StockItem first, then
    /// the reservation, then the request line; Loaded is zeroed on cancel only when <paramref name="force"/> (shipment/consignment cancel, release).</summary>
    private void ReduceLine(Ctx ctx, ConsignmentLine line, decimal newEntered, string? releaseReason, Guid userId, DateTimeOffset now, bool force = false)
    {
        var newBase = newEntered * line.ConversionFactorToBase;
        var delta = line.AllocatedBaseQty - newBase;
        var item = ctx.Items[(ctx.Shipment.SourceLocationId, line.ProductId)];
        var requestLine = ctx.Transfers[line.StockTransferId].Lines.Single(l => l.Id == line.StockTransferLineId);
        var reservation = ctx.Reservations.GetValueOrDefault(line.Id)
            ?? throw new InvalidOperationException($"Consignment line {line.Id} has no active reservation.");

        if (delta > item.ReservedQuantity || delta > requestLine.AllocatedBaseQty)
        {
            throw new InvalidOperationException($"Reservation bookkeeping for consignment line {line.Id} is inconsistent (reserved {item.ReservedQuantity}, request allocated {requestLine.AllocatedBaseQty}, releasing {delta}).");
        }

        item.ReservedQuantity -= delta;
        requestLine.AllocatedBaseQty -= delta;

        if (newEntered == 0)
        {
            reservation.Status = StockReservationStatus.Released;
            reservation.ReleasedAt = now;
            reservation.ReleasedByUserId = userId;
            reservation.ReleaseReason = releaseReason;
            line.Status = ConsignmentLineStatus.Cancelled;
            line.CancelledAt = now;
            line.EnteredQty = 0;
            line.AllocatedBaseQty = 0;
            line.LoadedBaseQty = 0;
            ctx.Reservations.Remove(line.Id);
        }
        else
        {
            reservation.BaseQty = newBase;
            line.EnteredQty = newEntered;
            line.AllocatedBaseQty = newBase;
        }
        line.UpdatedAt = now;
    }

    private void CancelConsignmentCore(Ctx ctx, Consignment consignment, string releaseReason, string reason, Guid userId, DateTimeOffset now)
    {
        foreach (var line in consignment.Lines.Where(l => l.Status == ConsignmentLineStatus.Active).ToList())
        {
            ReduceLine(ctx, line, 0m, releaseReason, userId, now, force: true);
        }
        consignment.Status = ConsignmentStatus.Cancelled;
        consignment.LoadedAt = null;
        consignment.LoadedByUserId = null;
        consignment.CancelledAt = now;
        consignment.CancelledByUserId = userId;
        consignment.CancellationReason = reason;
        consignment.UpdatedAt = now;
        consignment.UpdatedByUserId = userId;
    }

    // ------------------------------------------------------------------ status recomputation (pure functions of the plan + loading)

    private static bool LegSatisfied(Shipment shipment, Consignment c) =>
        shipment.TransportVia == TransportVia.OwnFleet ? c.ShipmentVehicleId is not null : !string.IsNullOrWhiteSpace(shipment.ProviderName);

    /// <summary>FI-D29 per-mode reference rule: Road = a vehicle number (a vehicle on the shipment, external or own) or a reference; every other mode needs its reference.</summary>
    public static bool ReferenceValid(Shipment shipment) =>
        !string.IsNullOrWhiteSpace(shipment.TransportReference)
        || (shipment.TransportMode == TransportMode.Road && shipment.Vehicles.Count > 0);

    private static void RecomputeConsignment(Shipment shipment, Consignment c, Guid userId, DateTimeOffset now)
    {
        if (c.Status == ConsignmentStatus.Cancelled) return;
        var active = c.Lines.Where(l => l.Status == ConsignmentLineStatus.Active).ToList();
        var full = active.Count > 0 && active.All(l => l.LoadedBaseQty == l.AllocatedBaseQty);
        if (full && LegSatisfied(shipment, c))
        {
            if (c.Status != ConsignmentStatus.Loaded)
            {
                c.Status = ConsignmentStatus.Loaded;
                c.LoadedAt = now;
                c.LoadedByUserId = userId;
            }
        }
        else if (c.Status == ConsignmentStatus.Loaded)
        {
            c.Status = ConsignmentStatus.Planned;
            c.LoadedAt = null;
            c.LoadedByUserId = null;
        }
    }

    private static void RecomputeShipment(Shipment s, Guid userId, DateTimeOffset now)
    {
        if (s.Status is ShipmentStatus.Cancelled) return;
        foreach (var c in s.Consignments) RecomputeConsignment(s, c, userId, now);

        var live = s.Consignments.Where(c => c.Status != ConsignmentStatus.Cancelled && c.Lines.Any(l => l.Status == ConsignmentLineStatus.Active)).ToList();
        var anyLoaded = live.Any(c => c.Status == ConsignmentStatus.Loaded || c.Lines.Any(l => l.Status == ConsignmentLineStatus.Active && l.LoadedBaseQty > 0));
        var all = live.Count > 0 && live.All(c => c.Status == ConsignmentStatus.Loaded);

        s.Status = all && ReferenceValid(s) ? ShipmentStatus.ReadyToDispatch : anyLoaded ? ShipmentStatus.Loading : ShipmentStatus.Draft;
    }

    // ------------------------------------------------------------------ loading the aggregate

    private IQueryable<Shipment> Graph() => db.Shipments
        .Include(s => s.Vehicles)
        .Include(s => s.Consignments).ThenInclude(c => c.Lines)
        .AsSplitQuery();

    private async Task<Shipment?> LoadGraphAsync(Guid id, CancellationToken ct)
    {
        var shipment = await Graph().SingleOrDefaultAsync(s => s.Id == id, ct);
        return shipment is null ? null : Finish(shipment);
    }

    // Header FOR UPDATE, then the tracked graph (the Branch filter hides shipments of other source branches: null = 404).
    private async Task<Shipment?> LockAndLoadAsync(Guid id, CancellationToken ct)
    {
        if (!await StockLocks.LockShipmentAsync(db, id, ct)) return null;
        var shipment = await Graph().SingleOrDefaultAsync(s => s.Id == id, ct);
        return shipment is null ? null : Finish(shipment);
    }

    private static Shipment Finish(Shipment shipment)
    {
        shipment.Vehicles.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        shipment.Consignments.Sort((a, b) => string.CompareOrdinal(a.ConsignmentNo, b.ConsignmentNo));
        foreach (var c in shipment.Consignments) c.Lines.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        return shipment;
    }

    private static Consignment FindConsignment(Shipment shipment, Guid consignmentId)
    {
        var consignment = shipment.Consignments.SingleOrDefault(c => c.Id == consignmentId)
            ?? throw new DomainValidationException("consignmentId", "This consignment is not on the shipment.");
        if (consignment.Status == ConsignmentStatus.Cancelled) throw new DomainValidationException("consignmentId", "This consignment is cancelled.");
        return consignment;
    }

    private static (Consignment Consignment, ConsignmentLine Line) FindActiveLine(Shipment shipment, Guid lineId)
    {
        foreach (var c in shipment.Consignments.Where(c => c.Status != ConsignmentStatus.Cancelled))
        {
            if (c.Lines.SingleOrDefault(l => l.Id == lineId && l.Status == ConsignmentLineStatus.Active) is { } line) return (c, line);
        }
        throw new DomainValidationException("consignmentLineId", "This is not an active allocation of the shipment.");
    }

    private static void EnsureStatus(Shipment shipment, params ShipmentStatus[] allowed)
    {
        if (!allowed.Contains(shipment.Status))
        {
            throw new DomainValidationException("status", $"This shipment is {shipment.Status}; the action needs it to be {string.Join(" or ", allowed)}.");
        }
    }

    private static DomainValidationException ReplayHidden() =>
        new("IdempotencyKey", "This request was already processed, but its result is no longer visible with your current branch access.");

    private static void Touch(Shipment s, Guid userId, DateTimeOffset now)
    {
        s.UpdatedAt = now;
        s.UpdatedByUserId = userId;
    }

    private static void Touch(Consignment c, Guid userId, DateTimeOffset now)
    {
        c.UpdatedAt = now;
        c.UpdatedByUserId = userId;
    }

    // ------------------------------------------------------------------ validation helpers

    private static string? Text(string? value, string field, bool required, int max = MaxText)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text is null && required) throw new DomainValidationException(field, field == "reason" ? "Enter a reason." : "This field is required.");
        if (text is { } t && t.Length > max) throw new DomainValidationException(field, $"Use at most {max} characters.");
        return text;
    }

    private static (string? Provider, string? Contact, string? Reference, string? Notes) NormalizeHeader(
        TransportMode mode, TransportVia via, string? provider, string? contact, string? reference, string? notes)
    {
        if (!Enum.IsDefined(mode)) throw new DomainValidationException("transportMode", "Select a transport mode.");
        if (!Enum.IsDefined(via)) throw new DomainValidationException("transportVia", "Select own fleet or an external provider.");

        var providerName = Text(provider, "providerName", false);
        var providerContact = Text(contact, "providerContact", false);
        var transportReference = Text(reference, "transportReference", false, 100);
        var note = Text(notes, "notes", false, 500);

        if (via == TransportVia.ExternalProvider && providerName is null)
        {
            throw new DomainValidationException("providerName", "Enter the transport provider's name.");
        }
        if (via == TransportVia.OwnFleet)
        {
            providerName = null;
            providerContact = null;
        }

        // Road references come from the vehicles (or an optional reference); every other mode is identified by its document number.
        if (mode != TransportMode.Road && transportReference is null)
        {
            throw new DomainValidationException("transportReference", mode switch
            {
                TransportMode.Air => "Enter the air waybill (AWB) number.",
                TransportMode.Sea => "Enter the bill of lading number.",
                TransportMode.Rail => "Enter the railway receipt number.",
                TransportMode.Courier => "Enter the courier tracking number.",
                _ => "Enter the name of the person carrying the goods.",
            });
        }
        return (providerName, providerContact, transportReference, note);
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

    private static ProductUnit RequireUnit(IReadOnlyDictionary<Guid, ProductUnit> units, Guid productUnitId, Guid productId, string field, bool requireActive)
    {
        if (!units.TryGetValue(productUnitId, out var unit)) throw new DomainValidationException(field, "Select a valid unit.");
        if (unit.ProductId != productId) throw new DomainValidationException(field, "This unit does not belong to this product.");
        if (requireActive && !unit.IsActive) throw new DomainValidationException(field, $"{unit.Label ?? unit.Unit.Name} is no longer active.");
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

    // ------------------------------------------------------------------ source location and fit (same rules as the transfer service, source side)

    private async Task<(StockLocation Location, Guid? EffectiveBranchId)> ResolveSourceAsync(Guid id, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var location = await db.StockLocations.AsNoTracking().SingleOrDefaultAsync(l => l.CompanyId == companyId && l.Id == id, ct);
        if (location is null || !location.IsActive || location.LocationType == StockLocationType.Transit)
        {
            throw new DomainValidationException(SourceField, InvalidLocation);
        }

        var effectiveBranch = location.BranchId;
        if (location.LocationType == StockLocationType.Vehicle)
        {
            var vehicle = await db.Vehicles.AsNoTracking()
                .Where(v => v.CompanyId == companyId && v.StockLocationId == location.Id)
                .Select(v => new { v.AssignedBranchId, v.IsActive })
                .SingleOrDefaultAsync(ct);
            if (vehicle is null || !vehicle.IsActive) throw new DomainValidationException(SourceField, InvalidLocation);
            effectiveBranch ??= vehicle.AssignedBranchId;
        }
        return (location, effectiveBranch);
    }

    private async Task<StockLocation> LoadSourceAsync(Shipment shipment, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        return await db.StockLocations.AsNoTracking().SingleAsync(l => l.CompanyId == companyId && l.Id == shipment.SourceLocationId, ct);
    }

    private async Task EnsureLocationActiveAsync(Guid locationId, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var active = await db.StockLocations.AsNoTracking().Where(l => l.CompanyId == companyId && l.Id == locationId).Select(l => (bool?)l.IsActive).SingleOrDefaultAsync(ct);
        if (active != true) throw new DomainValidationException("stockTransferLineId", "The request's destination location is no longer active.");
    }

    // D7: the permission held company-wide opens the source (within the membership's branches); otherwise the working branch must fit the source.
    private async Task EnsureFitAsync(string permissionCode, StockLocation source, CancellationToken ct)
    {
        if (await StockLocationRules.HasCompanyLevelAsync(permissions, permissionCode, ct))
        {
            if (!await IsAccessibleAsync(source, ct)) throw new DomainValidationException(SourceField, InvalidLocation);
            return;
        }

        var branch = await BranchRules.ResolveWorkingBranchAsync(db, ct);
        await StockLocationRules.EnsureFitsAsync(db, source, branch.Id, false, ct);
    }

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
        // FI-D32 amendment: a company-level source (no owning branch) needs an all-branch membership (handled above), never a narrowed one.
        return owner is { } b && db.BranchContext.CanAccess(b);
    }

    // ------------------------------------------------------------------ audit

    private void Audit(Guid userId, string? correlationId, string action, string target, Guid targetId, string? reason, object? before, object? after) =>
        AuditLog.Append(db, userId, correlationId, action, target, targetId, reason, before, after);

    private static object Snapshot(Shipment s) => new
    {
        shipmentNo = s.ShipmentNo,
        status = s.Status.ToString(),
        sourceLocationId = s.SourceLocationId,
        transportMode = s.TransportMode.ToString(),
        transportVia = s.TransportVia.ToString(),
        providerName = s.ProviderName,
        transportReference = s.TransportReference,
        plannedDispatchDate = s.PlannedDispatchDate,
    };
}
