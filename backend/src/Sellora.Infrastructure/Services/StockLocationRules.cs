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
