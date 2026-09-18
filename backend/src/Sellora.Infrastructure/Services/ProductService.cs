using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

public class ProductService(SelloraDbContext db) : IProductService
{
    public async Task<Product> CreateProductAsync(UpsertProductInput input, CancellationToken ct = default)
    {
        await ValidateReferencesAsync(input, ct);

        if (await db.Products.AnyAsync(p => p.Sku.ToLower() == input.Sku.Trim().ToLower(), ct))
        {
            throw new DomainValidationException(nameof(input.Sku), "A product with this SKU already exists.");
        }

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Sku = input.Sku.Trim(),
            Name = input.Name.Trim(),
            Category = input.Category,
            BaseUnitId = input.BaseUnitId,
            TaxId = input.TaxId,
            ReorderLevel = input.ReorderLevel,
        };

        foreach (var mode in input.SellingModes)
        {
            product.SellingModes.Add(new ProductSellingMode
            {
                Id = Guid.NewGuid(),
                ProductId = product.Id,
                Label = mode.Label.Trim(),
                ConversionFactorToBase = mode.ConversionFactorToBase,
            });
        }

        db.Products.Add(product);
        await db.SaveChangesAsync(ct);

        await db.Entry(product).Reference(p => p.BaseUnit).LoadAsync(ct);
        await db.Entry(product).Reference(p => p.Tax).LoadAsync(ct);
        return product;
    }

    public async Task<Product?> UpdateProductAsync(Guid productId, UpsertProductInput input, CancellationToken ct = default)
    {
        await ValidateReferencesAsync(input, ct);

        var product = await db.Products.Include(p => p.SellingModes).Include(p => p.BaseUnit).Include(p => p.Tax)
            .SingleOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null) return null;

        if (await db.Products.AnyAsync(p => p.Id != productId && p.Sku.ToLower() == input.Sku.Trim().ToLower(), ct))
        {
            throw new DomainValidationException(nameof(input.Sku), "A product with this SKU already exists.");
        }

        product.Sku = input.Sku.Trim();
        product.Name = input.Name.Trim();
        product.Category = input.Category;
        product.BaseUnitId = input.BaseUnitId;
        product.TaxId = input.TaxId;
        product.ReorderLevel = input.ReorderLevel;

        await ReconcileSellingModesAsync(product, input.SellingModes, ct);

        await db.SaveChangesAsync(ct);

        if (product.BaseUnitId != product.BaseUnit.Id) await db.Entry(product).Reference(p => p.BaseUnit).LoadAsync(ct);
        if (product.TaxId != product.Tax.Id) await db.Entry(product).Reference(p => p.Tax).LoadAsync(ct);
        return product;
    }

    /// <summary>Matches submitted selling modes against the product's existing ones by id instead of
    /// clearing and recreating the collection, so modes referenced by PurchaseOrderLines/SalesInvoiceLines
    /// keep their identity. A mode dropped from the list is deactivated if it has transactional history,
    /// or deleted outright if it does not.</summary>
    private async Task ReconcileSellingModesAsync(Product product, IReadOnlyList<SellingModeInputItem> inputs, CancellationToken ct)
    {
        var existingById = product.SellingModes.ToDictionary(m => m.Id);
        var submittedIds = inputs.Where(i => i.Id.HasValue).Select(i => i.Id!.Value).ToHashSet();

        foreach (var input in inputs)
        {
            if (input.Id is { } id)
            {
                if (!existingById.TryGetValue(id, out var mode))
                {
                    throw new DomainValidationException(nameof(UpsertProductInput.SellingModes), "One of the selling modes no longer exists on this product.");
                }

                mode.Label = input.Label.Trim();
                mode.ConversionFactorToBase = input.ConversionFactorToBase;
                mode.IsActive = true;
            }
            else
            {
                // product is already tracked (this is an Update, not a Create), so adding to the
                // navigation collection alone leaves EF unsure whether this pre-keyed row is new or
                // existing — it must be added to the context explicitly to be tracked as Added.
                var newMode = new ProductSellingMode
                {
                    Id = Guid.NewGuid(),
                    ProductId = product.Id,
                    Label = input.Label.Trim(),
                    ConversionFactorToBase = input.ConversionFactorToBase,
                };
                product.SellingModes.Add(newMode);
                db.ProductSellingModes.Add(newMode);
            }
        }

        var removed = existingById.Values.Where(m => !submittedIds.Contains(m.Id)).ToList();
        if (removed.Count == 0) return;

        var removedIds = removed.Select(m => m.Id).ToList();
        var referencedByPurchases = await db.PurchaseOrderLines.Where(l => removedIds.Contains(l.SellingModeId))
            .Select(l => l.SellingModeId).ToListAsync(ct);
        var referencedBySales = await db.SalesInvoiceLines.Where(l => removedIds.Contains(l.SellingModeId))
            .Select(l => l.SellingModeId).ToListAsync(ct);
        var referenced = referencedByPurchases.Concat(referencedBySales).ToHashSet();

        foreach (var mode in removed)
        {
            if (referenced.Contains(mode.Id))
            {
                mode.IsActive = false;
            }
            else
            {
                product.SellingModes.Remove(mode);
            }
        }
    }

    private async Task ValidateReferencesAsync(UpsertProductInput input, CancellationToken ct)
    {
        if (!await db.Units.AnyAsync(u => u.Id == input.BaseUnitId, ct))
        {
            throw new DomainValidationException(nameof(input.BaseUnitId), "Select a valid base unit.");
        }
        if (!await db.Taxes.AnyAsync(t => t.Id == input.TaxId, ct))
        {
            throw new DomainValidationException(nameof(input.TaxId), "Select a valid tax.");
        }
    }
}
