using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

public class PurchaseService(SelloraDbContext db) : IPurchaseService
{
    public async Task<PurchaseOrder> CreatePurchaseAsync(CreatePurchaseInput input, Guid createdByUserId, CancellationToken ct = default)
    {
        if (input.Lines.Count == 0)
        {
            throw new DomainValidationException("Lines", "Add at least one item.");
        }

        var supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Id == input.SupplierId, ct)
            ?? throw new DomainValidationException("SupplierId", "Select a valid supplier.");
        if (!supplier.IsActive)
        {
            throw new DomainValidationException("SupplierId", "This supplier is inactive.");
        }

        var purchaseNo = await NextPurchaseNoAsync(ct);

        var purchase = new PurchaseOrder
        {
            Id = Guid.NewGuid(),
            PurchaseNo = purchaseNo,
            SupplierId = supplier.Id,
            PurchaseDate = input.PurchaseDate,
            SupplierInvoiceNumber = input.SupplierInvoiceNumber,
            Notes = input.Notes,
            PaymentMethod = input.PaymentMethod,
            PaidAmount = input.PaidAmount,
            Status = PurchaseStatus.Completed,
            CreatedByUserId = createdByUserId,
        };

        decimal subtotal = 0, taxTotal = 0, otherChargesTotal = 0, discountTotal = 0;

        foreach (var lineInput in input.Lines)
        {
            var line = await BuildLineAsync(purchase, lineInput, ct);
            purchase.Lines.Add(line);

            subtotal += lineInput.Qty * lineInput.Rate;
            taxTotal += line.TaxAmount;
            otherChargesTotal += line.OtherCharges;
            discountTotal += line.Discount;

            await ApplyPurchaseLineToStockAsync(line, purchaseNo, createdByUserId, ct);
        }

        purchase.Subtotal = subtotal;
        purchase.TaxTotal = taxTotal;
        purchase.OtherChargesTotal = otherChargesTotal;
        purchase.DiscountTotal = discountTotal;
        purchase.GrandTotal = subtotal + otherChargesTotal - discountTotal + taxTotal;

        db.PurchaseOrders.Add(purchase);
        await db.SaveChangesAsync(ct);

        return purchase;
    }

    public async Task CancelPurchaseAsync(Guid purchaseOrderId, CancellationToken ct = default)
    {
        var purchase = await db.PurchaseOrders
            .Include(p => p.Lines).ThenInclude(l => l.Batch)
            .SingleOrDefaultAsync(p => p.Id == purchaseOrderId, ct)
            ?? throw new DomainValidationException("Id", "Purchase not found.");

        if (purchase.Status == PurchaseStatus.Cancelled)
        {
            throw new DomainValidationException("Status", "This purchase has already been cancelled.");
        }

        foreach (var line in purchase.Lines)
        {
            var batch = line.Batch ?? throw new InvalidOperationException($"Purchase line {line.Id} has no batch.");
            if (batch.RemainingBaseQty != batch.PurchasedBaseQty)
            {
                throw new DomainValidationException("Status", "Cannot cancel — stock from this purchase has already been sold.");
            }
        }

        foreach (var line in purchase.Lines)
        {
            var batch = line.Batch!;
            var stockItem = await db.StockItems.SingleAsync(s => s.ProductId == line.ProductId, ct);

            var qtyAfter = stockItem.QuantityOnHand - batch.PurchasedBaseQty;
            if (qtyAfter > 0)
            {
                var totalValueBefore = stockItem.QuantityOnHand * stockItem.AverageCost;
                var totalValueRemoved = batch.PurchasedBaseQty * batch.UnitCost;
                stockItem.AverageCost = Math.Max(0, (totalValueBefore - totalValueRemoved) / qtyAfter);
                stockItem.QuantityOnHand = qtyAfter;
            }
            else
            {
                stockItem.QuantityOnHand = 0;
                stockItem.AverageCost = 0;
            }

            batch.RemainingBaseQty = 0;

            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                ProductId = line.ProductId,
                Type = StockMovementType.Purchase,
                QuantityBaseUnits = -batch.PurchasedBaseQty,
                ReferenceType = "PurchaseOrder",
                ReferenceId = purchase.Id,
                ReferenceNo = purchase.PurchaseNo,
                UserId = purchase.CreatedByUserId,
                Notes = "Purchase cancelled",
            });
        }

        purchase.Status = PurchaseStatus.Cancelled;
        purchase.CancelledAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    private async Task<string> NextPurchaseNoAsync(CancellationToken ct)
    {
        var count = await db.PurchaseOrders.CountAsync(ct);
        return $"PUR-{count + 1:D4}";
    }

    private async Task<PurchaseOrderLine> BuildLineAsync(PurchaseOrder purchase, PurchaseLineInput input, CancellationToken ct)
    {
        if (input.Qty <= 0)
        {
            throw new DomainValidationException("Lines", "Quantity must be greater than zero.");
        }
        if (input.Rate < 0)
        {
            throw new DomainValidationException("Lines", "Rate cannot be negative.");
        }

        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == input.ProductId, ct)
            ?? throw new DomainValidationException("Lines", "Select a valid product.");
        if (!product.IsActive)
        {
            throw new DomainValidationException("Lines", $"{product.Name} is inactive.");
        }

        var sellingMode = await db.ProductSellingModes.SingleOrDefaultAsync(m => m.Id == input.SellingModeId, ct)
            ?? throw new DomainValidationException("Lines", "Select a valid unit.");
        if (sellingMode.ProductId != product.Id)
        {
            throw new DomainValidationException("Lines", "The selected unit does not belong to this product.");
        }

        var tax = await db.Taxes.SingleAsync(t => t.Id == product.TaxId, ct);

        var baseQty = input.Qty * sellingMode.ConversionFactorToBase;
        var taxableValue = input.Qty * input.Rate + input.OtherCharges - input.Discount;
        var taxAmount = taxableValue * tax.RatePercent / 100;
        var lineTotal = taxableValue + taxAmount;
        var unitCost = baseQty > 0 ? taxableValue / baseQty : 0;

        return new PurchaseOrderLine
        {
            Id = Guid.NewGuid(),
            PurchaseOrderId = purchase.Id,
            ProductId = product.Id,
            SellingModeId = sellingMode.Id,
            Qty = input.Qty,
            ConversionFactorToBase = sellingMode.ConversionFactorToBase,
            BaseQty = baseQty,
            Rate = input.Rate,
            TaxId = tax.Id,
            TaxRatePercent = tax.RatePercent,
            OtherCharges = input.OtherCharges,
            Discount = input.Discount,
            TaxableValue = taxableValue,
            TaxAmount = taxAmount,
            LineTotal = lineTotal,
            UnitCost = unitCost,
        };
    }

    private async Task ApplyPurchaseLineToStockAsync(PurchaseOrderLine line, string purchaseNo, Guid userId, CancellationToken ct)
    {
        db.PurchaseBatches.Add(new PurchaseBatch
        {
            Id = Guid.NewGuid(),
            PurchaseOrderLineId = line.Id,
            ProductId = line.ProductId,
            PurchasedBaseQty = line.BaseQty,
            RemainingBaseQty = line.BaseQty,
            UnitCost = line.UnitCost,
        });

        db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            ProductId = line.ProductId,
            Type = StockMovementType.Purchase,
            QuantityBaseUnits = line.BaseQty,
            ReferenceType = "PurchaseOrder",
            ReferenceId = line.PurchaseOrderId,
            ReferenceNo = purchaseNo,
            UserId = userId,
        });

        var stockItem = await db.StockItems.SingleOrDefaultAsync(s => s.ProductId == line.ProductId, ct);
        if (stockItem is null)
        {
            stockItem = new StockItem { ProductId = line.ProductId, QuantityOnHand = 0, AverageCost = 0 };
            db.StockItems.Add(stockItem);
        }

        var newQty = stockItem.QuantityOnHand + line.BaseQty;
        stockItem.AverageCost = newQty > 0
            ? (stockItem.QuantityOnHand * stockItem.AverageCost + line.BaseQty * line.UnitCost) / newQty
            : 0;
        stockItem.QuantityOnHand = newQty;
    }
}
