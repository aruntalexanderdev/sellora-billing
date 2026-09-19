using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

public class SalesService(SelloraDbContext db) : ISalesService
{
    public async Task<SalesInvoice> CreateSaleAsync(CreateSaleInput input, Guid createdByUserId, CancellationToken ct = default)
    {
        if (input.Lines.Count == 0)
        {
            throw new DomainValidationException("Lines", "Add at least one item.");
        }

        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == input.CustomerId, ct)
            ?? throw new DomainValidationException("CustomerId", "Select a valid customer.");
        if (!customer.IsActive)
        {
            throw new DomainValidationException("CustomerId", "This customer is inactive.");
        }

        var invoiceNo = await NextInvoiceNoAsync(ct);

        var invoice = new SalesInvoice
        {
            Id = Guid.NewGuid(),
            InvoiceNo = invoiceNo,
            CustomerId = customer.Id,
            InvoiceDate = input.InvoiceDate,
            PaymentMethod = input.PaymentMethod,
            Status = SalesStatus.Completed,
            CreatedByUserId = createdByUserId,
        };

        decimal subtotal = 0, taxTotal = 0, discountTotal = 0;

        // Requested base quantity per product, summed across lines, so a product picked in two
        // different selling modes (e.g. one bag + some loose kg) can't together oversell stock.
        var requestedBaseQtyByProduct = new Dictionary<Guid, decimal>();

        foreach (var lineInput in input.Lines)
        {
            var line = await BuildLineAsync(invoice, lineInput, ct);
            invoice.Lines.Add(line);

            subtotal += lineInput.Qty * lineInput.UnitPrice;
            taxTotal += line.TaxAmount;
            discountTotal += lineInput.Discount;

            requestedBaseQtyByProduct[line.ProductId] = requestedBaseQtyByProduct.GetValueOrDefault(line.ProductId) + line.BaseQty;
        }

        foreach (var (productId, requestedBaseQty) in requestedBaseQtyByProduct)
        {
            var stockItem = await db.StockItems.Include(s => s.Product).SingleOrDefaultAsync(s => s.ProductId == productId, ct);
            var available = stockItem?.QuantityOnHand ?? 0;
            if (requestedBaseQty > available)
            {
                var product = stockItem?.Product ?? await db.Products.SingleAsync(p => p.Id == productId, ct);
                throw new DomainValidationException("Lines", $"Only {available} in stock for {product.Name} — cannot sell {requestedBaseQty}.");
            }
        }

        foreach (var line in invoice.Lines)
        {
            await ApplySaleLineToStockAsync(line, invoiceNo, createdByUserId, ct);
        }

        invoice.Subtotal = subtotal;
        invoice.TaxTotal = taxTotal;
        invoice.DiscountTotal = discountTotal;
        invoice.GrandTotal = subtotal - discountTotal + taxTotal;

        // Cash tendered above the total is change handed back, not money received against the invoice.
        var received = Math.Min(input.PaidAmount, invoice.GrandTotal);
        if (received > 0)
        {
            RecordPayment(invoice, received, input.PaymentMethod, input.InvoiceDate, createdByUserId);
        }

        db.SalesInvoices.Add(invoice);
        await db.SaveChangesAsync(ct);

        return invoice;
    }

    public async Task<SalesInvoice?> AddPaymentAsync(Guid salesInvoiceId, AddSalePaymentInput input, Guid receivedByUserId, CancellationToken ct = default)
    {
        // FOR UPDATE serialises concurrent payments on the same invoice, so the due they are checked
        // against cannot go stale between the read and the save. The in-memory test provider has neither
        // transactions nor row locks, hence the relational check.
        var relational = db.Database.IsRelational();
        await using var transaction = relational ? await db.Database.BeginTransactionAsync(ct) : null;

        var invoice = relational
            ? await db.SalesInvoices
                .FromSqlInterpolated($"SELECT * FROM \"SalesInvoices\" WHERE \"Id\" = {salesInvoiceId} FOR UPDATE")
                .SingleOrDefaultAsync(ct)
            : await db.SalesInvoices.SingleOrDefaultAsync(i => i.Id == salesInvoiceId, ct);
        if (invoice is null) return null;

        if (invoice.Status == SalesStatus.Cancelled)
        {
            throw new DomainValidationException("Status", "This sale is cancelled and cannot receive payments.");
        }

        var due = invoice.DueAmount;
        if (due <= 0)
        {
            throw new DomainValidationException("Amount", "This invoice is already fully paid.");
        }
        if (input.Amount <= 0)
        {
            throw new DomainValidationException("Amount", "Payment amount must be greater than zero.");
        }
        if (input.ExpectedDue != due)
        {
            throw new DomainValidationException("ExpectedDue", $"The amount due has changed to {due:F2}. Reload the invoice and try again.");
        }
        if (input.Amount > due)
        {
            throw new DomainValidationException("Amount", $"Payment cannot exceed the outstanding due of {due:F2}.");
        }

        RecordPayment(invoice, input.Amount, input.PaymentMethod, DateOnly.FromDateTime(DateTime.UtcNow), receivedByUserId);

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return invoice;
    }

    // The one place a payment is recorded: appends a ledger row and moves the invoice's running paid total;
    // an existing payment is never touched.
    private void RecordPayment(SalesInvoice invoice, decimal amount, PaymentMethod method, DateOnly paymentDate, Guid userId)
    {
        invoice.PaidAmount += amount;
        invoice.PaymentMethod = method;

        var payment = new SalesPayment
        {
            Id = Guid.NewGuid(),
            SalesInvoiceId = invoice.Id,
            PaymentDate = paymentDate,
            Amount = amount,
            ExchangeRateToBase = 1m,
            BaseAmount = amount,
            PaymentMethod = method,
            CreatedByUserId = userId,
        };
        invoice.Payments.Add(payment);
        db.SalesPayments.Add(payment);
    }

    public async Task CancelSaleAsync(Guid salesInvoiceId, CancellationToken ct = default)
    {
        var invoice = await db.SalesInvoices
            .Include(i => i.Lines).ThenInclude(l => l.BatchConsumptions).ThenInclude(c => c.PurchaseBatch)
            .SingleOrDefaultAsync(i => i.Id == salesInvoiceId, ct)
            ?? throw new DomainValidationException("Id", "Sale not found.");

        if (invoice.Status == SalesStatus.Cancelled)
        {
            throw new DomainValidationException("Status", "This sale has already been cancelled.");
        }

        var baseQtyByProduct = new Dictionary<Guid, decimal>();

        foreach (var line in invoice.Lines)
        {
            foreach (var consumption in line.BatchConsumptions)
            {
                consumption.PurchaseBatch.RemainingBaseQty += consumption.QtyBaseUnits;
            }
            baseQtyByProduct[line.ProductId] = baseQtyByProduct.GetValueOrDefault(line.ProductId) + line.BaseQty;
        }

        foreach (var (productId, baseQty) in baseQtyByProduct)
        {
            var stockItem = await db.StockItems.SingleAsync(s => s.ProductId == productId, ct);
            stockItem.QuantityOnHand += baseQty;

            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                ProductId = productId,
                Type = StockMovementType.Sale,
                QuantityBaseUnits = baseQty,
                ReferenceType = "SalesInvoice",
                ReferenceId = invoice.Id,
                ReferenceNo = invoice.InvoiceNo,
                UserId = invoice.CreatedByUserId,
                Notes = "Sale cancelled",
            });
        }

        invoice.Status = SalesStatus.Cancelled;
        invoice.CancelledAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    private async Task<string> NextInvoiceNoAsync(CancellationToken ct)
    {
        var settings = await db.BusinessSettings.FirstOrDefaultAsync(ct);
        var prefix = settings?.InvoicePrefix ?? "INV";
        var count = await db.SalesInvoices.CountAsync(ct);
        return $"{prefix}-{count + 1:D4}";
    }

    private async Task<SalesInvoiceLine> BuildLineAsync(SalesInvoice invoice, SaleLineInput input, CancellationToken ct)
    {
        if (input.Qty <= 0)
        {
            throw new DomainValidationException("Lines", "Quantity must be greater than zero.");
        }
        if (input.UnitPrice < 0)
        {
            throw new DomainValidationException("Lines", "Unit price cannot be negative.");
        }

        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == input.ProductId, ct)
            ?? throw new DomainValidationException("Lines", "Select a valid product.");
        if (!product.IsActive)
        {
            throw new DomainValidationException("Lines", $"{product.Name} is inactive.");
        }

        var sellingMode = await db.ProductSellingModes.SingleOrDefaultAsync(m => m.Id == input.SellingModeId, ct)
            ?? throw new DomainValidationException("Lines", "Select a valid selling mode.");
        if (sellingMode.ProductId != product.Id)
        {
            throw new DomainValidationException("Lines", "The selected selling mode does not belong to this product.");
        }

        var tax = await db.Taxes.SingleAsync(t => t.Id == product.TaxId, ct);

        var baseQty = input.Qty * sellingMode.ConversionFactorToBase;
        var taxableValue = input.Qty * input.UnitPrice - input.Discount;
        var taxAmount = taxableValue * tax.RatePercent / 100;
        var lineTotal = taxableValue + taxAmount;

        return new SalesInvoiceLine
        {
            Id = Guid.NewGuid(),
            SalesInvoiceId = invoice.Id,
            ProductId = product.Id,
            SellingModeId = sellingMode.Id,
            Qty = input.Qty,
            ConversionFactorToBase = sellingMode.ConversionFactorToBase,
            BaseQty = baseQty,
            UnitPrice = input.UnitPrice,
            TaxId = tax.Id,
            TaxRatePercent = tax.RatePercent,
            Discount = input.Discount,
            TaxableValue = taxableValue,
            TaxAmount = taxAmount,
            LineTotal = lineTotal,
        };
    }

    private async Task ApplySaleLineToStockAsync(SalesInvoiceLine line, string invoiceNo, Guid userId, CancellationToken ct)
    {
        var costConsumed = await ConsumeBatchesFifoAsync(line, ct);
        line.CostOfGoodsSold = costConsumed;

        db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            ProductId = line.ProductId,
            Type = StockMovementType.Sale,
            QuantityBaseUnits = -line.BaseQty,
            ReferenceType = "SalesInvoice",
            ReferenceId = line.SalesInvoiceId,
            ReferenceNo = invoiceNo,
            UserId = userId,
        });

        var stockItem = await db.StockItems.SingleAsync(s => s.ProductId == line.ProductId, ct);
        stockItem.QuantityOnHand -= line.BaseQty;
    }

    private async Task<decimal> ConsumeBatchesFifoAsync(SalesInvoiceLine line, CancellationToken ct)
    {
        var remaining = line.BaseQty;
        var costConsumed = 0m;

        var batches = await db.PurchaseBatches
            .Where(b => b.ProductId == line.ProductId && b.RemainingBaseQty > 0)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync(ct);

        foreach (var batch in batches)
        {
            if (remaining <= 0) break;
            var take = Math.Min(batch.RemainingBaseQty, remaining);
            batch.RemainingBaseQty -= take;
            remaining -= take;
            costConsumed += take * batch.UnitCost;

            line.BatchConsumptions.Add(new SalesInvoiceLineBatchConsumption
            {
                Id = Guid.NewGuid(),
                SalesInvoiceLineId = line.Id,
                PurchaseBatchId = batch.Id,
                QtyBaseUnits = take,
                UnitCost = batch.UnitCost,
            });
        }

        return costConsumed;
    }
}
