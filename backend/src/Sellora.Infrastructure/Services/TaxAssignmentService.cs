using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Tax assignments (BR7, BR10). One row per (scope target, country). Every write runs in a transaction under the per-company advisory lock
/// <c>sellora:tax-assign:&lt;company&gt;</c> and re-syncs <c>Products.TaxId</c> (D3) for the products it affects when the country is the company's.
/// </summary>
public class TaxAssignmentService(SelloraDbContext db, TimeProvider? time = null) : ITaxAssignmentService
{
    private TimeProvider Clock => time ?? TimeProvider.System;

    public async Task<TaxAssignment> UpsertAsync(UpsertTaxAssignmentInput input, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(input.Scope)) throw new DomainValidationException("scopeType", "Select a valid scope.");
        if (!await db.Countries.AnyAsync(c => c.Id == input.CountryId, ct)) throw new DomainValidationException("countryId", "Select a valid country.");

        await using var transaction = await StockLocks.BeginAsync(db, ct);
        await TaxLocks.LockAssignmentsAsync(db, ct);

        var (categoryId, subcategoryId, productId) = await ResolveTargetAsync(input.Scope, input.ScopeId, ct);
        var existing = await FindAsync(input.Scope, input.CountryId, categoryId, subcategoryId, productId, ct);

        await EnsureAssignableAsync(input.SalesProfileId, input.CountryId, existing?.SalesTaxProfileId, "salesProfileId", ct);
        var purchase = input.PurchaseProfileId is { } p && p != input.SalesProfileId ? p : (Guid?)null;
        if (purchase is { } purchaseId) await EnsureAssignableAsync(purchaseId, input.CountryId, existing?.PurchaseTaxProfileId, "purchaseProfileId", ct);

        var now = Clock.GetUtcNow().UtcDateTime;
        if (existing is null)
        {
            existing = new TaxAssignment
            {
                Id = Guid.NewGuid(), Scope = input.Scope, CountryId = input.CountryId, CategoryId = categoryId, SubcategoryId = subcategoryId, ProductId = productId,
                SalesTaxProfileId = input.SalesProfileId, PurchaseTaxProfileId = purchase, CreatedAt = now,
            };
            db.TaxAssignments.Add(existing);
        }
        else
        {
            existing.SalesTaxProfileId = input.SalesProfileId;
            existing.PurchaseTaxProfileId = purchase;
            existing.UpdatedAt = now;
        }
        await SaveAsync(ct);

        await SyncMirrorAsync(input.CountryId, ScopeFilter(input.Scope, categoryId, subcategoryId, productId), ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var transaction = await StockLocks.BeginAsync(db, ct);
        await TaxLocks.LockAssignmentsAsync(db, ct);

        var assignment = await db.TaxAssignments.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (assignment is null) return false;
        if (assignment.Scope == TaxScope.Company)
        {
            throw new DomainValidationException("id", "The company default cannot be deleted. Assign another profile to replace it.");
        }

        db.TaxAssignments.Remove(assignment);
        await db.SaveChangesAsync(ct);
        await SyncMirrorAsync(assignment.CountryId, ScopeFilter(assignment.Scope, assignment.CategoryId, assignment.SubcategoryId, assignment.ProductId), ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<int> ClearRedundantOverridesAsync(Guid countryId, CancellationToken ct = default)
    {
        if (!await db.Countries.AnyAsync(c => c.Id == countryId, ct)) throw new DomainValidationException("countryId", "Select a valid country.");

        await using var transaction = await StockLocks.BeginAsync(db, ct);
        await TaxLocks.LockAssignmentsAsync(db, ct);

        var index = await TaxAssignmentIndex.LoadAsync(db, countryId, ct);
        var overrides = await db.TaxAssignments.Where(a => a.CountryId == countryId && a.Scope == TaxScope.Product).ToListAsync(ct);
        var productIds = overrides.Where(a => a.ProductId != null).Select(a => a.ProductId!.Value).ToList();
        var facts = new Dictionary<Guid, (Guid? CategoryId, Guid? SubcategoryId)>();
        foreach (var chunk in productIds.Chunk(1000))
        {
            foreach (var p in await db.Products.AsNoTracking().Where(x => chunk.Contains(x.Id)).Select(x => new { x.Id, x.CategoryId, x.SubcategoryId }).ToListAsync(ct))
            {
                facts[p.Id] = (p.CategoryId, p.SubcategoryId);
            }
        }

        var redundant = new List<TaxAssignment>();
        foreach (var a in overrides)
        {
            if (a.ProductId is not { } pid || !facts.TryGetValue(pid, out var f)) continue;
            // What the product would get without its own row: subcategory, category, company default.
            var inherited = index.Find(pid, f.CategoryId, f.SubcategoryId, includeProduct: false);
            if (inherited is not null && inherited.SalesTaxProfileId == a.SalesTaxProfileId
                && TaxAssignmentIndex.EffectivePurchase(inherited) == TaxAssignmentIndex.EffectivePurchase(a))
            {
                redundant.Add(a);
            }
        }
        if (redundant.Count == 0)
        {
            if (transaction is not null) await transaction.CommitAsync(ct);
            return 0;
        }

        db.TaxAssignments.RemoveRange(redundant);
        await db.SaveChangesAsync(ct);
        await SyncMirrorAsync(countryId, null, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return redundant.Count;
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(Guid? CategoryId, Guid? SubcategoryId, Guid? ProductId)> ResolveTargetAsync(TaxScope scope, Guid? scopeId, CancellationToken ct)
    {
        if (scope == TaxScope.Company) return (null, null, null);
        if (scopeId is not { } id) throw new DomainValidationException("scopeId", "Select the category, subcategory or product.");

        switch (scope)
        {
            case TaxScope.Category:
                if (!await db.ProductCategories.AnyAsync(c => c.Id == id, ct)) throw new DomainValidationException("scopeId", "The category does not exist.");
                return (id, null, null);
            case TaxScope.Subcategory:
                var categoryId = await db.ProductSubcategories.AsNoTracking().Where(s => s.Id == id).Select(s => (Guid?)s.CategoryId).SingleOrDefaultAsync(ct)
                    ?? throw new DomainValidationException("scopeId", "The subcategory does not exist.");
                return (categoryId, id, null);
            default:
                if (!await db.Products.AnyAsync(p => p.Id == id, ct)) throw new DomainValidationException("scopeId", "The product does not exist.");
                return (null, null, id);
        }
    }

    private Task<TaxAssignment?> FindAsync(TaxScope scope, Guid countryId, Guid? categoryId, Guid? subcategoryId, Guid? productId, CancellationToken ct) =>
        scope switch
        {
            TaxScope.Company => db.TaxAssignments.SingleOrDefaultAsync(a => a.Scope == scope && a.CountryId == countryId, ct),
            TaxScope.Category => db.TaxAssignments.SingleOrDefaultAsync(a => a.Scope == scope && a.CountryId == countryId && a.CategoryId == categoryId, ct),
            TaxScope.Subcategory => db.TaxAssignments.SingleOrDefaultAsync(a => a.Scope == scope && a.CountryId == countryId && a.SubcategoryId == subcategoryId, ct),
            _ => db.TaxAssignments.SingleOrDefaultAsync(a => a.Scope == scope && a.CountryId == countryId && a.ProductId == productId, ct),
        };

    /// <summary>BR10 and BR6: the profile exists, belongs to the country and is active (an inactive profile already on the row may stay).</summary>
    private async Task EnsureAssignableAsync(Guid profileId, Guid countryId, Guid? currentProfileId, string field, CancellationToken ct) =>
        await TaxAssignmentRules.EnsureAssignableAsync(db, profileId, countryId, currentProfileId, field, TaxRules.Today(Clock), ct);

    private static Expression<Func<Product, bool>>? ScopeFilter(TaxScope scope, Guid? categoryId, Guid? subcategoryId, Guid? productId) => scope switch
    {
        TaxScope.Category => p => p.CategoryId == categoryId,
        TaxScope.Subcategory => p => p.SubcategoryId == subcategoryId,
        TaxScope.Product => p => p.Id == productId,
        _ => null,
    };

    private async Task SyncMirrorAsync(Guid countryId, Expression<Func<Product, bool>>? scope, CancellationToken ct)
    {
        var companyCountry = await db.BusinessSettings.AsNoTracking().Select(s => s.CountryId).FirstOrDefaultAsync(ct);
        if (companyCountry != countryId) return; // other countries do not drive the mirror (D3)
        if (await TaxMirror.SyncAsync(db, scope, ct) > 0) await db.SaveChangesAsync(ct);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainValidationException("scopeId", "This scope already has an assignment for the country. Reload and edit it.");
        }
    }
}

/// <summary>Profile checks shared by the assignment service and the product service (product overrides).</summary>
internal static class TaxAssignmentRules
{
    /// <summary>BR10, BR6, T25: the profile exists, belongs to the country, is active and has a version valid today (a profile that cannot resolve would break
    /// every product that inherits it). A profile already on the row may stay (the current value is exempt from the active and version checks).</summary>
    public static async Task EnsureAssignableAsync(SelloraDbContext db, Guid profileId, Guid countryId, Guid? currentProfileId, string field, DateOnly today, CancellationToken ct)
    {
        var profile = await db.TaxProfiles.AsNoTracking().Where(p => p.Id == profileId).Select(p => new { p.Name, p.CountryId, p.IsActive }).SingleOrDefaultAsync(ct)
            ?? throw new DomainValidationException(field, "Select a valid tax profile.");
        if (profile.CountryId != countryId) throw new DomainValidationException(field, $"{profile.Name} belongs to another country than the assignment.");
        if (currentProfileId == profileId) return;
        if (!profile.IsActive) throw new DomainValidationException(field, $"{profile.Name} is inactive and cannot be newly assigned.");
        if (!await HasVersionValidOnAsync(db, profileId, today, ct))
        {
            throw new DomainValidationException(field, $"{profile.Name} has no version valid today. Add a version first.");
        }
    }

    public static Task<bool> HasVersionValidOnAsync(SelloraDbContext db, Guid profileId, DateOnly date, CancellationToken ct) =>
        db.TaxProfileVersions.AsNoTracking().AnyAsync(v => v.TaxProfileId == profileId && v.EffectiveFrom <= date && (v.EffectiveTo == null || v.EffectiveTo >= date), ct);
}
