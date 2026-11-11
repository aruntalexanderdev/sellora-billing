using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Payments;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.TestData;

/// <summary>
/// Sales service for fixtures that exercise stock, returns and money rather than pricing. Selling now needs an open
/// list price per unit (an unpriced unit is refused, a different price needs an override), so before each sale this
/// puts the price the test asks for onto the unit's open list price, and sells as a user holding Sales.PriceOverride
/// (so discounts are allowed). Pricing rules themselves are tested against the real <see cref="SalesService"/>.
/// </summary>
public sealed class ListPricedSales(SelloraDbContext db, IEffectivePermissionService? permissions = null) : ISalesService
{
    private readonly SalesService inner = new(db, permissions);

    public static ISalesService For(SelloraDbContext db, IEffectivePermissionService? permissions = null) => new ListPricedSales(db, permissions);

    public Task<Guid> ResolveSellingLocationAsync(Guid? stockLocationId, CancellationToken ct = default) => inner.ResolveSellingLocationAsync(stockLocationId, ct);

    public async Task<SalesInvoice> CreateSaleAsync(CreateSaleInput input, Guid createdByUserId, CancellationToken ct = default)
    {
        foreach (var line in input.Lines)
        {
            if (!await db.ProductUnits.AnyAsync(u => u.Id == line.ProductUnitId, ct)) continue;

            var open = await db.SellingPrices.Where(p => p.ProductUnitId == line.ProductUnitId && p.EffectiveTo == null).ToListAsync(ct);
            if (open.Count == 1 && open[0].Price == line.UnitPrice) continue;

            foreach (var p in open) p.EffectiveTo = DateTimeOffset.UtcNow;
            db.SellingPrices.Add(new SellingPrice
            {
                Id = Guid.NewGuid(), ProductUnitId = line.ProductUnitId, Price = line.UnitPrice, CreatedByUserId = createdByUserId,
            });
        }
        await db.SaveChangesAsync(ct);

        return await inner.CreateSaleAsync(input with { CanOverridePrice = true }, createdByUserId, ct);
    }

    public Task<SalesInvoice?> AddPaymentAsync(Guid salesInvoiceId, AddSalePaymentInput input, Guid receivedByUserId, CancellationToken ct = default) =>
        inner.AddPaymentAsync(salesInvoiceId, input, receivedByUserId, ct);

    public Task<SalesReturn> CreateReturnAsync(CreateSaleReturnInput input, Guid userId, CancellationToken ct = default) =>
        inner.CreateReturnAsync(input, userId, ct);

    public Task<SalesInvoice?> AddRefundAsync(Guid salesInvoiceId, AddSaleRefundInput input, Guid userId, CancellationToken ct = default) =>
        inner.AddRefundAsync(salesInvoiceId, input, userId, ct);

    public Task<SalesInvoice?> ReversePaymentAsync(Guid salesInvoiceId, Guid paymentId, ReversePaymentInput input, Guid userId, CancellationToken ct = default) =>
        inner.ReversePaymentAsync(salesInvoiceId, paymentId, input, userId, ct);

    public Task CancelSaleAsync(Guid salesInvoiceId, Guid cancelledByUserId, CancellationToken ct = default) =>
        inner.CancelSaleAsync(salesInvoiceId, cancelledByUserId, ct);
}
