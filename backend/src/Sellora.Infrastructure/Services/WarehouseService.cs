using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Warehouse writes (Phase 4). Code, name, branch and active flag live on the StockLocation; the Warehouse row carries identity and
/// ownership kind. Every write runs under the company-row lock (shared with BranchService) so code/name checks and the
/// branch-active rule cannot race branch creation or deactivation.
/// </summary>
public sealed partial class WarehouseService(SelloraDbContext db, IStockLocationUsage usage) : IWarehouseService
{
    public async Task<Warehouse> CreateAsync(CreateWarehouseInput input, Guid userId, CancellationToken ct = default)
    {
        var code = (input.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(code) || code.Length > 20)
        {
            throw new DomainValidationException("Code", "Code must be 1 to 20 letters or digits, optionally separated by single hyphens.");
        }
        var name = RequireName(input.Name);

        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var companyId = await CompanyLocks.LockCurrentCompanyAsync(db, ct);

        if (input.BranchId is { } branchId)
        {
            var branch = await db.Branches.AsNoTracking().SingleOrDefaultAsync(b => b.CompanyId == companyId && b.Id == branchId, ct);
            if (branch is null) throw new DomainValidationException("BranchId", "Branch not found.");
            if (!branch.IsActive) throw new DomainValidationException("BranchId", "The branch is inactive.");
        }

        if (await db.StockLocations.AnyAsync(l => l.CompanyId == companyId && l.Code == code, ct))
        {
            throw new DomainValidationException("Code", "This code is already used by a stock location.");
        }
        await EnsureNameFreeAsync(companyId, name, Guid.Empty, ct);

        var now = DateTimeOffset.UtcNow;
        var type = input.BranchId is null ? StockLocationType.CompanyWarehouse : StockLocationType.BranchWarehouse;
        var location = new StockLocation
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            BranchId = input.BranchId,
            LocationType = type,
            Code = code,
            Name = name,
            IsActive = true,
            CreatedAt = now,
            CreatedByUserId = userId,
        };
        var warehouse = new Warehouse
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            StockLocationId = location.Id,
            LocationType = type,
            CreatedAt = now,
            CreatedByUserId = userId,
        };
        db.StockLocations.Add(location);
        db.Warehouses.Add(warehouse);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return warehouse;
    }

    public async Task<Warehouse?> UpdateAsync(Guid warehouseId, UpdateWarehouseInput input, Guid userId, CancellationToken ct = default)
    {
        var name = RequireName(input.Name);

        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var companyId = await CompanyLocks.LockCurrentCompanyAsync(db, ct);

        var warehouse = await db.Warehouses.SingleOrDefaultAsync(w => w.CompanyId == companyId && w.Id == warehouseId, ct);
        if (warehouse is null) return null;
        var location = await RequireLocationAsync(companyId, warehouse, ct);

        await EnsureNameFreeAsync(companyId, name, warehouse.Id, ct);

        var now = DateTimeOffset.UtcNow;
        location.Name = name;
        location.UpdatedAt = now;
        location.UpdatedByUserId = userId;
        warehouse.UpdatedAt = now;
        warehouse.UpdatedByUserId = userId;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return warehouse;
    }

    public async Task<Warehouse?> SetStatusAsync(Guid warehouseId, bool isActive, string? reason, Guid userId, CancellationToken ct = default)
    {
        var note = AuditLog.RequireReason(reason);
        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var companyId = await CompanyLocks.LockCurrentCompanyAsync(db, ct);

        var warehouse = await db.Warehouses.SingleOrDefaultAsync(w => w.CompanyId == companyId && w.Id == warehouseId, ct);
        if (warehouse is null) return null;
        var location = await RequireLocationAsync(companyId, warehouse, ct);

        if (location.IsActive != isActive)
        {
            if (!isActive)
            {
                if ((await usage.GetAsync(location.Id, ct)).InUse)
                {
                    throw new DomainValidationException("IsActive", "This warehouse still holds stock or has open transfers. Transfer, return or reconcile its stock first.");
                }
            }
            else if (location.BranchId is { } branchId
                && !await db.Branches.AnyAsync(b => b.CompanyId == companyId && b.Id == branchId && b.IsActive, ct))
            {
                throw new DomainValidationException("IsActive", "The warehouse's branch is inactive. Reactivate the branch first.");
            }

            var now = DateTimeOffset.UtcNow;
            AuditLog.Append(db, userId, null, AuditActions.WarehouseStatusChanged, AuditTargets.Warehouse, warehouse.Id, note, new { isActive = location.IsActive }, new { isActive });
            location.IsActive = isActive;
            location.UpdatedAt = now;
            location.UpdatedByUserId = userId;
            warehouse.UpdatedAt = now;
            warehouse.UpdatedByUserId = userId;
            await db.SaveChangesAsync(ct);
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return warehouse;
    }

    // Names are unique per company among warehouses (case-insensitive). The name lives on the location, so join the two roots explicitly.
    private async Task EnsureNameFreeAsync(Guid companyId, string name, Guid exceptWarehouseId, CancellationToken ct)
    {
        var names = await (
            from w in db.Warehouses.AsNoTracking()
            join l in db.StockLocations.AsNoTracking() on w.StockLocationId equals l.Id
            where w.CompanyId == companyId && l.CompanyId == companyId && w.Id != exceptWarehouseId
            select l.Name).ToListAsync(ct);
        if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainValidationException("Name", "A warehouse with this name already exists.");
        }
    }

    private async Task<StockLocation> RequireLocationAsync(Guid companyId, Warehouse warehouse, CancellationToken ct) =>
        await db.StockLocations.SingleOrDefaultAsync(l => l.CompanyId == companyId && l.Id == warehouse.StockLocationId, ct)
        ?? throw new InvalidOperationException($"Warehouse {warehouse.Id} has no stock location.");

    private static string RequireName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length == 0) throw new DomainValidationException("Name", "Warehouse name is required.");
        if (name.Length > 150) throw new DomainValidationException("Name", "Warehouse name can be at most 150 characters.");
        return name;
    }

    [GeneratedRegex("^[A-Z0-9]+(-[A-Z0-9]+)*$")]
    private static partial Regex CodePattern();
}
