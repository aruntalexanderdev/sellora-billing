using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>Per-request id -> label dictionaries and the shipment detail projection shared by the shipment, consignment and reservation controllers.</summary>
internal sealed class ShipmentLookups
{
    private readonly Dictionary<Guid, StockTransferLocationDto> _locations;
    private readonly BranchLabels _branches;
    private Dictionary<Guid, string> _users = [];

    private ShipmentLookups(Dictionary<Guid, StockTransferLocationDto> locations, BranchLabels branches)
    {
        _locations = locations;
        _branches = branches;
    }

    public static async Task<ShipmentLookups> LoadAsync(SelloraDbContext db, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var rows = await db.StockLocations.AsNoTracking().Where(l => l.CompanyId == companyId)
            .Select(l => new { l.Id, l.Code, l.Name, l.LocationType }).ToListAsync(ct);
        return new ShipmentLookups(
            rows.ToDictionary(l => l.Id, l => new StockTransferLocationDto(l.Id, l.Code, l.Name, l.LocationType.ToString())),
            await BranchLabels.LoadAsync(db, ct));
    }

    public async Task LoadUsersAsync(SelloraDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        _users = await db.Users.AsNoTracking().Where(u => list.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName })
            .ToDictionaryAsync(u => u.Id, u => (u.FirstName + " " + u.LastName).Trim(), ct);
    }

    public StockTransferLocationDto Location(Guid id) => _locations.TryGetValue(id, out var d) ? d : new StockTransferLocationDto(id, null, null, null);
    public StockTransferBranchDto Branch(Guid? id) => new(id, _branches.Code(id), _branches.Name(id));
    public string? User(Guid? id) => id is { } x && _users.TryGetValue(x, out var n) ? n : null;
    public StockTransferActorDto? Actor(Guid? id, DateTimeOffset? at) => at is null && id is null ? null : new(id, User(id), at);

    public HashSet<Guid> LocationsMatching(string term) => _locations.Values
        .Where(l => (l.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) || (l.Code?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
        .Select(l => l.Id).ToHashSet();
}

internal sealed record ProductLabel(Guid Id, string Sku, string Name, string BaseUnitCode);

internal static class ShipmentReads
{
    public static async Task<Dictionary<Guid, ProductLabel>> ProductsAsync(SelloraDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Products.AsNoTracking().Where(p => list.Contains(p.Id))
            .Select(p => new ProductLabel(p.Id, p.Sku, p.Name, p.BaseUnit.ShortCode))
            .ToDictionaryAsync(p => p.Id, ct);
    }

    /// <summary>The shipment as a DTO, or null when it does not exist or the Branch filter hides it (source branch scope).</summary>
    public static async Task<ShipmentDto?> LoadShipmentAsync(SelloraDbContext db, Guid id, CancellationToken ct, StockVisibility? visibility = null)
    {
        var s = await db.Shipments.AsNoTracking()
            .Include(x => x.Vehicles).Include(x => x.Consignments).ThenInclude(c => c.Lines).AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return null;
        // FI-D32: reads (not the writer's own response) apply the source-location visibility; a hidden source is a 404.
        if (visibility is not null && !visibility.IsShipmentSourceVisible(s.SourceLocationId)) return null;

        var lookups = await ShipmentLookups.LoadAsync(db, ct);
        await lookups.LoadUsersAsync(db, [s.CreatedByUserId, s.UpdatedByUserId, s.CancelledByUserId, .. s.Consignments.Select(c => c.CancelledByUserId)], ct);

        var lines = s.Consignments.SelectMany(c => c.Lines).ToList();
        var transferIds = lines.Select(l => l.StockTransferId).Distinct().ToList();
        // The planner's authority is the source location; the request's creating branch must not hide its own allocation rows.
        var transfers = await db.StockTransfers.AsNoTracking().IgnoreQueryFilters([BranchQueryFilter.Name])
            .Where(t => transferIds.Contains(t.Id))
            .Select(t => new
            {
                t.Id, t.TransferNo,
                Lines = t.Lines.Select(l => new { l.Id, l.RequestedBaseQty, l.ClosedBaseQty, l.DispatchedBaseQty, l.AllocatedBaseQty }).ToList(),
            }).ToListAsync(ct);
        var transferById = transfers.ToDictionary(t => t.Id);
        var products = await ProductsAsync(db, lines.Select(l => l.ProductId), ct);

        var lineIds = lines.Select(l => l.Id).ToList();
        var reservations = await db.StockReservations.AsNoTracking()
            .Where(r => lineIds.Contains(r.ConsignmentLineId) && r.Status == StockReservationStatus.Active)
            .ToDictionaryAsync(r => r.ConsignmentLineId, ct);

        var ownVehicleIds = s.Vehicles.Where(v => v.VehicleId != null).Select(v => v.VehicleId!.Value).ToList();
        var masters = await db.Vehicles.AsNoTracking().Where(v => ownVehicleIds.Contains(v.Id))
            .Select(v => new { v.Id, v.VehicleNumber, v.VehicleName }).ToDictionaryAsync(v => v.Id, ct);

        var vehicles = s.Vehicles.OrderBy(v => v.Sequence).Select(v =>
        {
            var master = v.VehicleId is { } vid && masters.TryGetValue(vid, out var m) ? m : null;
            return new ShipmentVehicleDto(v.Id, v.Sequence, v.VehicleId, master?.VehicleNumber, master?.VehicleName,
                v.ExternalVehicleNumber, v.ExternalDescription, v.DriverName, v.DriverPhone);
        }).ToList();

        ConsignmentLineDto Line(ConsignmentLine l)
        {
            transferById.TryGetValue(l.StockTransferId, out var t);
            var rl = t?.Lines.SingleOrDefault(x => x.Id == l.StockTransferLineId);
            var request = rl is null
                ? new ConsignmentLineRequestDto(0, 0, 0, 0, 0)
                : new ConsignmentLineRequestDto(rl.RequestedBaseQty, rl.ClosedBaseQty, rl.DispatchedBaseQty, rl.AllocatedBaseQty,
                    Math.Max(0, rl.RequestedBaseQty - rl.ClosedBaseQty - rl.DispatchedBaseQty - (rl.AllocatedBaseQty - rl.DispatchedBaseQty)));
            products.TryGetValue(l.ProductId, out var p);
            reservations.TryGetValue(l.Id, out var res);
            return new ConsignmentLineDto(
                l.Id, l.StockTransferId, t?.TransferNo, l.StockTransferLineId, l.ProductId, p?.Sku, p?.Name, p?.BaseUnitCode,
                l.ProductUnitId, l.UnitCode, l.ConversionFactorToBase, l.EnteredQty, l.AllocatedBaseQty, l.LoadedBaseQty,
                l.ConversionFactorToBase > 0 ? l.LoadedBaseQty / l.ConversionFactorToBase : 0,
                l.Status, request, res?.Id, res?.BaseQty ?? 0);
        }

        var consignments = s.Consignments.OrderBy(c => c.ConsignmentNo, StringComparer.Ordinal).Select(c => new ConsignmentDto(
            c.Id, c.ConsignmentNo, c.Status, lookups.Location(c.DestinationLocationId), lookups.Branch(c.DestinationBranchId), c.ShipmentVehicleId,
            c.LoadedAt, c.CancelledAt is null ? null : lookups.Actor(c.CancelledByUserId, c.CancelledAt), c.CancellationReason,
            c.Lines.OrderBy(l => l.CreatedAt).Select(Line).ToList())).ToList();

        return new ShipmentDto(
            s.Id, s.ShipmentNo, s.Status, s.TransportMode, s.TransportVia, s.ProviderName, s.ProviderContact, s.TransportReference,
            Sellora.Infrastructure.Services.ShipmentService.ReferenceValid(s), s.PlannedDispatchDate, s.Notes,
            lookups.Branch(s.BranchId), lookups.Location(s.SourceLocationId), vehicles, consignments,
            lookups.Actor(s.CreatedByUserId, s.CreatedAt)!, s.UpdatedAt is null ? null : lookups.Actor(s.UpdatedByUserId, s.UpdatedAt),
            s.CancelledAt is null ? null : lookups.Actor(s.CancelledByUserId, s.CancelledAt), s.CancellationReason);
    }
}
