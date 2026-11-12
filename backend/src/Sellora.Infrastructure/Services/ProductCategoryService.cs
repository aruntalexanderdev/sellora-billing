using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>Category and subcategory masters (Phase 08, FI-D16). The DB constraints (unique code, case-insensitive unique name,
/// code format, composite same-company parent FK) are the final guard; the pre-checks give the field errors and a race on a
/// unique index is mapped to the same field error.</summary>
public partial class ProductCategoryService(SelloraDbContext db) : IProductCategoryService
{
    public const int NameMaxLength = 60;
    public const int CodeMaxLength = 20;

    private const string CategoryCodeIndex = "IX_ProductCategories_CompanyId_Code";
    private const string CategoryNameIndex = "UX_ProductCategories_CompanyId_Name_Lower";
    private const string SubcategoryCodeIndex = "IX_ProductSubcategories_CompanyId_CategoryId_Code";
    private const string SubcategoryNameIndex = "UX_ProductSubcategories_Category_Name_Lower";

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,19}$")]
    private static partial Regex CodePattern();

    public async Task<ProductCategory> CreateCategoryAsync(UpsertProductClassificationInput input, CancellationToken ct = default)
    {
        var (code, name) = Normalise(input);
        await EnsureCategoryUniqueAsync(null, code, name, ct);

        var category = new ProductCategory
        {
            Id = Guid.NewGuid(), Code = code, Name = name, SortOrder = input.SortOrder, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        db.ProductCategories.Add(category);
        await SaveAsync(ct);
        return category;
    }

    public async Task<ProductCategory?> UpdateCategoryAsync(Guid categoryId, UpsertProductClassificationInput input, CancellationToken ct = default)
    {
        var (code, name) = Normalise(input);
        await using var transaction = await StockLocks.BeginAsync(db, ct);

        var category = await db.ProductCategories.SingleOrDefaultAsync(c => c.Id == categoryId, ct);
        if (category is null) return null;
        await EnsureCategoryUniqueAsync(categoryId, code, name, ct);

        var renamed = category.Name != name;
        category.Code = code;
        category.Name = name;
        category.SortOrder = input.SortOrder;
        category.UpdatedAt = DateTime.UtcNow;

        if (renamed)
        {
            // Mirror of the legacy Product.Category string (kept for one release, FI-D17).
            var products = await db.Products.Where(p => p.CategoryId == categoryId).ToListAsync(ct);
            foreach (var product in products) product.Category = name;
        }

        await SaveAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return category;
    }

    public async Task<ProductCategory?> SetCategoryActiveAsync(Guid categoryId, bool isActive, CancellationToken ct = default)
    {
        var category = await db.ProductCategories.SingleOrDefaultAsync(c => c.Id == categoryId, ct);
        if (category is null) return null;

        if (category.IsActive != isActive)
        {
            category.IsActive = isActive;
            category.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return category;
    }

    public async Task<ProductSubcategory?> CreateSubcategoryAsync(Guid categoryId, UpsertProductClassificationInput input, CancellationToken ct = default)
    {
        var (code, name) = Normalise(input);
        var category = await db.ProductCategories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == categoryId, ct);
        if (category is null) return null;
        await EnsureSubcategoryUniqueAsync(categoryId, null, code, name, ct);

        var sub = new ProductSubcategory
        {
            Id = Guid.NewGuid(), CategoryId = categoryId, Code = code, Name = name, SortOrder = input.SortOrder, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        db.ProductSubcategories.Add(sub);
        await SaveAsync(ct);
        return sub;
    }

    public async Task<ProductSubcategory?> UpdateSubcategoryAsync(Guid categoryId, Guid subcategoryId, UpsertProductClassificationInput input, CancellationToken ct = default)
    {
        var (code, name) = Normalise(input);
        var sub = await db.ProductSubcategories.SingleOrDefaultAsync(s => s.Id == subcategoryId && s.CategoryId == categoryId, ct);
        if (sub is null) return null;
        await EnsureSubcategoryUniqueAsync(categoryId, subcategoryId, code, name, ct);

        sub.Code = code;
        sub.Name = name;
        sub.SortOrder = input.SortOrder;
        sub.UpdatedAt = DateTime.UtcNow;
        await SaveAsync(ct);
        return sub;
    }

    public async Task<ProductSubcategory?> SetSubcategoryActiveAsync(Guid categoryId, Guid subcategoryId, bool isActive, CancellationToken ct = default)
    {
        var sub = await db.ProductSubcategories.SingleOrDefaultAsync(s => s.Id == subcategoryId && s.CategoryId == categoryId, ct);
        if (sub is null) return null;

        if (sub.IsActive != isActive)
        {
            if (isActive && !await db.ProductCategories.AnyAsync(c => c.Id == categoryId && c.IsActive, ct))
            {
                throw new DomainValidationException("isActive", "Activate the category first; a subcategory of an inactive category cannot be activated.");
            }
            sub.IsActive = isActive;
            sub.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return sub;
    }

    // ------------------------------------------------------------------ validation

    /// <summary>Code trimmed + upper-cased then format-checked; name trimmed 1..60; sort order non-negative.</summary>
    private static (string Code, string Name) Normalise(UpsertProductClassificationInput input)
    {
        var code = (input.Code ?? string.Empty).Trim().ToUpperInvariant();
        var name = (input.Name ?? string.Empty).Trim();
        var errors = new List<FieldError>();

        if (code.Length == 0) errors.Add(new FieldError("code", "Code is required."));
        else if (!CodePattern().IsMatch(code))
        {
            errors.Add(new FieldError("code", $"Code must be 1-{CodeMaxLength} characters: letters, digits and hyphens, starting with a letter or digit."));
        }

        if (name.Length == 0) errors.Add(new FieldError("name", "Name is required."));
        else if (name.Length > NameMaxLength) errors.Add(new FieldError("name", $"Name cannot exceed {NameMaxLength} characters."));

        if (input.SortOrder < 0) errors.Add(new FieldError("sortOrder", "Sort order cannot be negative."));

        if (errors.Count > 0) throw new DomainValidationException(errors);
        return (code, name);
    }

    private async Task EnsureCategoryUniqueAsync(Guid? selfId, string code, string name, CancellationToken ct)
    {
        var lower = name.ToLower();
        if (await db.ProductCategories.AnyAsync(c => c.Id != selfId && c.Code == code, ct)) throw DuplicateCode();
        if (await db.ProductCategories.AnyAsync(c => c.Id != selfId && c.Name.ToLower() == lower, ct)) throw DuplicateName();
    }

    private async Task EnsureSubcategoryUniqueAsync(Guid categoryId, Guid? selfId, string code, string name, CancellationToken ct)
    {
        var lower = name.ToLower();
        if (await db.ProductSubcategories.AnyAsync(s => s.CategoryId == categoryId && s.Id != selfId && s.Code == code, ct)) throw DuplicateCode();
        if (await db.ProductSubcategories.AnyAsync(s => s.CategoryId == categoryId && s.Id != selfId && s.Name.ToLower() == lower, ct)) throw DuplicateName();
    }

    private static DomainValidationException DuplicateCode() => new("code", "This code is already in use.");

    private static DomainValidationException DuplicateName() => new("name", "This name is already in use.");

    /// <summary>The pre-checks cannot see a concurrent uncommitted insert/rename, so the unique indexes are the final guard.</summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: { } c }
            && (c is CategoryCodeIndex or CategoryNameIndex or SubcategoryCodeIndex or SubcategoryNameIndex))
        {
            throw c is CategoryCodeIndex or SubcategoryCodeIndex ? DuplicateCode() : DuplicateName();
        }
    }
}
