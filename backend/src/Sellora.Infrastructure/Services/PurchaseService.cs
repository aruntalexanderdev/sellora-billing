using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Payments;
using Sellora.Domain.Purchasing;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

public class PurchaseService(SelloraDbContext db, IExchangeRateService exchangeRateService) : IPurchaseService
{
    public async Task<PurchaseOrder> CreatePurchaseAsync(CreatePurchaseInput input, Guid createdByUserId, CancellationToken ct = default)
    {
        if (input.Lines.Count == 0)
        {
            throw new DomainValidationException("Lines", "Add at least one item.");
        }

        await using var transaction = await BeginAsync(ct);

        if (await Idempotency.FindAsync(db, createdByUserId, Idempotency.Purchase, input.IdempotencyKey, ct) is { } existingId)
        {
            return await db.PurchaseOrders.SingleAsync(p => p.Id == existingId, ct);
        }

        var supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Id == input.SupplierId, ct)
            ?? throw new DomainValidationException("SupplierId", "Select a valid supplier.");
        if (!supplier.IsActive)
        {
            throw new DomainValidationException("SupplierId", "This supplier is inactive.");
        }

        await FinancialAccountRules.EnsureUsableAsync(db, input.PaidAmount > 0 ? input.AccountId : null, ct);

        var purchaseNo = await NextPurchaseNoAsync(ct);

        // Product KEY SHARE first: a concurrent product-unit edit (FOR UPDATE) waits for this purchase to commit, so
        // the factor snapshotted below cannot change underneath it. Then the StockItems locks (fixed order, §5.4).
        await StockLocks.LockProductsKeyShareAsync(db, input.Lines.Select(l => l.ProductId), ct);

        // Stock on hand and its average cost are read-modify-written below, so hold each product's lock to commit.
        await StockLocks.LockStockItemsAsync(db, input.Lines.Select(l => l.ProductId), ct);

        // The supplier's *current* currency becomes this purchase's own snapshot; the rate is resolved for
        // the purchase date and stored on the purchase so later changes to the rate master never touch it.
        var transactionCurrency = await db.Currencies.SingleAsync(c => c.Id == supplier.CurrencyId, ct);
        if (!transactionCurrency.IsActive)
        {
            throw new DomainValidationException("SupplierId", $"This supplier's currency ({transactionCurrency.Code}) is inactive. Update the supplier's currency before purchasing from them.");
        }
        var rateToBase = await exchangeRateService.ResolveRateToBaseAsync(supplier.CurrencyId, input.PurchaseDate, ct);
        var basePrecision = await db.BusinessSettings.Select(s => s.BaseCurrency.DecimalPrecision).SingleAsync(ct);

        var purchase = new PurchaseOrder
        {
            Id = Guid.NewGuid(),
            PurchaseNo = purchaseNo,
            SupplierId = supplier.Id,
            CurrencyId = supplier.CurrencyId,
            ExchangeRateToBase = rateToBase,
            PurchaseDate = input.PurchaseDate,
            SupplierInvoiceNumber = input.SupplierInvoiceNumber,
            Notes = input.Notes,
            PaymentMethod = input.PaymentMethod,
            Status = PurchaseStatus.Completed,
            CreatedByUserId = createdByUserId,
        };

        decimal subtotal = 0, taxTotal = 0, otherChargesTotal = 0, discountTotal = 0, originalGrandTotal = 0;

        foreach (var lineInput in input.Lines)
        {
            var supplierBatchNumber = NormalizeSupplierBatchNumber(lineInput.SupplierBatchNumber);
            var (line, lineSubtotal, originalLineTotal) = await BuildLineAsync(purchase, lineInput, rateToBase, transactionCurrency.DecimalPrecision, basePrecision, ct);
            purchase.Lines.Add(line);

            subtotal += lineSubtotal;
            taxTotal += line.TaxAmount;
            otherChargesTotal += line.OtherCharges;
            discountTotal += line.Discount;
            originalGrandTotal += originalLineTotal;

            await ApplyPurchaseLineToStockAsync(line, purchaseNo, input.PurchaseDate, supplierBatchNumber, createdByUserId, ct);
        }

        purchase.Subtotal = subtotal;
        purchase.TaxTotal = taxTotal;
        purchase.OtherChargesTotal = otherChargesTotal;
        purchase.DiscountTotal = discountTotal;
        purchase.GrandTotal = subtotal + otherChargesTotal - discountTotal + taxTotal;
        purchase.OriginalGrandTotal = Math.Round(originalGrandTotal, transactionCurrency.DecimalPrecision, MidpointRounding.AwayFromZero);
        if (input.PaidAmount > 0)
        {
            RecordPayment(purchase, input.PaidAmount, input.PaymentMethod, input.PurchaseDate, createdByUserId, input.AccountId, transactionCurrency.DecimalPrecision, basePrecision);
        }

        db.PurchaseOrders.Add(purchase);
        Idempotency.Record(db, createdByUserId, Idempotency.Purchase, input.IdempotencyKey, purchase.Id);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        return purchase;
    }

    public async Task<PurchaseOrder?> UpdateHeaderAsync(Guid purchaseOrderId, UpdatePurchaseHeaderInput input, CancellationToken ct = default)
    {
        var purchase = await db.PurchaseOrders
            .SingleOrDefaultAsync(p => p.Id == purchaseOrderId, ct);
        if (purchase is null) return null;

        if (purchase.Status == PurchaseStatus.Cancelled)
        {
            throw new DomainValidationException("Status", "This purchase is cancelled and cannot be edited.");
        }

        purchase.SupplierInvoiceNumber = input.SupplierInvoiceNumber;
        purchase.Notes = input.Notes;

        await db.SaveChangesAsync(ct);
        return purchase;
    }

    public async Task<PurchaseOrder?> AddPaymentAsync(Guid purchaseOrderId, AddPurchasePaymentInput input, Guid paidByUserId, CancellationToken ct = default)
    {
        await using var transaction = await BeginAsync(ct);
        var purchase = await LockPurchaseAsync(purchaseOrderId, ct);
        if (purchase is null) return null;

        var currency = await db.Currencies.SingleAsync(c => c.Id == purchase.CurrencyId, ct);
        var precision = currency.DecimalPrecision;
        var foreign = purchase.ExchangeRateToBase != 1m;
        decimal InPurchaseCurrency(decimal amount) => foreign ? Math.Round(amount, precision, MidpointRounding.AwayFromZero) : amount;

        if (purchase.Status == PurchaseStatus.Cancelled)
        {
            throw new DomainValidationException("Status", "This purchase is cancelled and cannot be paid.");
        }

        await FinancialAccountRules.EnsureUsableAsync(db, input.AccountId, ct);

        var due = InPurchaseCurrency(purchase.OriginalDueAmount);
        if (due <= 0)
        {
            throw new DomainValidationException("Amount", "This purchase is already fully paid.");
        }

        var amount = InPurchaseCurrency(input.Amount);
        if (amount <= 0)
        {
            throw new DomainValidationException("Amount", "Payment amount must be greater than zero.");
        }
        if (InPurchaseCurrency(input.ExpectedDue) != due)
        {
            throw new DomainValidationException("ExpectedDue", $"The amount due has changed to {due.ToString($"F{precision}")}. Reload the purchase and try again.");
        }
        if (amount > due)
        {
            throw new DomainValidationException("Amount", $"Payment cannot exceed the outstanding due of {due.ToString($"F{precision}")} {currency.Code}.");
        }

        var basePrecision = await db.BusinessSettings.Select(s => s.BaseCurrency.DecimalPrecision).SingleAsync(ct);

        RecordPayment(purchase, amount, input.PaymentMethod, DateOnly.FromDateTime(DateTime.UtcNow), paidByUserId, input.AccountId, precision, basePrecision);

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        purchase.Currency = currency;
        return purchase;
    }

    // FOR UPDATE serialises concurrent money operations on the same purchase, so the due or refundable amount they
    // are checked against cannot go stale between the read and the save. The in-memory test provider has neither
    // transactions nor row locks, hence the relational check.
    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginAsync(CancellationToken ct) =>
        db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;

    private async Task<PurchaseOrder?> LockPurchaseAsync(Guid purchaseOrderId, CancellationToken ct) =>
        db.Database.IsRelational()
            ? await db.PurchaseOrders
                .FromSqlInterpolated($"SELECT * FROM \"PurchaseOrders\" WHERE \"Id\" = {purchaseOrderId} FOR UPDATE")
                .SingleOrDefaultAsync(ct)
            : await db.PurchaseOrders.SingleOrDefaultAsync(p => p.Id == purchaseOrderId, ct);

    // A payment is recorded through here. See RecordEntry for the ledger mechanics.
    private void RecordPayment(PurchaseOrder purchase, decimal amount, PaymentMethod method, DateOnly paymentDate, Guid userId, Guid? accountId, int transactionPrecision, int basePrecision) =>
        RecordEntry(purchase, PaymentEntryKind.Payment, amount, method, paymentDate, userId, accountId, null, null, null, transactionPrecision, basePrecision);

    // The one place money is recorded against a purchase. It appends a ledger row and moves the purchase's running
    // totals; an existing entry is never touched, and every entry writes its own financial transaction. The amount
    // is in the transaction currency and kept as entered; the base-currency figure uses the purchase's captured
    // rate. Each row's BaseAmount is the movement in the running base total (positive for a payment, and the size
    // of the drop for a refund), so the rows always net to PaidAmount. A reversal copies the original payment's
    // amount and base amount exactly instead of converting again.
    private void RecordEntry(
        PurchaseOrder purchase, PaymentEntryKind kind, decimal amount, PaymentMethod method, DateOnly date, Guid userId,
        Guid? accountId, string? reason, Guid? reversesPaymentId, Guid? purchaseReturnId, int transactionPrecision, int basePrecision,
        decimal? reversedBaseAmount = null)
    {
        var basePaidBefore = purchase.PaidAmount;
        var rounded = purchase.ExchangeRateToBase == 1m ? amount : Math.Round(amount, transactionPrecision, MidpointRounding.AwayFromZero);

        decimal baseAmount;
        if (kind == PaymentEntryKind.Reversal)
        {
            purchase.OriginalPaidAmount -= rounded;
            purchase.PaidAmount -= reversedBaseAmount!.Value;
            baseAmount = reversedBaseAmount.Value;
        }
        else
        {
            var newOriginalPaid = kind == PaymentEntryKind.Payment ? purchase.OriginalPaidAmount + rounded : purchase.OriginalPaidAmount - rounded;
            ApplyPaidTotal(purchase, newOriginalPaid, transactionPrecision, basePrecision);
            baseAmount = Math.Abs(purchase.PaidAmount - basePaidBefore);
        }
        if (kind == PaymentEntryKind.Payment) purchase.PaymentMethod = method;

        var payment = new PurchasePayment
        {
            Id = Guid.NewGuid(),
            PurchaseOrderId = purchase.Id,
            Kind = kind,
            ReversesPaymentId = reversesPaymentId,
            PurchaseReturnId = purchaseReturnId,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            PaymentDate = date,
            Amount = rounded,
            ExchangeRateToBase = purchase.ExchangeRateToBase,
            BaseAmount = baseAmount,
            PaymentMethod = method,
            CreatedByUserId = userId,
        };
        purchase.Payments.Add(payment);
        db.PurchasePayments.Add(payment);
        db.FinancialTransactions.Add(FinancialTransaction.ForPurchasePayment(payment, purchase.CurrencyId, accountId));
    }

    // Converts the running paid total (transaction currency) to the base currency at the purchase's captured rate.
    // What is owed is the net total — the grand total less returned goods, or nothing once cancelled. Paying off
    // exactly the net total is booked as exactly the base net total, so rounding can never leave a stray paisa of
    // due on a settled foreign purchase; anything paid beyond it is converted on top.
    private static void ApplyPaidTotal(PurchaseOrder purchase, decimal amount, int transactionPrecision, int basePrecision)
    {
        if (purchase.ExchangeRateToBase == 1m)
        {
            purchase.OriginalPaidAmount = amount;
            purchase.PaidAmount = amount;
            return;
        }

        var cancelled = purchase.Status == PurchaseStatus.Cancelled;
        var netOriginal = cancelled ? 0 : purchase.OriginalNetTotal;
        var netBase = cancelled ? 0 : purchase.NetTotal;

        var original = Math.Round(amount, transactionPrecision, MidpointRounding.AwayFromZero);
        purchase.OriginalPaidAmount = original;
        if (original >= netOriginal)
        {
            purchase.PaidAmount = netBase + Math.Round((original - netOriginal) * purchase.ExchangeRateToBase, basePrecision, MidpointRounding.AwayFromZero);
            return;
        }

        // Rounding to the base precision must never make a part-payment look like a full one.
        var converted = Math.Round(original * purchase.ExchangeRateToBase, basePrecision, MidpointRounding.AwayFromZero);
        var oneBaseUnit = 1m / (decimal)Math.Pow(10, basePrecision);
        purchase.PaidAmount = converted >= netBase && netBase > 0 ? netBase - oneBaseUnit : converted;
    }

    public async Task<PurchaseOrder?> AddRefundAsync(Guid purchaseOrderId, AddPurchaseRefundInput input, Guid userId, CancellationToken ct = default)
    {
        await using var transaction = await BeginAsync(ct);
        var purchase = await LockPurchaseAsync(purchaseOrderId, ct);
        if (purchase is null) return null;

        var currency = await db.Currencies.SingleAsync(c => c.Id == purchase.CurrencyId, ct);
        var basePrecision = await db.BusinessSettings.Select(s => s.BaseCurrency.DecimalPrecision).SingleAsync(ct);

        await FinancialAccountRules.EnsureUsableAsync(db, input.AccountId, ct);
        if (input.PurchaseReturnId is { } returnId
            && !await db.PurchaseReturns.AnyAsync(r => r.Id == returnId && r.PurchaseOrderId == purchase.Id, ct))
        {
            throw new DomainValidationException("PurchaseReturnId", "Select a return that belongs to this purchase.");
        }

        ValidateRefund(purchase, currency, input.Amount, input.ExpectedRefundable);
        RecordEntry(purchase, PaymentEntryKind.Refund, input.Amount, input.PaymentMethod, DateOnly.FromDateTime(DateTime.UtcNow),
            userId, input.AccountId, input.Reason, null, input.PurchaseReturnId, currency.DecimalPrecision, basePrecision);

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        purchase.Currency = currency;
        return purchase;
    }

    private static void ValidateRefund(PurchaseOrder purchase, Currency currency, decimal amount, decimal? expectedRefundable)
    {
        var precision = currency.DecimalPrecision;
        var refundable = Math.Round(purchase.OriginalRefundableAmount, precision, MidpointRounding.AwayFromZero);
        var rounded = Math.Round(amount, precision, MidpointRounding.AwayFromZero);
        if (refundable <= 0)
        {
            throw new DomainValidationException("Amount", "There is nothing to refund on this purchase.");
        }
        if (rounded <= 0)
        {
            throw new DomainValidationException("Amount", "Refund amount must be greater than zero.");
        }
        if (expectedRefundable is { } expected && Math.Round(expected, precision, MidpointRounding.AwayFromZero) != refundable)
        {
            throw new DomainValidationException("ExpectedRefundable", $"The refundable amount has changed to {refundable.ToString($"F{precision}")}. Reload the purchase and try again.");
        }
        if (rounded > refundable)
        {
            throw new DomainValidationException("Amount", $"Refund cannot exceed the refundable amount of {refundable.ToString($"F{precision}")} {currency.Code}.");
        }
    }

    public async Task<PurchaseOrder?> ReversePaymentAsync(Guid purchaseOrderId, Guid paymentId, ReversePaymentInput input, Guid userId, CancellationToken ct = default)
    {
        await using var transaction = await BeginAsync(ct);
        var purchase = await LockPurchaseAsync(purchaseOrderId, ct);
        if (purchase is null) return null;

        var payment = await db.PurchasePayments.SingleOrDefaultAsync(p => p.Id == paymentId && p.PurchaseOrderId == purchase.Id, ct)
            ?? throw new DomainValidationException("PaymentId", "Payment not found on this purchase.");
        if (payment.Kind != PaymentEntryKind.Payment)
        {
            throw new DomainValidationException("PaymentId", "Only a payment can be reversed.");
        }
        if (await db.PurchasePayments.AnyAsync(p => p.ReversesPaymentId == payment.Id, ct))
        {
            throw new DomainValidationException("PaymentId", "This payment has already been reversed.");
        }
        if (purchase.OriginalPaidAmount < payment.Amount || purchase.PaidAmount < payment.BaseAmount)
        {
            throw new DomainValidationException("PaymentId", "This payment can't be reversed because part of it has since been refunded.");
        }

        var currency = await db.Currencies.SingleAsync(c => c.Id == purchase.CurrencyId, ct);
        var basePrecision = await db.BusinessSettings.Select(s => s.BaseCurrency.DecimalPrecision).SingleAsync(ct);

        // The reversal goes back through the same account the payment came out of.
        var accountId = await db.FinancialTransactions.Where(t => t.PurchasePaymentId == payment.Id).Select(t => t.AccountId).SingleOrDefaultAsync(ct);
        RecordEntry(purchase, PaymentEntryKind.Reversal, payment.Amount, payment.PaymentMethod, DateOnly.FromDateTime(DateTime.UtcNow),
            userId, accountId, input.Reason, payment.Id, null, currency.DecimalPrecision, basePrecision, reversedBaseAmount: payment.BaseAmount);

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        purchase.Currency = currency;
        return purchase;
    }

    public async Task<PurchaseReturn> CreateReturnAsync(CreatePurchaseReturnInput input, Guid userId, CancellationToken ct = default)
    {
        if (input.Lines.Count == 0)
        {
            throw new DomainValidationException("Lines", "Choose at least one item to return.");
        }
        if (input.Lines.Select(l => l.PurchaseOrderLineId).Distinct().Count() != input.Lines.Count)
        {
            throw new DomainValidationException("Lines", "Each item can only be listed once.");
        }
        if (input.ReturnDate > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
        {
            throw new DomainValidationException("ReturnDate", "Return date cannot be in the future.");
        }

        await using var transaction = await BeginAsync(ct);
        var purchase = await LockPurchaseAsync(input.PurchaseOrderId, ct)
            ?? throw new DomainValidationException("PurchaseOrderId", "Select a valid purchase.");
        if (purchase.Status == PurchaseStatus.Cancelled)
        {
            throw new DomainValidationException("Status", "This purchase is cancelled, so nothing can be returned against it.");
        }
        if (input.ReturnDate < purchase.PurchaseDate)
        {
            throw new DomainValidationException("ReturnDate", "Return date cannot be before the purchase date.");
        }

        var currency = await db.Currencies.SingleAsync(c => c.Id == purchase.CurrencyId, ct);
        var basePrecision = await db.BusinessSettings.Select(s => s.BaseCurrency.DecimalPrecision).SingleAsync(ct);
        // Lock the stock rows before reading the batches, so the remaining quantities checked below are current.
        await StockLocks.LockStockItemsAsync(db, await db.PurchaseOrderLines.Where(l => l.PurchaseOrderId == purchase.Id).Select(l => l.ProductId).ToListAsync(ct), ct);
        var lines = await db.PurchaseOrderLines.Include(l => l.Product).Include(l => l.Batch)
            .Where(l => l.PurchaseOrderId == purchase.Id).ToListAsync(ct);
        var priorReturns = await db.PurchaseReturnLines
            .Where(r => r.PurchaseOrderLine.PurchaseOrderId == purchase.Id).ToListAsync(ct);

        var purchaseReturn = new PurchaseReturn
        {
            Id = Guid.NewGuid(),
            ReturnNo = await NextReturnNoAsync(ct),
            PurchaseOrderId = purchase.Id,
            ReturnDate = input.ReturnDate,
            Reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim(),
            CreatedByUserId = userId,
        };

        foreach (var requested in input.Lines)
        {
            var line = lines.SingleOrDefault(l => l.Id == requested.PurchaseOrderLineId)
                ?? throw new DomainValidationException("Lines", "One of the items does not belong to this purchase.");
            var batch = line.Batch ?? throw new InvalidOperationException($"Purchase line {line.Id} has no batch.");

            var qty = Math.Round(requested.Qty, 3, MidpointRounding.AwayFromZero);
            if (qty <= 0)
            {
                throw new DomainValidationException("Lines", $"Quantity for {line.Product.Name} must be greater than zero.");
            }

            var prior = priorReturns.Where(r => r.PurchaseOrderLineId == line.Id).ToList();
            var returnable = line.Qty - prior.Sum(r => r.Qty);
            if (qty > returnable)
            {
                throw new DomainValidationException("Lines", $"Only {returnable:0.###} of {line.Product.Name} can still be returned.");
            }

            // Returning whatever is left takes the exact remainder, so per-line rounding can never leave a stray
            // paisa of credit unissued (or issue one too many) once an item is fully returned.
            var last = qty == returnable;
            var baseQty = last ? line.BaseQty - prior.Sum(r => r.BaseQty) : qty * line.ConversionFactorToBase;
            StockQuantity.EnsureStorable(baseQty, "Lines");
            if (baseQty > batch.RemainingBaseQty)
            {
                var inStock = batch.RemainingBaseQty / line.ConversionFactorToBase;
                throw new DomainValidationException("Lines", $"Only {inStock:0.###} of {line.Product.Name} from this purchase is still in stock to return.");
            }

            var taxable = last ? line.TaxableValue - prior.Sum(r => r.TaxableValue) : Math.Round(line.TaxableValue * qty / line.Qty, basePrecision, MidpointRounding.AwayFromZero);
            var tax = last ? line.TaxAmount - prior.Sum(r => r.TaxAmount) : Math.Round(line.TaxAmount * qty / line.Qty, basePrecision, MidpointRounding.AwayFromZero);

            var returnLine = new PurchaseReturnLine
            {
                Id = Guid.NewGuid(),
                PurchaseReturnId = purchaseReturn.Id,
                PurchaseOrderLineId = line.Id,
                ProductId = line.ProductId,
                Qty = qty,
                BaseQty = baseQty,
                TaxableValue = taxable,
                TaxAmount = tax,
                LineTotal = taxable + tax,
                CostRemoved = baseQty * batch.UnitCost,
            };
            purchaseReturn.Lines.Add(returnLine);
            priorReturns.Add(returnLine);
            purchaseReturn.TaxableTotal += taxable;
            purchaseReturn.TaxTotal += tax;

            // Take the stock out of the batch and off the product's on-hand figures, valuing it at the batch cost.
            batch.RemainingBaseQty -= baseQty;
            var stockItem = await db.StockItems.SingleAsync(s => s.ProductId == line.ProductId, ct);
            var qtyAfter = stockItem.QuantityOnHand - baseQty;
            if (qtyAfter > 0)
            {
                var valueAfter = stockItem.QuantityOnHand * stockItem.AverageCost - baseQty * batch.UnitCost;
                stockItem.AverageCost = Math.Max(0, valueAfter / qtyAfter);
                stockItem.QuantityOnHand = qtyAfter;
            }
            else
            {
                stockItem.QuantityOnHand = 0;
                stockItem.AverageCost = 0;
            }

            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                ProductId = line.ProductId,
                Type = StockMovementType.PurchaseReturn,
                PurchaseBatchId = batch.Id,
                QuantityBaseUnits = -baseQty,
                ReferenceType = "PurchaseReturn",
                ReferenceId = purchaseReturn.Id,
                ReferenceNo = purchaseReturn.ReturnNo,
                UserId = userId,
                Notes = $"Returned to supplier from {purchase.PurchaseNo}",
            });
        }

        purchaseReturn.Total = purchaseReturn.TaxableTotal + purchaseReturn.TaxTotal;

        // The same credit in the purchase's own currency. Returning the last of the goods credits exactly what is
        // left of the original total; otherwise the base credit's share of the purchase is applied to it.
        decimal originalCredit;
        var originalLeft = purchase.OriginalGrandTotal - purchase.OriginalReturnedAmount;
        if (purchase.ExchangeRateToBase == 1m)
        {
            originalCredit = purchaseReturn.Total;
        }
        else if (purchase.ReturnedAmount + purchaseReturn.Total >= purchase.GrandTotal)
        {
            originalCredit = originalLeft;
        }
        else
        {
            originalCredit = Math.Min(originalLeft,
                Math.Round(purchaseReturn.Total / purchase.GrandTotal * purchase.OriginalGrandTotal, currency.DecimalPrecision, MidpointRounding.AwayFromZero));
        }
        purchaseReturn.OriginalTotal = originalCredit;
        purchase.ReturnedAmount += purchaseReturn.Total;
        purchase.OriginalReturnedAmount += originalCredit;
        db.PurchaseReturns.Add(purchaseReturn);

        if (input.Refund is { } refund)
        {
            await FinancialAccountRules.EnsureUsableAsync(db, refund.AccountId, ct);
            ValidateRefund(purchase, currency, refund.Amount, null);
            RecordEntry(purchase, PaymentEntryKind.Refund, refund.Amount, refund.PaymentMethod, input.ReturnDate,
                userId, refund.AccountId, refund.Reason, null, purchaseReturn.Id, currency.DecimalPrecision, basePrecision);
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return purchaseReturn;
    }

    public async Task CancelPurchaseAsync(Guid purchaseOrderId, Guid cancelledByUserId, CancellationToken ct = default)
    {
        await using var transaction = await BeginAsync(ct);
        _ = await LockPurchaseAsync(purchaseOrderId, ct) ?? throw new DomainValidationException("Id", "Purchase not found.");
        await StockLocks.LockStockItemsAsync(db, await db.PurchaseOrderLines.Where(l => l.PurchaseOrderId == purchaseOrderId).Select(l => l.ProductId).ToListAsync(ct), ct);

        var purchase = await db.PurchaseOrders
            .Include(p => p.Lines).ThenInclude(l => l.Batch)
            .SingleAsync(p => p.Id == purchaseOrderId, ct);

        if (purchase.Status == PurchaseStatus.Cancelled)
        {
            throw new DomainValidationException("Status", "This purchase has already been cancelled.");
        }
        if (await db.PurchaseReturns.AnyAsync(r => r.PurchaseOrderId == purchase.Id, ct))
        {
            throw new DomainValidationException("Status", "Cannot cancel — items from this purchase have been returned.");
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
                PurchaseBatchId = batch.Id,
                QuantityBaseUnits = -batch.PurchasedBaseQty,
                ReferenceType = "PurchaseOrder",
                ReferenceId = purchase.Id,
                ReferenceNo = purchase.PurchaseNo,
                UserId = cancelledByUserId,
                Notes = "Purchase cancelled",
            });
        }

        purchase.Status = PurchaseStatus.Cancelled;
        purchase.CancelledAt = DateTimeOffset.UtcNow;
        purchase.CancelledByUserId = cancelledByUserId;

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    // The supplier's lot number is kept exactly as entered; a blank entry just means "none given".
    private static string? NormalizeSupplierBatchNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 60)
        {
            throw new DomainValidationException("SupplierBatchNumber", "Supplier batch number can be at most 60 characters.");
        }
        return value;
    }

    private async Task<string> NextReturnNoAsync(CancellationToken ct)
    {
        await StockLocks.LockDocumentNumberAsync(db, "PRT", ct);
        return $"PRT-{await db.PurchaseReturns.CountAsync(ct) + 1:D4}";
    }

    private async Task<string> NextPurchaseNoAsync(CancellationToken ct)
    {
        await StockLocks.LockDocumentNumberAsync(db, "PUR", ct);
        var count = await db.PurchaseOrders.CountAsync(ct);
        return $"PUR-{count + 1:D4}";
    }

    // Returns the line with every monetary field in the business base currency, plus the line's pre-tax
    // subtotal (also base currency) for the header, and the line total in the original transaction
    // currency for the purchase's OriginalGrandTotal. The user's rate/charges/discount are entered in the
    // transaction currency; a real conversion (rate != 1) rounds each converted component to the base
    // currency's precision so header totals always equal the sum of their lines.
    private async Task<(PurchaseOrderLine Line, decimal Subtotal, decimal OriginalLineTotal)> BuildLineAsync(
        PurchaseOrder purchase, PurchaseLineInput input, decimal rateToBase, int transactionPrecision, int basePrecision, CancellationToken ct)
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
        if (!productUnit.IsPurchasable)
        {
            throw new DomainValidationException("Lines", $"{unitDisplay} cannot be purchased for {product.Name}.");
        }

        var tax = await db.Taxes.SingleAsync(t => t.Id == product.TaxId, ct);

        // The stored factor is snapshotted onto the line; history is never recomputed from configuration.
        var baseQty = input.Qty * productUnit.ConversionFactorToBase;
        StockQuantity.EnsureStorable(baseQty, "Lines");
        var foreign = rateToBase != 1m;

        // A foreign invoice's figures exist in whole units of its own currency, so each is rounded to that
        // currency's precision first (tax included, as the supplier's invoice would print it) and only
        // those rounded figures are converted. This keeps original × rate consistent with the base value.
        decimal InOriginal(decimal amount) => foreign ? Math.Round(amount, transactionPrecision, MidpointRounding.AwayFromZero) : amount;
        decimal ToBase(decimal amount) => foreign ? Math.Round(amount * rateToBase, basePrecision, MidpointRounding.AwayFromZero) : amount;

        var originalSubtotal = InOriginal(input.Qty * input.Rate);
        var originalOtherCharges = InOriginal(input.OtherCharges);
        var originalDiscount = InOriginal(input.Discount);
        var originalTaxable = originalSubtotal + originalOtherCharges - originalDiscount;
        var originalTax = InOriginal(originalTaxable * tax.RatePercent / 100);
        var originalLineTotal = originalTaxable + originalTax;

        var subtotal = ToBase(originalSubtotal);
        var otherCharges = ToBase(originalOtherCharges);
        var discount = ToBase(originalDiscount);
        var taxableValue = subtotal + otherCharges - discount;
        var taxAmount = ToBase(originalTax);
        var lineTotal = taxableValue + taxAmount;
        var unitCost = baseQty > 0 ? taxableValue / baseQty : 0;

        var line = new PurchaseOrderLine
        {
            Id = Guid.NewGuid(),
            PurchaseOrderId = purchase.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            Qty = input.Qty,
            ConversionFactorToBase = productUnit.ConversionFactorToBase,
            BaseQty = baseQty,
            Rate = rateToBase == 1m ? input.Rate : Math.Round(input.Rate * rateToBase, 4, MidpointRounding.AwayFromZero),
            TaxId = tax.Id,
            TaxRatePercent = tax.RatePercent,
            OtherCharges = otherCharges,
            Discount = discount,
            TaxableValue = taxableValue,
            TaxAmount = taxAmount,
            LineTotal = lineTotal,
            UnitCost = unitCost,
        };

        return (line, subtotal, originalLineTotal);
    }

    private async Task ApplyPurchaseLineToStockAsync(PurchaseOrderLine line, string purchaseNo, DateOnly receivedDate, string? supplierBatchNumber, Guid userId, CancellationToken ct)
    {
        var batch = new PurchaseBatch
        {
            Id = Guid.NewGuid(),
            PurchaseOrderLineId = line.Id,
            ProductId = line.ProductId,
            SupplierBatchNumber = supplierBatchNumber,
            ReceivedDate = receivedDate,
            PurchasedBaseQty = line.BaseQty,
            RemainingBaseQty = line.BaseQty,
            UnitCost = line.UnitCost,
        };
        db.PurchaseBatches.Add(batch);

        db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            ProductId = line.ProductId,
            PurchaseBatchId = batch.Id,
            Type = StockMovementType.Purchase,
            QuantityBaseUnits = line.BaseQty,
            ReferenceType = "PurchaseOrder",
            ReferenceId = line.PurchaseOrderId,
            ReferenceNo = purchaseNo,
            UserId = userId,
        });

        var stockItem = db.StockItems.Local.FirstOrDefault(s => s.ProductId == line.ProductId)
            ?? await db.StockItems.SingleOrDefaultAsync(s => s.ProductId == line.ProductId, ct);
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
