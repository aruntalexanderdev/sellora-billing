using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>Tax components (BR1, BR6). Unique code and name per company and country.</summary>
public class TaxComponentService(SelloraDbContext db, TimeProvider? time = null) : ITaxComponentService
{
    private const string CodeIndex = "IX_TaxComponents_CompanyId_CountryId_Code";
    private const string NameIndex = "UX_TaxComponents_Country_Name_Lower";

    public async Task<TaxComponent> CreateAsync(UpsertTaxComponentInput input, CancellationToken ct = default)
    {
        var code = TaxRules.NormaliseCode(input.Code);
        var name = TaxRules.NormaliseName(input.Name);
        ValidateEnums(input.Kind, input.Method);
        if (!await db.Countries.AnyAsync(c => c.Id == input.CountryId, ct)) throw new DomainValidationException("countryId", "Select a valid country.");
        await EnsureUniqueAsync(null, input.CountryId, code, name, ct);

        var component = new TaxComponent
        {
            Id = Guid.NewGuid(), CountryId = input.CountryId, Code = code, Name = name, Kind = input.Kind, Method = input.Method,
            IsActive = true, CreatedAt = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime,
        };
        db.TaxComponents.Add(component);
        await SaveAsync(ct);
        return component;
    }

    public async Task<TaxComponent?> UpdateAsync(Guid id, UpdateTaxComponentInput input, CancellationToken ct = default)
    {
        var component = await db.TaxComponents.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (component is null) return null;

        var code = TaxRules.NormaliseCode(input.Code);
        var name = TaxRules.NormaliseName(input.Name);
        ValidateEnums(input.Kind, input.Method);
        await EnsureUniqueAsync(id, component.CountryId, code, name, ct);

        if ((input.Method != component.Method || input.Kind != component.Kind) && await IsUsedAsync(id, ct))
        {
            throw new DomainValidationException(input.Method != component.Method ? "method" : "kind",
                "This component is used by a tax profile version, so its kind and calculation method cannot change. Create a new component instead.");
        }

        if (input.Method != component.Method)
        {
            // Method is part of the alternate key the version components reference (their rate must match it), so EF cannot change it in place. Nothing uses the
            // component (checked above), so it is replaced by a new row with a new id, in one transaction (delete first: the unique code).
            await using var transaction = await StockLocks.BeginAsync(db, ct);
            var replacement = new TaxComponent
            {
                Id = Guid.NewGuid(), CountryId = component.CountryId, Code = code, Name = name, Kind = input.Kind, Method = input.Method,
                IsActive = component.IsActive, CreatedAt = component.CreatedAt,
            };
            db.TaxComponents.Remove(component);
            await SaveAsync(ct);
            db.TaxComponents.Add(replacement);
            await SaveAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return replacement;
        }

        component.Code = code;
        component.Name = name;
        component.Kind = input.Kind;
        await SaveAsync(ct);
        return component;
    }

    public async Task<TaxComponent?> SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default)
    {
        var component = await db.TaxComponents.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (component is null) return null;
        if (component.IsActive != isActive)
        {
            component.IsActive = isActive;
            await db.SaveChangesAsync(ct);
        }
        return component;
    }

    /// <summary>True when any profile version or any document line tax row references the component.</summary>
    internal async Task<bool> IsUsedAsync(Guid id, CancellationToken ct) =>
        await db.TaxProfileVersionComponents.AnyAsync(c => c.TaxComponentId == id, ct)
        || await db.SalesInvoiceLineTaxes.AnyAsync(r => r.TaxComponentId == id, ct)
        || await db.PurchaseOrderLineTaxes.AnyAsync(r => r.TaxComponentId == id, ct)
        || await db.SalesReturnLineTaxes.AnyAsync(r => r.TaxComponentId == id, ct)
        || await db.PurchaseReturnLineTaxes.AnyAsync(r => r.TaxComponentId == id, ct);

    private static void ValidateEnums(TaxComponentKind kind, TaxCalculationMethod method)
    {
        if (!Enum.IsDefined(kind)) throw new DomainValidationException("kind", "Select a valid kind.");
        if (!Enum.IsDefined(method)) throw new DomainValidationException("method", "Select a valid calculation method.");
    }

    private async Task EnsureUniqueAsync(Guid? selfId, Guid countryId, string code, string name, CancellationToken ct)
    {
        if (await db.TaxComponents.AnyAsync(c => c.Id != selfId && c.CountryId == countryId && c.Code == code, ct)) throw DuplicateCode();
        var lower = name.ToLower();
        if (await db.TaxComponents.AnyAsync(c => c.Id != selfId && c.CountryId == countryId && c.Name.ToLower() == lower, ct)) throw DuplicateName();
    }

    private static DomainValidationException DuplicateCode() => new("code", "A component with this code already exists for the country.");

    private static DomainValidationException DuplicateName() => new("name", "A component with this name already exists for the country.");

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: { } c } && (c is CodeIndex or NameIndex))
        {
            throw c == CodeIndex ? DuplicateCode() : DuplicateName();
        }
    }
}
