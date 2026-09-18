using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

public class PricingService(SelloraDbContext db) : IPricingService
{
    public async Task<SellingPrice> SetPriceAsync(Guid sellingModeId, decimal price, Guid userId, CancellationToken ct = default)
    {
        if (price < 0)
        {
            throw new DomainValidationException("Price", "Price cannot be negative.");
        }

        var mode = await db.ProductSellingModes.SingleOrDefaultAsync(m => m.Id == sellingModeId, ct)
            ?? throw new DomainValidationException("SellingModeId", "Select a valid selling mode.");

        var now = DateTimeOffset.UtcNow;

        var current = await db.SellingPrices
            .Where(p => p.ProductSellingModeId == sellingModeId && p.EffectiveTo == null)
            .SingleOrDefaultAsync(ct);

        if (current is not null)
        {
            current.EffectiveTo = now;
        }

        var next = new SellingPrice
        {
            Id = Guid.NewGuid(),
            ProductSellingModeId = mode.Id,
            Price = price,
            EffectiveFrom = now,
            EffectiveTo = null,
            CreatedByUserId = userId,
        };

        db.SellingPrices.Add(next);
        await db.SaveChangesAsync(ct);

        return next;
    }
}
