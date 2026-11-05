using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

public class PricingService(SelloraDbContext db) : IPricingService
{
    public async Task<SellingPrice> SetPriceAsync(Guid productUnitId, decimal price, Guid userId, CancellationToken ct = default)
    {
        if (price < 0)
        {
            throw new DomainValidationException("Price", "Price cannot be negative.");
        }

        await using var tx = await StockLocks.BeginAsync(db, ct);

        var productId = await db.ProductUnits
            .Where(u => u.Id == productUnitId)
            .Select(u => (Guid?)u.ProductId)
            .SingleOrDefaultAsync(ct)
            ?? throw new DomainValidationException("ProductUnitId", "Select a valid product unit.");

        // Serialises with product-unit edits (which could deactivate or restructure this unit) and with a
        // concurrent price change for the same product, which would otherwise leave two open prices.
        await StockLocks.LockProductForUpdateAsync(db, productId, ct);

        var unit = await db.ProductUnits
            .Include(u => u.Unit)
            .SingleOrDefaultAsync(u => u.Id == productUnitId, ct)
            ?? throw new DomainValidationException("ProductUnitId", "Select a valid product unit.");

        if (!unit.IsActive)
        {
            throw new DomainValidationException("ProductUnitId", $"{unit.Label ?? unit.Unit.Name} is no longer active and cannot be priced.");
        }

        var now = DateTimeOffset.UtcNow;

        var current = await db.SellingPrices
            .Where(p => p.ProductUnitId == productUnitId && p.EffectiveTo == null)
            .SingleOrDefaultAsync(ct);

        if (current is not null)
        {
            current.EffectiveTo = now;
        }

        var next = new SellingPrice
        {
            Id = Guid.NewGuid(),
            ProductUnitId = unit.Id,
            Price = price,
            EffectiveFrom = now,
            EffectiveTo = null,
            CreatedByUserId = userId,
        };

        db.SellingPrices.Add(next);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);

        return next;
    }
}
