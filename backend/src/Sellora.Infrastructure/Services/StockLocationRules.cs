using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>What a posting does at its stock location; selects the type capability and the company-level permission (Phase 8 D3).</summary>
internal enum PostingKind { Sale, Purchase, Adjustment }

/// <summary>
/// Phase 8b: resolves and validates the one stock location a new sale, purchase or adjustment moves stock at. The request may
/// name a location; without one the working branch's store is used. Failures are 400 on <see cref="Field"/> with one message for
/// unknown, foreign, inaccessible and inactive locations (no existence leak, like BranchRules). Call <see cref="ResolveAsync"/>
/// before any lock and <see cref="ResolveAsync"/> again after the location KEY SHARE lock: the second call re-reads the row and
/// re-checks IsActive under the lock, so a deactivation that committed in between is seen. Returns and cancellations do not use
/// this class; they post at the original document's location.
/// </summary>
internal static class StockLocationRules
{
    public const string Field = "stockLocationId";
    private const string Generic = "Select a valid, active stock location.";

    public static async Task<Guid> ResolveAsync(
        SelloraDbContext db, PostingKind kind, Guid? requested, Guid workingBranchId, bool companyLevelPermission, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();

        StockLocation? location;
        if (requested is { } id)
        {
            location = await db.StockLocations.AsNoTracking().SingleOrDefaultAsync(l => l.CompanyId == companyId && l.Id == id, ct);
        }
        else
        {
            location = await db.StockLocations.AsNoTracking().SingleOrDefaultAsync(
                l => l.CompanyId == companyId && l.BranchId == workingBranchId && l.LocationType == StockLocationType.BranchStore, ct);
            if (location is null) throw new InvalidOperationException("The working branch has no store location.");
        }

        if (location is null || !location.IsActive || location.LocationType == StockLocationType.Transit) throw Invalid();

        // Capability by type.
        var capable = kind switch
        {
            PostingKind.Sale => StockLocationTypeRules.CanSell(location.LocationType),
            PostingKind.Purchase => StockLocationTypeRules.CanReceivePurchase(location.LocationType),
            _ => true,
        };
        if (!capable)
        {
            throw new DomainValidationException(Field, kind == PostingKind.Sale
                ? "Sales cannot be made from this type of location."
                : "Purchases cannot be received into this type of location.");
        }

        await EnsureFitsAsync(db, location, workingBranchId, companyLevelPermission, ct);
        return location.Id;
    }

    private const string RestockInactive = "The sale's stock location is inactive. Reactivate the location first.";
    private const string RestockReassigned = "The sale's vehicle has been reassigned to another branch. Reassign it back first, or record a sales return instead.";

    /// <summary>Phase 10 D1: where a sales return (or cancellation) puts goods back. The invoice location when it is active and
    /// still the invoice branch's (branch-owned location of that branch, a vehicle currently assigned to it, or a central
    /// vehicle). Otherwise a return from a vehicle falls back to the invoice branch's active store; any other case, and every
    /// cancellation (exact undo), fails with 400 on <see cref="Field"/>. Re-read on every call, so calling it again after the
    /// location KEY SHARE lock sees a deactivation or reassignment that committed in between.</summary>
    public static async Task<Guid> ResolveRestockAsync(SelloraDbContext db, SalesInvoice invoice, bool forCancel, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var location = await db.StockLocations.AsNoTracking().SingleOrDefaultAsync(l => l.CompanyId == companyId && l.Id == invoice.StockLocationId, ct);
        if (location is not null && location.IsActive && await BelongsToBranchAsync(db, location, invoice.BranchId, ct)) return location.Id;

        if (!forCancel && location is { LocationType: StockLocationType.Vehicle })
        {
            var store = await db.StockLocations.AsNoTracking().SingleOrDefaultAsync(
                l => l.CompanyId == companyId && l.BranchId == invoice.BranchId && l.LocationType == StockLocationType.BranchStore && l.IsActive, ct);
            if (store is not null) return store.Id;
        }
        throw new DomainValidationException(Field, location is { IsActive: true } ? RestockReassigned : RestockInactive);
    }

    /// <summary>The locations <see cref="ResolveRestockAsync"/> may pick (ignoring active flags), so the caller can lock them
    /// all in id order before the re-check.</summary>
    public static async Task<IReadOnlyList<Guid>> RestockCandidatesAsync(SelloraDbContext db, SalesInvoice invoice, bool forCancel, CancellationToken ct)
    {
        var ids = new List<Guid> { invoice.StockLocationId };
        if (forCancel) return ids;
        var companyId = db.CompanyContext.RequireCompanyId();
        var store = await db.StockLocations.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.BranchId == invoice.BranchId && l.LocationType == StockLocationType.BranchStore)
            .Select(l => (Guid?)l.Id).SingleOrDefaultAsync(ct);
        if (store is { } s) ids.Add(s);
        return ids;
    }

    private static async Task<bool> BelongsToBranchAsync(SelloraDbContext db, StockLocation location, Guid branchId, CancellationToken ct)
    {
        if (location.BranchId is { } owner) return owner == branchId;
        if (location.LocationType != StockLocationType.Vehicle) return false;
        var companyId = db.CompanyContext.RequireCompanyId();
        var vehicle = await db.Vehicles.AsNoTracking().Where(v => v.CompanyId == companyId && v.StockLocationId == location.Id)
            .Select(v => new { v.AssignedBranchId, v.IsActive }).SingleOrDefaultAsync(ct);
        return vehicle is { IsActive: true } && (vehicle.AssignedBranchId is null || vehicle.AssignedBranchId == branchId);
    }

    /// <summary>Phase 10 D2: the location a purchase return issues from (<paramref name="requested"/> ?? the purchase's own).
    /// Active, not Transit, same company; must fit the purchase's branch (<see cref="EnsureFitsAsync"/> with the company-level
    /// <c>Purchase.Return</c> grant). Another branch's location (or vehicle) is allowed only with that grant company-wide AND
    /// when the location's effective branch is accessible to the caller (mirrors the Phase 9 transfer Resolve). Call again after
    /// the location KEY SHARE lock to re-check under the lock.</summary>
    public static async Task<Guid> ResolveReturnSourceAsync(
        SelloraDbContext db, Guid? requested, Guid purchaseLocationId, Guid purchaseBranchId, bool companyLevelPermission, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var id = requested ?? purchaseLocationId;
        var location = await db.StockLocations.AsNoTracking().SingleOrDefaultAsync(l => l.CompanyId == companyId && l.Id == id, ct);
        if (location is null || !location.IsActive || location.LocationType == StockLocationType.Transit) throw Invalid();

        if (await FitsAsync(db, location, purchaseBranchId, companyLevelPermission, ct)) return location.Id;

        if (companyLevelPermission)
        {
            var owner = location.BranchId;
            var vehicleActive = true;
            if (owner is null && location.LocationType == StockLocationType.Vehicle)
            {
                var v = await db.Vehicles.AsNoTracking().Where(x => x.CompanyId == companyId && x.StockLocationId == location.Id)
                    .Select(x => new { x.AssignedBranchId, x.IsActive }).SingleOrDefaultAsync(ct);
                owner = v?.AssignedBranchId;
                vehicleActive = v is { IsActive: true };
            }
            if (vehicleActive && owner is { } b && b != purchaseBranchId && db.BranchContext.CanAccess(b)) return location.Id;
        }

        // Not usable: throw the natural failure of the branch-fit rules.
        await EnsureFitsAsync(db, location, purchaseBranchId, companyLevelPermission, ct);
        throw Invalid();
    }

    /// <summary>Branch fit of an already loaded, active location for a working branch (arch 13.2): a branch-owned location must
    /// be the working branch's own; a vehicle assigned to the working branch is that branch's to use, one assigned to another
    /// branch never fits (no company-level grant opens it, D3); any other company-level location needs
    /// <paramref name="companyLevelPermission"/>. Throws on <see cref="Field"/>. Shared by the resolver and the transfer service.</summary>
    public static async Task EnsureFitsAsync(
        SelloraDbContext db, StockLocation location, Guid workingBranchId, bool companyLevelPermission, CancellationToken ct)
    {
        if (await FitFailureAsync(db, location, workingBranchId, companyLevelPermission, ct) is { } failure) throw failure;
    }

    /// <summary>Same rules as <see cref="EnsureFitsAsync"/>, as a yes/no (the transfer service tests source OR destination).</summary>
    public static async Task<bool> FitsAsync(
        SelloraDbContext db, StockLocation location, Guid workingBranchId, bool companyLevelPermission, CancellationToken ct) =>
        await FitFailureAsync(db, location, workingBranchId, companyLevelPermission, ct) is null;

    private static async Task<DomainValidationException?> FitFailureAsync(
        SelloraDbContext db, StockLocation location, Guid workingBranchId, bool companyLevelPermission, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();

        // Branch fit: a branch-owned location must be the working branch's own (arch 13.2: no picking another branch's location).
        if (location.BranchId is { } owner)
        {
            return owner != workingBranchId || !db.BranchContext.CanAccess(owner) ? Invalid() : null;
        }

        // Company-level location. A vehicle assigned to the working branch is that branch's to use; a central vehicle or a
        // company warehouse needs the permission at company level (O2).
        if (location.LocationType == StockLocationType.Vehicle)
        {
            var assigned = await db.Vehicles.AsNoTracking()
                .Where(v => v.CompanyId == companyId && v.StockLocationId == location.Id)
                .Select(v => new { v.AssignedBranchId, v.IsActive })
                .SingleOrDefaultAsync(ct);
            if (assigned is null || !assigned.IsActive) return Invalid();
            if (assigned.AssignedBranchId == workingBranchId) return null;
            // Another branch's vehicle is that branch's location (arch 13.2): no company-level grant opens it (D3).
            if (assigned.AssignedBranchId is not null) return Invalid();
        }

        return companyLevelPermission
            ? null
            : new DomainValidationException(Field, "This location is company-level; you need the permission company-wide to use it.");
    }

    /// <summary>True when <paramref name="code"/> is held company-wide. Without a permission service the permission is
    /// treated as not held (fail closed); the API always registers one.</summary>
    public static async Task<bool> HasCompanyLevelAsync(IEffectivePermissionService? permissions, string code, CancellationToken ct) =>
        permissions is not null && (await permissions.GetCurrentAsync(ct)).HasCompanyLevel(code);

    private static DomainValidationException Invalid() => new(Field, Generic);
}
