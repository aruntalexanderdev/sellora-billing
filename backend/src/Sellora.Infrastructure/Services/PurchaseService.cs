using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
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

        var supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Id == input.SupplierId, ct)
            ?? throw new DomainValidationException("SupplierId", "Select a valid supplier.");
        if (!supplier.IsActive)
        {
            throw new DomainValidationException("SupplierId", "This supplier is inactive.");
        }

        var purchaseNo = await NextPurchaseNoAsync(ct);

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
            var (line, lineSubtotal, originalLineTotal) = await BuildLineAsync(purchase, lineInput, rateToBase, transactionCurrency.DecimalPrecision, basePrecision, ct);
            purchase.Lines.Add(line);

            subtotal += lineSubtotal;
            taxTotal += line.TaxAmount;
            otherChargesTotal += line.OtherCharges;
            discountTotal += line.Discount;
            originalGrandTotal += originalLineTotal;

            await ApplyPurchaseLineToStockAsync(line, purchaseNo, createdByUserId, ct);
        }

        purchase.Subtotal = subtotal;
        purchase.TaxTotal = taxTotal;
        purchase.OtherChargesTotal = otherChargesTotal;
        purchase.DiscountTotal = discountTotal;
        purchase.GrandTotal = subtotal + otherChargesTotal - discountTotal + taxTotal;
        purchase.OriginalGrandTotal = Math.Round(originalGrandTotal, transactionCurrency.DecimalPrecision, MidpointRounding.AwayFromZero);
        if (input.PaidAmount > 0)
        {
            RecordPayment(purchase, input.PaidAmount, input.PaymentMethod, input.PurchaseDate, createdByUserId, transactionCurrency.DecimalPrecision, basePrecision);
        }

        db.PurchaseOrders.Add(purchase);
        await db.SaveChangesAsync(ct);

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
        // FOR UPDATE serialises concurrent payments on the same purchase, so the due they are checked
        // against cannot go stale between the read and the save. The in-memory test provider has neither
        // transactions nor row locks, hence the relational check.
        var relational = db.Database.IsRelational();
        await using var transaction = relational ? await db.Database.BeginTransactionAsync(ct) : null;

        var purchase = relational
            ? await db.PurchaseOrders
                .FromSqlInterpolated($"SELECT * FROM \"PurchaseOrders\" WHERE \"Id\" = {purchaseOrderId} FOR UPDATE")
                .SingleOrDefaultAsync(ct)
            : await db.PurchaseOrders.SingleOrDefaultAsync(p => p.Id == purchaseOrderId, ct);
        if (purchase is null) return null;

        var currency = await db.Currencies.SingleAsync(c => c.Id == purchase.CurrencyId, ct);
        var precision = currency.DecimalPrecision;
        var foreign = purchase.ExchangeRateToBase != 1m;
        decimal InPurchaseCurrency(decimal amount) => foreign ? Math.Round(amount, precision, MidpointRounding.AwayFromZero) : amount;

        if (purchase.Status == PurchaseStatus.Cancelled)
        {
            throw new DomainValidationException("Status", "This purchase is cancelled and cannot be paid.");
        }

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

        RecordPayment(purchase, amount, input.PaymentMethod, DateOnly.FromDateTime(DateTime.UtcNow), paidByUserId, precision, basePrecision);

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        purchase.Currency = currency;
        return purchase;
    }

    // The one place a payment is recorded. It appends a ledger row and moves the purchase's running totals;
    // an existing payment is never touched. The amount is entered in the transaction currency and kept as
    // entered; the base-currency figure uses the purchase's captured rate. A payment that clears the whole
    // original total is booked so the base paid total is exactly the base grand total, so rounding can never
    // leave a stray paisa of due behind on a fully-paid foreign purchase. Each row's BaseAmount is the movement
    // in the running base total, so the rows always sum to PaidAmount.
    private void RecordPayment(PurchaseOrder purchase, decimal amount, PaymentMethod method, DateOnly paymentDate, Guid userId, int transactionPrecision, int basePrecision)
    {
        var basePaidBefore = purchase.PaidAmount;
        ApplyPaidTotal(purchase, purchase.OriginalPaidAmount + amount, transactionPrecision, basePrecision);
        purchase.PaymentMethod = method;

        var payment = new PurchasePayment
        {
            Id = Guid.NewGuid(),
            PurchaseOrderId = purchase.Id,
            PaymentDate = paymentDate,
            Amount = purchase.ExchangeRateToBase == 1m ? amount : Math.Round(amount, transactionPrecision, MidpointRounding.AwayFromZero),
            ExchangeRateToBase = purchase.ExchangeRateToBase,
            BaseAmount = purchase.PaidAmount - basePaidBefore,
            PaymentMethod = method,
            CreatedByUserId = userId,
        };
        purchase.Payments.Add(payment);
        db.PurchasePayments.Add(payment);
        db.FinancialTransactions.Add(FinancialTransaction.ForPurchasePayment(payment, purchase.CurrencyId));
    }

    private static void ApplyPaidTotal(PurchaseOrder purchase, decimal amount, int transactionPrecision, int basePrecision)
    {
        if (purchase.ExchangeRateToBase == 1m)
        {
            purchase.OriginalPaidAmount = amount;
            purchase.PaidAmount = amount;
            return;
        }

        var original = Math.Round(amount, transactionPrecision, MidpointRounding.AwayFromZero);
        purchase.OriginalPaidAmount = original;
        if (original >= purchase.OriginalGrandTotal)
        {
            purchase.PaidAmount = purchase.GrandTotal;
            return;
        }

        // Rounding to the base precision must never make a part-payment look like a full one.
        var converted = Math.Round(original * purchase.ExchangeRateToBase, basePrecision, MidpointRounding.AwayFromZero);
        var oneBaseUnit = 1m / (decimal)Math.Pow(10, basePrecision);
        purchase.PaidAmount = converted >= purchase.GrandTotal && purchase.GrandTotal > 0 ? purchase.GrandTotal - oneBaseUnit : converted;
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

        var sellingMode = await db.ProductSellingModes.SingleOrDefaultAsync(m => m.Id == input.SellingModeId, ct)
            ?? throw new DomainValidationException("Lines", "Select a valid unit.");
        if (sellingMode.ProductId != product.Id)
        {
            throw new DomainValidationException("Lines", "The selected unit does not belong to this product.");
        }

        var tax = await db.Taxes.SingleAsync(t => t.Id == product.TaxId, ct);

        var baseQty = input.Qty * sellingMode.ConversionFactorToBase;
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
            SellingModeId = sellingMode.Id,
            Qty = input.Qty,
            ConversionFactorToBase = sellingMode.ConversionFactorToBase,
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
