using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <remarks>
/// Update and SetActive lock the unit row (<c>FOR UPDATE</c>) inside a transaction before the in-use check, and
/// <see cref="ProductService"/> reads every unit it references <c>FOR SHARE</c> in its own transaction. This serialises the
/// two sides: a product cannot start using a unit while its category/decimals/factor are being changed or it is
/// being deactivated, and a unit cannot be changed between a product's validation and its commit. Unique-index
/// violations from concurrent duplicate creates/renames are mapped back to the duplicate field error.
/// </remarks>
public class UnitService(SelloraDbContext db) : IUnitService
{
    private const decimal MaxStandardFactor = 1_000_000_000m;
    private const int StandardFactorScale = 10;
    private const string DuplicateMessage = "A unit with this name or short code already exists.";
    private static readonly HashSet<string> UniqueIndexes =
        ["IX_Units_Name", "IX_Units_ShortCode", "UX_Units_Name_Lower", "UX_Units_ShortCode_Lower"];

    public async Task<Unit> CreateUnitAsync(UpsertUnitInput input, CancellationToken ct = default)
    {
        var name = input.Name.Trim();
        var shortCode = input.ShortCode.Trim();

        ValidateShape(input);
        await EnsureUniqueAsync(null, name, shortCode, ct);

        var unit = new Unit
        {
            Id = Guid.NewGuid(),
            Name = name,
            ShortCode = shortCode,
            MeasurementCategory = input.MeasurementCategory,
            DecimalPlaces = input.DecimalPlaces,
            StandardFactor = input.StandardFactor,
        };
        db.Units.Add(unit);
        await SaveMappingDuplicatesAsync(ct);
        return unit;
    }

    public async Task<Unit?> UpdateUnitAsync(Guid id, UpsertUnitInput input, CancellationToken ct = default)
    {
        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var unit = await LockUnitAsync(id, ct);
        if (unit is null) return null;

        var name = input.Name.Trim();
        var shortCode = input.ShortCode.Trim();

        ValidateShape(input);
        await EnsureUniqueAsync(id, name, shortCode, ct);

        // Stock and product conversions are interpreted through these attributes, so once a product
        // references the unit they are frozen. Unchanged values resent by the form are accepted.
        var categoryChanged = unit.MeasurementCategory != input.MeasurementCategory;
        var decimalsChanged = unit.DecimalPlaces != input.DecimalPlaces;
        var factorChanged = RoundFactor(unit.StandardFactor) != RoundFactor(input.StandardFactor);
        if ((categoryChanged || decimalsChanged || factorChanged) && await IsInUseAsync(id, ct))
        {
            if (categoryChanged) throw Locked(nameof(input.MeasurementCategory), "category");
            if (decimalsChanged) throw Locked(nameof(input.DecimalPlaces), "decimal places");
            throw Locked(nameof(input.StandardFactor), "standard factor");
        }

        unit.Name = name;
        unit.ShortCode = shortCode;
        unit.MeasurementCategory = input.MeasurementCategory;
        unit.DecimalPlaces = input.DecimalPlaces;
        unit.StandardFactor = input.StandardFactor;
        await SaveMappingDuplicatesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return unit;
    }

    public async Task<Unit?> SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default)
    {
        await using var transaction = await StockLocks.BeginAsync(db, ct);
        var unit = await LockUnitAsync(id, ct);
        if (unit is null) return null;

        if (!isActive && await IsUsedByActiveProductAsync(id, ct))
        {
            throw new DomainValidationException(nameof(Unit.IsActive), "This unit is used by products and cannot be deactivated.");
        }

        unit.IsActive = isActive;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return unit;
    }

    // Units are a platform table shared by every company, so "in use" must look at ALL companies' products and product
    // units: this is one of the three documented IgnoreQueryFilters bypasses (with UnitsController.Project and
    // CurrenciesController.SetActive). Only existence flags leave this method, never another company's data.
    /// <inheritdoc />
    public async Task<bool> IsInUseAsync(Guid id, CancellationToken ct = default) =>
        await db.Products.IgnoreQueryFilters([CompanyQueryFilter.Name]).AnyAsync(p => p.BaseUnitId == id, ct)
        || await db.ProductUnits.IgnoreQueryFilters([CompanyQueryFilter.Name]).AnyAsync(u => u.UnitId == id, ct);

    /// <inheritdoc />
    public async Task<bool> IsUsedByActiveProductAsync(Guid id, CancellationToken ct = default) =>
        await db.Products.IgnoreQueryFilters([CompanyQueryFilter.Name]).AnyAsync(p => p.BaseUnitId == id, ct)
        || await db.ProductUnits.IgnoreQueryFilters([CompanyQueryFilter.Name]).AnyAsync(u => u.UnitId == id && u.IsActive, ct);

    private static void ValidateShape(UpsertUnitInput input)
    {
        if (!Enum.IsDefined(input.MeasurementCategory))
        {
            throw new DomainValidationException(nameof(input.MeasurementCategory), "Select a valid measurement category.");
        }

        if (input.DecimalPlaces is < 0 or > 3)
        {
            throw new DomainValidationException(nameof(input.DecimalPlaces), "Decimal places must be between 0 and 3.");
        }

        var isWholeCategory = input.MeasurementCategory is MeasurementCategory.Count or MeasurementCategory.Packaging;
        if (isWholeCategory && input.DecimalPlaces != 0)
        {
            throw new DomainValidationException(nameof(input.DecimalPlaces), "Count and packaging units are whole numbers.");
        }

        if (input.StandardFactor is { } factor)
        {
            if (isWholeCategory)
            {
                throw new DomainValidationException(nameof(input.StandardFactor), "Only weight, volume and length units have a standard factor.");
            }
            if (factor <= 0 || factor > MaxStandardFactor)
            {
                throw new DomainValidationException(nameof(input.StandardFactor), "Standard factor must be greater than 0 and at most 1,000,000,000.");
            }
            // Stored as numeric(28,10); decimal equality ignores trailing zeros, so this checks significant digits only.
            if (Math.Round(factor, StandardFactorScale) != factor)
            {
                throw new DomainValidationException(nameof(input.StandardFactor), "Standard factor can have at most 10 decimal places.");
            }
        }
    }

    private async Task EnsureUniqueAsync(Guid? selfId, string name, string shortCode, CancellationToken ct)
    {
        var lowerName = name.ToLower();
        var lowerCode = shortCode.ToLower();
        if (await db.Units.AnyAsync(u => (selfId == null || u.Id != selfId)
                && (u.Name.ToLower() == lowerName || u.ShortCode.ToLower() == lowerCode), ct))
        {
            throw new DomainValidationException(nameof(UpsertUnitInput.ShortCode), DuplicateMessage);
        }
    }

    /// <summary>Loads the unit for modification; on a relational provider the row is locked <c>FOR UPDATE</c>
    /// until the surrounding transaction ends (see class remarks).</summary>
    private async Task<Unit?> LockUnitAsync(Guid id, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return await db.Units.SingleOrDefaultAsync(u => u.Id == id, ct);

        var rows = await db.Units.FromSqlInterpolated($"SELECT * FROM \"Units\" WHERE \"Id\" = {id} FOR UPDATE").ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    /// <summary>The pre-check cannot see a concurrent uncommitted insert/rename, so the unique indexes are the
    /// final guard; their violation is reported as the same field error.</summary>
    private async Task SaveMappingDuplicatesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: { } constraint,
        } && UniqueIndexes.Contains(constraint))
        {
            throw new DomainValidationException(nameof(UpsertUnitInput.ShortCode), DuplicateMessage);
        }
    }

    private static decimal? RoundFactor(decimal? value) =>
        value is { } v ? Math.Round(v, StandardFactorScale) : null;

    private static DomainValidationException Locked(string field, string attribute) =>
        new(field, $"This unit is used by products; its {attribute} cannot change. Create a new unit instead.");
}
