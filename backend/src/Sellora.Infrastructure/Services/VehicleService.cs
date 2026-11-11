using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Vehicle writes (Phase 4). The vehicle number is the location code (normalised, immutable); name and active flag are mirrored onto
/// the StockLocation, whose BranchId stays NULL (the assignment lives on the vehicle). Every write runs under the company-row lock.
/// </summary>
public sealed partial class VehicleService(SelloraDbContext db, IStockLocationUsage usage) : IVehicleService
{
    public async Task<Vehicle> CreateAsync(CreateVehicleInput input, Guid userId, CancellationToken ct = default)
    {
        var number = NormalizeNumber(input.VehicleNumber);
        var name = RequireName(input.VehicleName);
        RequireType(input.VehicleType);

        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var companyId = await CompanyLocks.LockCurrentCompanyAsync(db, ct);

        await RequireActiveBranchAsync(companyId, input.AssignedBranchId, ct);
        if (await db.StockLocations.AnyAsync(l => l.CompanyId == companyId && l.Code == number, ct)
            || await db.Vehicles.AnyAsync(v => v.CompanyId == companyId && v.VehicleNumber == number, ct))
        {
            throw new DomainValidationException("VehicleNumber", "This number is already used by a vehicle or another stock location.");
        }

        var now = DateTimeOffset.UtcNow;
        var location = new StockLocation
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            BranchId = null,
            LocationType = StockLocationType.Vehicle,
            Code = number,
            Name = name,
            IsActive = true,
            CreatedAt = now,
            CreatedByUserId = userId,
        };
        var vehicle = new Vehicle
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            StockLocationId = location.Id,
            LocationType = StockLocationType.Vehicle,
            VehicleNumber = number,
            VehicleName = name,
            VehicleType = input.VehicleType,
            AssignedBranchId = input.AssignedBranchId,
            IsActive = true,
            CreatedAt = now,
            CreatedByUserId = userId,
        };
        db.StockLocations.Add(location);
        db.Vehicles.Add(vehicle);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return vehicle;
    }

    public async Task<Vehicle?> UpdateAsync(Guid vehicleId, UpdateVehicleInput input, Guid userId, CancellationToken ct = default)
    {
        var name = RequireName(input.VehicleName);
        RequireType(input.VehicleType);

        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var companyId = await CompanyLocks.LockCurrentCompanyAsync(db, ct);

        var vehicle = await db.Vehicles.SingleOrDefaultAsync(v => v.CompanyId == companyId && v.Id == vehicleId, ct);
        if (vehicle is null) return null;
        var location = await RequireLocationAsync(companyId, vehicle, ct);

        if (vehicle.AssignedBranchId != input.AssignedBranchId)
        {
            await RequireActiveBranchAsync(companyId, input.AssignedBranchId, ct);
            if ((await usage.GetAsync(location.Id, ct)).InUse)
            {
                throw new DomainValidationException("AssignedBranchId", "This vehicle still holds stock or has open transfers. Transfer, return or reconcile its stock before reassigning it.");
            }
            vehicle.AssignedBranchId = input.AssignedBranchId;
        }

        var now = DateTimeOffset.UtcNow;
        vehicle.VehicleName = name;
        vehicle.VehicleType = input.VehicleType;
        vehicle.UpdatedAt = now;
        vehicle.UpdatedByUserId = userId;
        location.Name = name;
        location.UpdatedAt = now;
        location.UpdatedByUserId = userId;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return vehicle;
    }

    public async Task<Vehicle?> SetStatusAsync(Guid vehicleId, bool isActive, string? reason, Guid userId, CancellationToken ct = default)
    {
        var note = AuditLog.RequireReason(reason);
        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var companyId = await CompanyLocks.LockCurrentCompanyAsync(db, ct);

        var vehicle = await db.Vehicles.SingleOrDefaultAsync(v => v.CompanyId == companyId && v.Id == vehicleId, ct);
        if (vehicle is null) return null;
        var location = await RequireLocationAsync(companyId, vehicle, ct);

        if (vehicle.IsActive != isActive || location.IsActive != isActive)
        {
            if (!isActive && (await usage.GetAsync(location.Id, ct)).InUse)
            {
                throw new DomainValidationException("IsActive", "This vehicle still holds stock or has open transfers. Transfer, return or reconcile its stock first.");
            }
            if (isActive && vehicle.AssignedBranchId is { } assigned)
            {
                // Mirrors the warehouse rule: an active vehicle must not be assigned to an inactive branch (O-1).
                var branchActive = await db.Branches.AsNoTracking()
                    .AnyAsync(b => b.CompanyId == companyId && b.Id == assigned && b.IsActive, ct);
                if (!branchActive)
                {
                    throw new DomainValidationException("IsActive", "The vehicle's assigned branch is inactive. Reassign the vehicle or reactivate the branch first.");
                }
            }

            var now = DateTimeOffset.UtcNow;
            AuditLog.Append(db, userId, null, AuditActions.VehicleStatusChanged, AuditTargets.Vehicle, vehicle.Id, note, new { isActive = vehicle.IsActive }, new { isActive });
            vehicle.IsActive = isActive;
            vehicle.UpdatedAt = now;
            vehicle.UpdatedByUserId = userId;
            location.IsActive = isActive;
            location.UpdatedAt = now;
            location.UpdatedByUserId = userId;
            await db.SaveChangesAsync(ct);
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return vehicle;
    }

    private async Task RequireActiveBranchAsync(Guid companyId, Guid? branchId, CancellationToken ct)
    {
        if (branchId is not { } id) return;
        var branch = await db.Branches.AsNoTracking().SingleOrDefaultAsync(b => b.CompanyId == companyId && b.Id == id, ct);
        if (branch is null) throw new DomainValidationException("AssignedBranchId", "Branch not found.");
        if (!branch.IsActive) throw new DomainValidationException("AssignedBranchId", "The branch is inactive.");
    }

    private async Task<StockLocation> RequireLocationAsync(Guid companyId, Vehicle vehicle, CancellationToken ct) =>
        await db.StockLocations.SingleOrDefaultAsync(l => l.CompanyId == companyId && l.Id == vehicle.StockLocationId, ct)
        ?? throw new InvalidOperationException($"Vehicle {vehicle.Id} has no stock location.");

    /// <summary>Trim, upper-case, runs of whitespace/hyphens become one '-', then 1-20 chars of A-Z0-9 and single hyphens.</summary>
    internal static string NormalizeNumber(string? value)
    {
        var number = SeparatorRuns().Replace((value ?? string.Empty).Trim().ToUpperInvariant(), "-");
        if (number.Length == 0 || number.Length > 20 || !NumberPattern().IsMatch(number))
        {
            throw new DomainValidationException("VehicleNumber", "Vehicle number must be 1 to 20 letters or digits, optionally separated by spaces or hyphens.");
        }
        return number;
    }

    private static string RequireName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length == 0) throw new DomainValidationException("VehicleName", "Vehicle name is required.");
        if (name.Length > 150) throw new DomainValidationException("VehicleName", "Vehicle name can be at most 150 characters.");
        return name;
    }

    private static void RequireType(VehicleType type)
    {
        if (!Enum.IsDefined(type)) throw new DomainValidationException("VehicleType", "Unknown vehicle type.");
    }

    [GeneratedRegex(@"[\s\-]+")]
    private static partial Regex SeparatorRuns();

    [GeneratedRegex("^[A-Z0-9]+(-[A-Z0-9]+)*$")]
    private static partial Regex NumberPattern();
}
