using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Payments;
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

        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, createdByUserId, Idempotency.Sale, input.IdempotencyKey, ct) is { } existingId)
        {
            return await db.SalesInvoices.SingleAsync(i => i.Id == existingId, ct);
        }

        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == input.CustomerId, ct)
            ?? throw new DomainValidationException("CustomerId", "Select a valid customer.");
        if (!customer.IsActive)
        {
            throw new DomainValidationException("CustomerId", "This customer is inactive.");
        }

        await FinancialAccountRules.EnsureUsableAsync(db, input.PaidAmount > 0 ? input.AccountId : null, ct);

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

        // Product KEY SHARE before building lines: a concurrent product-unit edit (FOR UPDATE) waits for this sale to
        // commit, so the factors snapshotted below cannot change underneath it. StockItems locks follow (§5.4).
        await StockLocks.LockProductsKeyShareAsync(db, input.Lines.Select(l => l.ProductId), ct);

        // Requested base quantity per product, summed across lines, so a product picked in two
        // different units (e.g. one bag + some loose kg) can't together oversell stock.
        var requestedBaseQtyByProduct = new Dictionary<Guid, decimal>();

        foreach (var lineInput in input.Lines)
        {
            var line = await BuildLineAsync(invoice, lineInput, input.CanOverridePrice, ct);
            invoice.Lines.Add(line);

            subtotal += lineInput.Qty * lineInput.UnitPrice;
            taxTotal += line.TaxAmount;
            discountTotal += lineInput.Discount;

            requestedBaseQtyByProduct[line.ProductId] = requestedBaseQtyByProduct.GetValueOrDefault(line.ProductId) + line.BaseQty;
        }

        // Hold each product's stock lock from here to commit, so the availability check and the FIFO batch
        // consumption below see the same, current quantities.
        await StockLocks.LockStockItemsAsync(db, requestedBaseQtyByProduct.Keys, ct);

        foreach (var (productId, requestedBaseQty) in requestedBaseQtyByProduct)
        {
            var stockItem = await db.StockItems.Include(s => s.Product).SingleOrDefaultAsync(s => s.ProductId == productId, ct);
            var available = stockItem?.QuantityOnHand ?? 0;
            if (requestedBaseQty > available)
            {
                var product = await db.Products.Include(p => p.BaseUnit).SingleAsync(p => p.Id == productId, ct);
                var code = product.BaseUnit.ShortCode;
                throw new DomainValidationException("Lines", $"Only {StockQuantity.Format(available)} {code} of {product.Name} in stock — cannot sell {StockQuantity.Format(requestedBaseQty)} {code}.");
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
            await RecordPaymentAsync(invoice, received, input.PaymentMethod, input.InvoiceDate, createdByUserId, input.AccountId, ct);
        }

        db.SalesInvoices.Add(invoice);
        Idempotency.Record(db, createdByUserId, Idempotency.Sale, input.IdempotencyKey, invoice.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        return invoice;
    }

    public async Task<SalesInvoice?> AddPaymentAsync(Guid salesInvoiceId, AddSalePaymentInput input, Guid receivedByUserId, CancellationToken ct = default)
    {
        await using var transaction = await BeginAsync(ct);
        var invoice = await LockInvoiceAsync(salesInvoiceId, ct);
        if (invoice is null) return null;

        if (invoice.Status == SalesStatus.Cancelled)
        {
            throw new DomainValidationException("Status", "This sale is cancelled and cannot receive payments.");
        }

        await FinancialAccountRules.EnsureUsableAsync(db, input.AccountId, ct);

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

        await RecordPaymentAsync(invoice, input.Amount, input.PaymentMethod, DateOnly.FromDateTime(DateTime.UtcNow), receivedByUserId, input.AccountId, ct);

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return invoice;
    }

    // FOR UPDATE serialises concurrent money operations on the same invoice, so the due or refundable amount they
    // are checked against cannot go stale between the read and the save. The in-memory test provider has neither
    // transactions nor row locks, hence the relational check.
    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginAsync(CancellationToken ct) =>
        db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;

    private async Task<SalesInvoice?> LockInvoiceAsync(Guid salesInvoiceId, CancellationToken ct) =>
        db.Database.IsRelational()
            ? await db.SalesInvoices
                .FromSqlInterpolated($"SELECT * FROM \"SalesInvoices\" WHERE \"Id\" = {salesInvoiceId} FOR UPDATE")
                .SingleOrDefaultAsync(ct)
            : await db.SalesInvoices.SingleOrDefaultAsync(i => i.Id == salesInvoiceId, ct);

    // The one place money is recorded against an invoice: appends a ledger row and moves the invoice's running paid
    // total (up for a payment, down for a refund or reversal); an existing entry is never touched. Every entry
    // writes its own financial transaction.
    private async Task<SalesPayment> RecordEntryAsync(
        SalesInvoice invoice, PaymentEntryKind kind, decimal amount, PaymentMethod method, DateOnly date, Guid userId,
        Guid? accountId, string? reason, Guid? reversesPaymentId, Guid? salesReturnId, CancellationToken ct)
    {
        invoice.PaidAmount += kind == PaymentEntryKind.Payment ? amount : -amount;
        if (kind == PaymentEntryKind.Payment) invoice.PaymentMethod = method;

        var payment = new SalesPayment
        {
            Id = Guid.NewGuid(),
            SalesInvoiceId = invoice.Id,
            Kind = kind,
            ReversesPaymentId = reversesPaymentId,
            SalesReturnId = salesReturnId,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            PaymentDate = date,
            Amount = amount,
            ExchangeRateToBase = 1m,
            BaseAmount = amount,
            PaymentMethod = method,
            CreatedByUserId = userId,
        };
        invoice.Payments.Add(payment);
        db.SalesPayments.Add(payment);

        // Sales are billed in the base currency, so the money moved is in the base currency too.
        var baseCurrencyId = await db.BusinessSettings.Select(s => s.BaseCurrencyId).SingleAsync(ct);
        db.FinancialTransactions.Add(FinancialTransaction.ForSalesPayment(payment, baseCurrencyId, accountId));
        return payment;
    }

    private Task RecordPaymentAsync(SalesInvoice invoice, decimal amount, PaymentMethod method, DateOnly paymentDate, Guid userId, Guid? accountId, CancellationToken ct) =>
        RecordEntryAsync(invoice, PaymentEntryKind.Payment, amount, method, paymentDate, userId, accountId, null, null, null, ct);

    public async Task<SalesInvoice?> AddRefundAsync(Guid salesInvoiceId, AddSaleRefundInput input, Guid userId, CancellationToken ct = default)
    {
        await using var transaction = await BeginAsync(ct);
        var invoice = await LockInvoiceAsync(salesInvoiceId, ct);
        if (invoice is null) return null;

        await FinancialAccountRules.EnsureUsableAsync(db, input.AccountId, ct);
        if (input.SalesReturnId is { } returnId
            && !await db.SalesReturns.AnyAsync(r => r.Id == returnId && r.SalesInvoiceId == invoice.Id, ct))
        {
            throw new DomainValidationException("SalesReturnId", "Select a return that belongs to this sale.");
        }

        ValidateRefund(invoice, input.Amount, input.ExpectedRefundable);
        await RecordEntryAsync(invoice, PaymentEntryKind.Refund, input.Amount, input.PaymentMethod, DateOnly.FromDateTime(DateTime.UtcNow),
            userId, input.AccountId, input.Reason, null, input.SalesReturnId, ct);

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return invoice;
    }

    private static void ValidateRefund(SalesInvoice invoice, decimal amount, decimal? expectedRefundable)
    {
        var refundable = invoice.RefundableAmount;
        if (refundable <= 0)
        {
            throw new DomainValidationException("Amount", "There is nothing to refund on this invoice.");
        }
        if (amount <= 0)
        {
            throw new DomainValidationException("Amount", "Refund amount must be greater than zero.");
        }
        if (expectedRefundable is { } expected && expected != refundable)
        {
            throw new DomainValidationException("ExpectedRefundable", $"The refundable amount has changed to {refundable:F2}. Reload the invoice and try again.");
        }
        if (amount > refundable)
        {
            throw new DomainValidationException("Amount", $"Refund cannot exceed the refundable amount of {refundable:F2}.");
        }
    }

    public async Task<SalesInvoice?> ReversePaymentAsync(Guid salesInvoiceId, Guid paymentId, ReversePaymentInput input, Guid userId, CancellationToken ct = default)
    {
        await using var transaction = await BeginAsync(ct);
        var invoice = await LockInvoiceAsync(salesInvoiceId, ct);
        if (invoice is null) return null;

        var payment = await db.SalesPayments.SingleOrDefaultAsync(p => p.Id == paymentId && p.SalesInvoiceId == invoice.Id, ct)
            ?? throw new DomainValidationException("PaymentId", "Payment not found on this invoice.");
        if (payment.Kind != PaymentEntryKind.Payment)
        {
            throw new DomainValidationException("PaymentId", "Only a payment can be reversed.");
        }
        if (await db.SalesPayments.AnyAsync(p => p.ReversesPaymentId == payment.Id, ct))
        {
            throw new DomainValidationException("PaymentId", "This payment has already been reversed.");
        }
        if (invoice.PaidAmount < payment.Amount)
        {
            throw new DomainValidationException("PaymentId", "This payment can't be reversed because part of it has since been refunded.");
        }

        // The reversal goes back through the same account the payment came in through.
        var accountId = await db.FinancialTransactions.Where(t => t.SalesPaymentId == payment.Id).Select(t => t.AccountId).SingleOrDefaultAsync(ct);
        await RecordEntryAsync(invoice, PaymentEntryKind.Reversal, payment.Amount, payment.PaymentMethod, DateOnly.FromDateTime(DateTime.UtcNow),
            userId, accountId, input.Reason, payment.Id, null, ct);

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return invoice;
    }

    public async Task<SalesReturn> CreateReturnAsync(CreateSaleReturnInput input, Guid userId, CancellationToken ct = default)
    {
        if (input.Lines.Count == 0)
        {
            throw new DomainValidationException("Lines", "Choose at least one item to return.");
        }
        if (input.Lines.Select(l => l.SalesInvoiceLineId).Distinct().Count() != input.Lines.Count)
        {
            throw new DomainValidationException("Lines", "Each item can only be listed once.");
        }
        if (input.ReturnDate > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
        {
            throw new DomainValidationException("ReturnDate", "Return date cannot be in the future.");
        }

        await using var transaction = await BeginAsync(ct);
        var invoice = await LockInvoiceAsync(input.SalesInvoiceId, ct)
            ?? throw new DomainValidationException("SalesInvoiceId", "Select a valid sale.");
        if (invoice.Status == SalesStatus.Cancelled)
        {
            throw new DomainValidationException("Status", "This sale is cancelled, so nothing can be returned against it.");
        }
        if (input.ReturnDate < invoice.InvoiceDate)
        {
            throw new DomainValidationException("ReturnDate", "Return date cannot be before the sale date.");
        }

        // Lock the stock rows before reading the batches the goods go back into.
        var lineProductIds = await db.SalesInvoiceLines.Where(l => l.SalesInvoiceId == invoice.Id).Select(l => l.ProductId).ToListAsync(ct);
        // Lock order: SalesInvoices (above) -> Products KEY SHARE -> StockItems. The return reads the factor of a unit
        // that history may not yet reference, so a concurrent product-unit edit must wait until this commits.
        await StockLocks.LockProductsKeyShareAsync(db, lineProductIds, ct);
        await StockLocks.LockStockItemsAsync(db, lineProductIds, ct);
        var lines = await db.SalesInvoiceLines
            .Include(l => l.Product).ThenInclude(p => p.BaseUnit)
            .Include(l => l.BatchConsumptions).ThenInclude(c => c.PurchaseBatch)
            .Where(l => l.SalesInvoiceId == invoice.Id).ToListAsync(ct);
        var priorReturns = await db.SalesReturnLines
            .Where(r => r.SalesInvoiceLine.SalesInvoiceId == invoice.Id).ToListAsync(ct);
        var productUnits = await db.ProductUnits.Include(u => u.Unit)
            .Where(u => lineProductIds.Contains(u.ProductId)).ToListAsync(ct);
        var precision = await db.BusinessSettings.Select(s => s.BaseCurrency.DecimalPrecision).SingleAsync(ct);

        var salesReturn = new SalesReturn
        {
            Id = Guid.NewGuid(),
            ReturnNo = await NextReturnNoAsync(ct),
            SalesInvoiceId = invoice.Id,
            ReturnDate = input.ReturnDate,
            Reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim(),
            CreatedByUserId = userId,
        };

        foreach (var requested in input.Lines)
        {
            var line = lines.SingleOrDefault(l => l.Id == requested.SalesInvoiceLineId)
                ?? throw new DomainValidationException("Lines", "One of the items does not belong to this sale.");

            var baseUnit = line.Product.BaseUnit;

            // The return unit: the line's own unit (even if deactivated since) or any active unit of the product.
            // The sellable flag does not apply - a return reverses goods that were physically sold.
            var unitId = requested.ProductUnitId ?? line.ProductUnitId;
            var productUnit = productUnits.SingleOrDefault(u => u.Id == unitId)
                ?? throw new DomainValidationException("Lines", "Select a valid unit.");
            if (productUnit.ProductId != line.ProductId)
            {
                throw new DomainValidationException("Lines", $"The selected unit does not belong to {line.Product.Name}.");
            }
            var unitDisplay = productUnit.Label ?? productUnit.Unit.Name;
            if (!productUnit.IsActive && productUnit.Id != line.ProductUnitId)
            {
                throw new DomainValidationException("Lines", $"{unitDisplay} is no longer active.");
            }

            var qty = requested.Qty;
            if (qty <= 0)
            {
                throw new DomainValidationException("Lines", $"Quantity for {line.Product.Name} must be greater than zero.");
            }
            StockQuantity.EnsureEnteredDecimals(qty, productUnit.Unit.DecimalPlaces, unitDisplay, "Lines");

            var baseQty = qty * productUnit.ConversionFactorToBase;
            StockQuantity.EnsureStorable(baseQty, "Lines");
            StockQuantity.EnsureWithinBaseDecimals(qty, productUnit.Unit.ShortCode, baseQty, baseUnit, "Lines");

            var prior = priorReturns.Where(r => r.SalesInvoiceLineId == line.Id).ToList();
            var returnableBase = line.BaseQty - prior.Sum(r => r.BaseQty);
            if (baseQty > returnableBase)
            {
                throw new DomainValidationException("Lines", $"Only {StockQuantity.Format(returnableBase)} {baseUnit.ShortCode} of {line.Product.Name} can still be returned.");
            }

            // Returning whatever is left takes the exact remainder, so per-line rounding can never leave a stray
            // paisa of credit unissued (or issue one too many) once an item is fully returned. Credit is prorated
            // by base quantity, so returns in different units of the same line add up.
            var last = baseQty == returnableBase;
            var taxable = last ? line.TaxableValue - prior.Sum(r => r.TaxableValue) : Math.Round(line.TaxableValue * baseQty / line.BaseQty, precision, MidpointRounding.AwayFromZero);
            var tax = last ? line.TaxAmount - prior.Sum(r => r.TaxAmount) : Math.Round(line.TaxAmount * baseQty / line.BaseQty, precision, MidpointRounding.AwayFromZero);

            // Put the goods back into the batches they were sold from, oldest first.
            var remaining = baseQty;
            decimal costRestored = 0;
            foreach (var consumption in line.BatchConsumptions.OrderBy(c => c.PurchaseBatch.CreatedAt).ThenBy(c => c.PurchaseBatch.BatchSequence))
            {
                if (remaining <= 0) break;
                var alloc = Math.Min(remaining, consumption.QtyBaseUnits - consumption.ReturnedQtyBaseUnits);
                if (alloc <= 0) continue;

                consumption.ReturnedQtyBaseUnits += alloc;
                consumption.PurchaseBatch.RemainingBaseQty += alloc;
                costRestored += alloc * consumption.UnitCost;
                remaining -= alloc;
            }
            if (remaining > 0)
            {
                throw new InvalidOperationException($"Sale line {line.Id} has fewer consumed batches than the quantity being returned.");
            }

            var returnLine = new SalesReturnLine
            {
                Id = Guid.NewGuid(),
                SalesReturnId = salesReturn.Id,
                SalesInvoiceLineId = line.Id,
                ProductId = line.ProductId,
                ProductUnitId = productUnit.Id,
                UnitId = productUnit.UnitId,
                UnitCode = productUnit.Unit.ShortCode,
                ConversionFactorToBase = productUnit.ConversionFactorToBase,
                Qty = qty,
                BaseQty = baseQty,
                TaxableValue = taxable,
                TaxAmount = tax,
                LineTotal = taxable + tax,
                CostRestored = StockCost.Money(costRestored),
            };
            salesReturn.Lines.Add(returnLine);
            priorReturns.Add(returnLine);
            salesReturn.TaxableTotal += taxable;
            salesReturn.TaxTotal += tax;

            var stockItem = await db.StockItems.SingleAsync(s => s.ProductId == line.ProductId, ct);
            stockItem.QuantityOnHand += baseQty;
            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                ProductId = line.ProductId,
                Type = StockMovementType.SaleReturn,
                QuantityBaseUnits = baseQty,
                ReferenceType = "SalesReturn",
                ReferenceId = salesReturn.Id,
                ReferenceNo = salesReturn.ReturnNo,
                UserId = userId,
                Notes = $"Returned from {invoice.InvoiceNo}",
            });
        }

        salesReturn.Total = salesReturn.TaxableTotal + salesReturn.TaxTotal;
        invoice.ReturnedAmount += salesReturn.Total;
        db.SalesReturns.Add(salesReturn);

        if (input.Refund is { } refund)
        {
            await FinancialAccountRules.EnsureUsableAsync(db, refund.AccountId, ct);
            ValidateRefund(invoice, refund.Amount, null);
            await RecordEntryAsync(invoice, PaymentEntryKind.Refund, refund.Amount, refund.PaymentMethod, input.ReturnDate,
                userId, refund.AccountId, refund.Reason, null, salesReturn.Id, ct);
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return salesReturn;
    }

    public async Task CancelSaleAsync(Guid salesInvoiceId, Guid cancelledByUserId, CancellationToken ct = default)
    {
        await using var transaction = await BeginAsync(ct);
        _ = await LockInvoiceAsync(salesInvoiceId, ct) ?? throw new DomainValidationException("Id", "Sale not found.");
        await StockLocks.LockStockItemsAsync(db, await db.SalesInvoiceLines.Where(l => l.SalesInvoiceId == salesInvoiceId).Select(l => l.ProductId).ToListAsync(ct), ct);

        var invoice = await db.SalesInvoices
            .Include(i => i.Lines).ThenInclude(l => l.BatchConsumptions).ThenInclude(c => c.PurchaseBatch)
            .SingleAsync(i => i.Id == salesInvoiceId, ct);

        if (invoice.Status == SalesStatus.Cancelled)
        {
            throw new DomainValidationException("Status", "This sale has already been cancelled.");
        }
        if (await db.SalesReturns.AnyAsync(r => r.SalesInvoiceId == invoice.Id, ct))
        {
            throw new DomainValidationException("Status", "Cannot cancel — items from this sale have been returned.");
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
                Type = StockMovementType.SaleCancellation,
                QuantityBaseUnits = baseQty,
                ReferenceType = "SalesInvoice",
                ReferenceId = invoice.Id,
                ReferenceNo = invoice.InvoiceNo,
                UserId = cancelledByUserId,
                Notes = "Sale cancelled",
            });
        }

        invoice.Status = SalesStatus.Cancelled;
        invoice.CancelledAt = DateTimeOffset.UtcNow;
        invoice.CancelledByUserId = cancelledByUserId;

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    private async Task<string> NextReturnNoAsync(CancellationToken ct)
    {
        await StockLocks.LockDocumentNumberAsync(db, "SRT", ct);
        return $"SRT-{await db.SalesReturns.CountAsync(ct) + 1:D4}";
    }

    private async Task<string> NextInvoiceNoAsync(CancellationToken ct)
    {
        await StockLocks.LockDocumentNumberAsync(db, "INV", ct);
        var settings = await db.BusinessSettings.FirstOrDefaultAsync(ct);
        var prefix = settings?.InvoicePrefix ?? "INV";
        var count = await db.SalesInvoices.CountAsync(ct);
        return $"{prefix}-{count + 1:D4}";
    }

    private async Task<SalesInvoiceLine> BuildLineAsync(SalesInvoice invoice, SaleLineInput input, bool canOverridePrice, CancellationToken ct)
    {
        if (input.Qty <= 0)
        {
            throw new DomainValidationException("Lines", "Quantity must be greater than zero.");
        }
        if (input.UnitPrice < 0)
        {
            throw new DomainValidationException("Lines", "Unit price cannot be negative.");
        }
        if (input.Discount < 0)
        {
            throw new DomainValidationException("Lines", "Discount cannot be negative.");
        }
        // The column is numeric(18,4): a finer discount would be rounded on save while the line and header totals were
        // computed from the unrounded value, so it is refused instead.
        if (Math.Round(input.Discount, 4, MidpointRounding.AwayFromZero) != input.Discount)
        {
            throw new DomainValidationException("Lines", "Discount can have at most 4 decimal places.");
        }

        var product = await db.Products.Include(p => p.BaseUnit).SingleOrDefaultAsync(p => p.Id == input.ProductId, ct)
            ?? throw new DomainValidationException("Lines", "Select a valid product.");
        if (!product.IsActive)
        {
            throw new DomainValidationException("Lines", $"{product.Name} is inactive.");
        }

        var productUnit = await db.ProductUnits
            .Include(u => u.Unit)
            .SingleOrDefaultAsync(u => u.Id == input.ProductUnitId, ct)
            ?? throw new DomainValidationException("Lines", "Select a valid unit.");
        if (productUnit.ProductId != product.Id)
        {
            throw new DomainValidationException("Lines", "The selected unit does not belong to this product.");
        }
        var unitDisplay = productUnit.Label ?? productUnit.Unit.Name;
        if (!productUnit.IsActive)
        {
            throw new DomainValidationException("Lines", $"{unitDisplay} is no longer active.");
        }
        if (!productUnit.IsSellable)
        {
            throw new DomainValidationException("Lines", $"{unitDisplay} cannot be sold for {product.Name}.");
        }

        StockQuantity.EnsureEnteredDecimals(input.Qty, productUnit.Unit.DecimalPlaces, unitDisplay, "Lines");

        // The stored factor is snapshotted onto the line; history is never recomputed from configuration.
        var baseQty = input.Qty * productUnit.ConversionFactorToBase;
        StockQuantity.EnsureStorable(baseQty, "Lines");
        StockQuantity.EnsureWithinBaseDecimals(input.Qty, productUnit.Unit.ShortCode, baseQty, product.BaseUnit, "Lines");

        // The open list price is stable here: the product KEY SHARE lock blocks PricingService.SetPriceAsync (FOR UPDATE).
        var listPrice = await db.SellingPrices
            .Where(p => p.ProductUnitId == productUnit.Id && p.EffectiveTo == null)
            .Select(p => (decimal?)p.Price)
            .SingleOrDefaultAsync(ct);
        if (listPrice is not { } list)
        {
            throw new DomainValidationException("Lines", $"{unitDisplay} of {product.Name} has no price. Set a price before selling it.");
        }

        if (Math.Round(input.UnitPrice, 4, MidpointRounding.AwayFromZero) != input.UnitPrice)
        {
            throw new DomainValidationException("Lines", "Unit price has too many decimals (at most 4).");
        }
        var priceDiffers = input.UnitPrice != list;
        if (priceDiffers && !input.OverridePrice)
        {
            throw new DomainValidationException("Lines", $"The price of {product.Name} ({unitDisplay}) is now {list:F2}. Refresh the item and try again.");
        }
        if ((priceDiffers || input.OverridePrice || input.Discount > 0) && !canOverridePrice)
        {
            throw new DomainValidationException("Lines", priceDiffers || input.OverridePrice
                ? $"You are not allowed to change the price of {product.Name}."
                : $"You are not allowed to give a discount on {product.Name}.");
        }

        var gross = input.Qty * input.UnitPrice;
        if (input.Discount > gross)
        {
            throw new DomainValidationException("Lines", $"Discount on {product.Name} cannot exceed the line amount of {gross:F2}.");
        }

        var tax = await db.Taxes.SingleAsync(t => t.Id == product.TaxId, ct);
        var taxableValue = gross - input.Discount;
        var taxAmount = taxableValue * tax.RatePercent / 100;
        var lineTotal = taxableValue + taxAmount;

        return new SalesInvoiceLine
        {
            Id = Guid.NewGuid(),
            SalesInvoiceId = invoice.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            UnitId = productUnit.UnitId,
            UnitCode = productUnit.Unit.ShortCode,
            Qty = input.Qty,
            ConversionFactorToBase = productUnit.ConversionFactorToBase,
            BaseQty = baseQty,
            UnitPrice = input.UnitPrice,
            ListUnitPrice = list,
            PriceOverridden = priceDiffers,
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
        line.CostOfGoodsSold = StockCost.Money(costConsumed);

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
        // Stock on hand and batch remainders are kept in step, so the shortage error is a safety net: never record a
        // sale whose cost (and batch trail) covers only part of the quantity.
        var allocations = await BatchAllocator.ConsumeFifoAsync(db, line.ProductId, line.BaseQty,
            shortBy => new DomainValidationException("Lines", $"Batch stock is short by {StockQuantity.Format(shortBy)} for this item — cannot complete the sale."), ct);

        var costConsumed = 0m;
        foreach (var allocation in allocations)
        {
            costConsumed += allocation.QtyBaseUnits * allocation.UnitCost;
            line.BatchConsumptions.Add(new SalesInvoiceLineBatchConsumption
            {
                Id = Guid.NewGuid(),
                SalesInvoiceLineId = line.Id,
                PurchaseBatchId = allocation.Batch.Id,
                QtyBaseUnits = allocation.QtyBaseUnits,
                UnitCost = allocation.UnitCost,
            });
        }

        return costConsumed;
    }
}
