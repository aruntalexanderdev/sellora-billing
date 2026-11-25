using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Payments;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

public class PurchaseService(SelloraDbContext db, IExchangeRateService exchangeRateService, IEffectivePermissionService? permissions = null, ITaxResolver? taxResolver = null) : IPurchaseService
{
    private readonly ITaxResolver resolver = taxResolver ?? new TaxResolver(db);

    public async Task<PurchaseOrder> CreatePurchaseAsync(CreatePurchaseInput input, Guid createdByUserId, CancellationToken ct = default)
    {
        if (input.Lines.Count == 0)
        {
            throw new DomainValidationException("Lines", "Add at least one item.");
        }

        await using var transaction = await BeginAsync(ct);

        if (await Idempotency.FindAsync(db, createdByUserId, Idempotency.Purchase, input.IdempotencyKey, ct) is { } existingId)
        {
            return await db.PurchaseOrders.SingleOrDefaultAsync(p => p.Id == existingId, ct)
                ?? throw new DomainValidationException("IdempotencyKey", "This request was already processed, but its result is no longer visible with your current branch access.");
        }

        var supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Id == input.SupplierId, ct)
            ?? throw new DomainValidationException("SupplierId", "Select a valid supplier.");
        if (!supplier.IsActive)
        {
            throw new DomainValidationException("SupplierId", "This supplier is inactive.");
        }

        var branch = await BranchRules.ResolveWorkingBranchAsync(db, ct);
        await FinancialAccountRules.EnsureUsableAsync(db, input.PaidAmount > 0 ? input.AccountId : null, branch.Id, ct);

        var purchaseNo = await DocumentNumbers.NextNumberAsync(db, DocumentSeries.Purchase, branch, ct);

        // Product KEY SHARE first: a concurrent product-unit edit (FOR UPDATE) waits for this purchase to commit, so
        // the factor snapshotted below cannot change underneath it. Then the StockItems locks (fixed order, §5.4).
        // Phase 8b: received at the requested location, else the working branch's store (company-level locations need
        // Purchase.Create company-wide). Resolved before the locks, then re-read and re-checked under the location lock.
        var companyLevel = await StockLocationRules.HasCompanyLevelAsync(permissions, "Purchase.Create", ct);
        var locationId = await StockLocationRules.ResolveAsync(db, PostingKind.Purchase, input.StockLocationId, branch.Id, companyLevel, ct);
        await StockLocks.LockProductsKeyShareAsync(db, input.Lines.Select(l => l.ProductId), ct);

        // TP-3: place of supply (BR12) and the rate sets valid on the purchase date with the purchase profile (BR5, BR7). The profiles are then locked
        // FOR KEY SHARE (after the products, before the locations and StockItems: no new deadlock path) so a version cannot be superseded under this
        // purchase, and the versions are resolved again under the lock (TP3-Q6).
        var place = await resolver.ResolvePlaceOfSupplyAsync(TaxDirection.Purchase, branch.Id, supplier.Id, ct);
        var productIds = input.Lines.Select(l => l.ProductId).ToList();
        var rates = await resolver.ResolveRateSetsAsync(TaxDirection.Purchase, place.CountryId, input.PurchaseDate, productIds, "lines", ct);
        await StockLocks.LockTaxProfilesKeyShareAsync(db, rates.Values.Select(r => r.Rates.ProfileId), ct);
        if (db.Database.IsRelational())
        {
            var again = await resolver.ResolveRateSetsAsync(TaxDirection.Purchase, place.CountryId, input.PurchaseDate, productIds, "lines", ct);
            if (again.Any(kv => !rates.TryGetValue(kv.Key, out var first) || first.Rates.VersionId != kv.Value.Rates.VersionId || first.Rates.ProfileId != kv.Value.Rates.ProfileId))
            {
                throw new DomainValidationException("lines", "A tax rate changed while the purchase was being saved. Review the tax and save again.");
            }
        }

        await StockLocks.LockLocationsKeyShareAsync(db, [locationId], ct);
        await StockLocationRules.ResolveAsync(db, PostingKind.Purchase, locationId, branch.Id, companyLevel, ct);

        // Stock on hand and its average cost are read-modify-written below, so hold each (location, product) lock to commit.
        // The rows are created (QOH 0) first if this is a product's first stock-in, so the lock always has a row to hold.
        var stockItems = await StockLocks.EnsureAndLockStockItemsAsync(db, input.Lines.Select(l => (locationId, l.ProductId)), ct);

        // The supplier's *current* currency becomes this purchase's own snapshot; the rate is resolved for
        // the purchase date and stored on the purchase so later changes to the rate master never touch it.
        var transactionCurrency = await db.Currencies.SingleAsync(c => c.Id == supplier.CurrencyId, ct);
        if (!transactionCurrency.IsActive)
        {
            throw new DomainValidationException("SupplierId", $"This supplier's currency ({transactionCurrency.Code}) is inactive. Update the supplier's currency before purchasing from them.");
        }
        var rateToBase = await exchangeRateService.ResolveRateToBaseAsync(supplier.CurrencyId, input.PurchaseDate, ct);
        var basePrecision = (await db.GetCurrentCompanySettingsAsync(ct)).BaseCurrencyDecimalPrecision;

        var purchase = new PurchaseOrder
        {
            Id = Guid.NewGuid(),
            PurchaseNo = purchaseNo,
            BranchId = branch.Id,
            StockLocationId = locationId,
            SupplierId = supplier.Id,
            CurrencyId = supplier.CurrencyId,
            ExchangeRateToBase = rateToBase,
            PurchaseDate = input.PurchaseDate,
            SupplierInvoiceNumber = input.SupplierInvoiceNumber,
            Notes = input.Notes,
            PaymentMethod = input.PaymentMethod,
            Status = PurchaseStatus.Completed,
            CreatedByUserId = createdByUserId,
            RatesIncludeTax = input.RatesIncludeTax,
            BillDiscountPercent = input.BillDiscountPercent,
            PlaceOfSupply = place.Place,
            PlaceOfSupplyStateId = place.OwnStateId,
            SupplierStateId = place.PartyStateId,
            PartyTaxStatus = PartyTaxStatus.Unspecified,
        };

        // ---- 1. validate every line (units, quantities), collecting the errors of all lines
        var errors = new List<FieldError>();
        var prepared = new List<PreparedLine>(input.Lines.Count);
        for (var i = 0; i < input.Lines.Count; i++)
        {
            try
            {
                prepared.Add(await PrepareLineAsync(i, input.Lines[i], ct));
            }
            catch (DomainValidationException ex)
            {
                errors.AddRange(ex.Errors);
            }
        }
        if (errors.Count > 0) throw new DomainValidationException(errors);

        // ---- 2. the calculator, in the document currency (BR22): its precision is the supplier currency's
        var calc = Calculate(input, place, rates, prepared, transactionCurrency.DecimalPrecision);

        // ---- 3. convert each part and each row to base and build the lines
        var warnings = place.Warnings.Select(w => new PurchaseWarning("supplierId", w)).ToList();
        var warnedProducts = new HashSet<Guid>();
        decimal subtotal = 0, taxTotal = 0, taxableTotal = 0, otherChargesTotal = 0, discountTotal = 0, grandTotal = 0, originalGrandTotal = 0, billDiscount = 0, billCharges = 0;

        for (var i = 0; i < prepared.Count; i++)
        {
            var line = BuildLine(purchase, prepared[i], calc.Lines[i], rates[prepared[i].Product.Id], input, place.Place, rateToBase, basePrecision);
            purchase.Lines.Add(line);
            if (rates[line.ProductId] is { HsnWarning: true, HsnWarningMessage: { } hsnMessage } && warnedProducts.Add(line.ProductId))
            {
                warnings.Add(new PurchaseWarning($"lines[{i}].hsnCode", hsnMessage));
            }

            subtotal += ToBase(calc.Lines[i].Gross, rateToBase, basePrecision);
            taxTotal += line.TaxAmount;
            taxableTotal += line.TaxableValue;
            otherChargesTotal += line.OtherCharges + line.BillChargeShare;
            discountTotal += line.Discount + line.BillDiscountShare;
            grandTotal += line.LineTotal;
            originalGrandTotal += line.OriginalLineTotal!.Value;
            billDiscount += line.BillDiscountShare;
            billCharges += line.BillChargeShare;

            ApplyPurchaseLineToStock(line, stockItems[(locationId, line.ProductId)], purchaseNo, input.PurchaseDate, prepared[i].SupplierBatchNumber, createdByUserId);
        }

        purchase.Subtotal = subtotal;
        purchase.TaxTotal = taxTotal;
        purchase.TaxableTotal = taxableTotal;
        purchase.OtherChargesTotal = otherChargesTotal;
        purchase.DiscountTotal = discountTotal;
        purchase.GrandTotal = grandTotal; // TP-3: Σ line totals (the old Subtotal + charges - discount + tax identity breaks for tax-inclusive lines)
        purchase.OriginalGrandTotal = originalGrandTotal;
        purchase.BillDiscountAmount = billDiscount;
        purchase.BillCharges = billCharges;
        purchase.OriginalBillDiscountAmount = calc.BillDiscountAmount;
        purchase.OriginalBillCharges = calc.Lines.Sum(l => l.BillChargeShare);
        purchase.Warnings = warnings;
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

        BranchRules.EnsureCanAccessDocument(db, purchase.BranchId);

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

        BranchRules.EnsureCanAccessDocument(db, purchase.BranchId);
        await FinancialAccountRules.EnsureUsableAsync(db, input.AccountId, purchase.BranchId, ct);

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

        var basePrecision = (await db.GetCurrentCompanySettingsAsync(ct)).BaseCurrencyDecimalPrecision;

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
                .FromSqlInterpolated($"SELECT * FROM \"PurchaseOrders\" WHERE \"CompanyId\" = {db.CompanyContext.RequireCompanyId()} AND \"Id\" = {purchaseOrderId} FOR UPDATE")
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
        var basePrecision = (await db.GetCurrentCompanySettingsAsync(ct)).BaseCurrencyDecimalPrecision;

        BranchRules.EnsureCanAccessDocument(db, purchase.BranchId);
        await FinancialAccountRules.EnsureUsableAsync(db, input.AccountId, purchase.BranchId, ct);
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
        BranchRules.EnsureCanAccessDocument(db, purchase.BranchId);

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
        var basePrecision = (await db.GetCurrentCompanySettingsAsync(ct)).BaseCurrencyDecimalPrecision;

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

        if (await Idempotency.FindAsync(db, userId, Idempotency.PurchaseReturn, input.IdempotencyKey, ct) is { } existingReturnId)
        {
            var replayed = await db.PurchaseReturns.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == existingReturnId, ct)
                ?? throw new DomainValidationException("IdempotencyKey", "This request was already processed, but its result is no longer visible with your current branch access.");
            // A key reused for another purchase must not answer with an unrelated return.
            if (replayed.PurchaseOrderId != input.PurchaseOrderId)
            {
                throw new DomainValidationException("IdempotencyKey", "This Idempotency-Key was already used for a return against a different purchase. Use a new key.");
            }
            return replayed;
        }

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

        var returnBranch = await BranchRules.ResolveReturnBranchAsync(db, purchase.BranchId, ct);

        var currency = await db.Currencies.SingleAsync(c => c.Id == purchase.CurrencyId, ct);
        var basePrecision = (await db.GetCurrentCompanySettingsAsync(ct)).BaseCurrencyDecimalPrecision;
        var purchaseLineProductIds = await db.PurchaseOrderLines.Where(l => l.PurchaseOrderId == purchase.Id).Select(l => l.ProductId).ToListAsync(ct);
        // Lock order: PurchaseOrders (above) -> Products KEY SHARE -> StockItems. The return reads the factor of a unit
        // that history may not yet reference, so a concurrent product-unit edit must wait until this commits.
        await StockLocks.LockProductsKeyShareAsync(db, purchaseLineProductIds, ct);
        // Phase 10 D2: goods go out of the requested location (default: where the purchase was received), which must be
        // usable by the purchase's branch. Resolved before the lock, locked, then re-checked under the lock.
        var companyLevel = await StockLocationRules.HasCompanyLevelAsync(permissions, "Purchase.Return", ct);
        var locationId = await StockLocationRules.ResolveReturnSourceAsync(db, input.StockLocationId, purchase.StockLocationId, purchase.BranchId, companyLevel, ct);
        await StockLocks.LockLocationsKeyShareAsync(db, [locationId], ct);
        await StockLocationRules.ResolveReturnSourceAsync(db, locationId, purchase.StockLocationId, purchase.BranchId, companyLevel, ct);
        // Lock the stock rows before reading the batches, so the remaining quantities checked below are current.
        var stockItems = await StockLocks.EnsureAndLockStockItemsAsync(db, purchaseLineProductIds.Select(id => (locationId, id)), ct);
        var lines = await db.PurchaseOrderLines.Include(l => l.Product).ThenInclude(p => p.BaseUnit).Include(l => l.Batch).Include(l => l.Tax).Include(l => l.Taxes)
            .Where(l => l.PurchaseOrderId == purchase.Id).ToListAsync(ct);
        var priorReturns = await db.PurchaseReturnLines.Include(r => r.Taxes)
            .Where(r => r.PurchaseOrderLine.PurchaseOrderId == purchase.Id).ToListAsync(ct);
        var productUnits = await db.ProductUnits.Include(u => u.Unit)
            .Where(u => purchaseLineProductIds.Contains(u.ProductId)).ToListAsync(ct);

        var purchaseReturn = new PurchaseReturn
        {
            Id = Guid.NewGuid(),
            ReturnNo = await DocumentNumbers.NextNumberAsync(db, DocumentSeries.PurchaseReturn, returnBranch, ct),
            BranchId = returnBranch.Id,
            StockLocationId = locationId,
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
            var baseUnit = line.Product.BaseUnit;

            // The return unit: the line's own unit (even if deactivated since) or any active unit of the product.
            // Purchasable/sellable flags do not apply - a return reverses goods that were physically received.
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

            var prior = priorReturns.Where(r => r.PurchaseOrderLineId == line.Id).ToList();
            var returnableBase = line.BaseQty - prior.Sum(r => r.BaseQty);
            if (baseQty > returnableBase)
            {
                throw new DomainValidationException("Lines", $"Only {StockQuantity.Format(returnableBase)} {baseUnit.ShortCode} of {line.Product.Name} can still be returned.");
            }
            await StockAvailability.EnsureInBatchAsync(db, batch.Id, locationId, line.ProductId, baseQty,
                inStock => new DomainValidationException("Lines", $"Only {StockQuantity.Format(inStock)} {baseUnit.ShortCode} of {line.Product.Name} from this purchase is still in stock to return."), ct);

            // Returning whatever is left takes the exact remainder, so per-line rounding can never leave a stray
            // paisa of credit unissued (or issue one too many) once an item is fully returned. Credit is prorated
            // by base quantity, so returns in different units of the same line add up. Every tax row of the original is
            // reversed on its own (BR25, TaxReversal) and the return line's tax is the sum of the returned rows (DQ-TP2-4);
            // the document-currency values of a TP-3 purchase are reversed at that currency's precision.
            var taxable = TaxReversal.Share(line.TaxableValue, prior.Sum(r => r.TaxableValue), baseQty, returnableBase, line.BaseQty, basePrecision);
            var rows = TaxReversal.ReverseRows(
                OriginalRows(line), prior.SelectMany(r => r.Taxes).Select(t => ToRow(t, original: false)).ToList(), baseQty, returnableBase, line.BaseQty, basePrecision);
            var tax = rows.Sum(r => r.TaxAmount);

            var returnLine = new PurchaseReturnLine
            {
                Id = Guid.NewGuid(),
                PurchaseReturnId = purchaseReturn.Id,
                PurchaseOrderLineId = line.Id,
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
                CostRemoved = StockCost.Money(baseQty * batch.UnitCost),
                TaxProfileId = line.TaxProfileId,
                TaxProfileVersionId = line.TaxProfileVersionId,
                PlaceOfSupply = line.PlaceOfSupply,
            };

            // Document-currency twins: only for a TP-3 original (NULL on history, which keeps the old proportional rule below).
            IReadOnlyList<TaxRowResult>? originalRows = null;
            if (line.OriginalTaxableValue is { } originalTaxable)
            {
                returnLine.OriginalTaxableValue = TaxReversal.Share(
                    originalTaxable, prior.Sum(r => r.OriginalTaxableValue ?? 0m), baseQty, returnableBase, line.BaseQty, currency.DecimalPrecision);
                originalRows = TaxReversal.ReverseRows(
                    OriginalRows(line, original: true), prior.SelectMany(r => r.Taxes).Select(t => ToRow(t, original: true)).ToList(),
                    baseQty, returnableBase, line.BaseQty, currency.DecimalPrecision);
                returnLine.OriginalTaxAmount = originalRows.Sum(r => r.TaxAmount);
            }

            foreach (var row in rows)
            {
                var originalRow = originalRows?.SingleOrDefault(o => TaxReversal.RowKey(o.ComponentId, o.Code) == TaxReversal.RowKey(row.ComponentId, row.Code));
                var source = line.Taxes.FirstOrDefault(t => TaxReversal.RowKey(t.TaxComponentId, t.ComponentCode) == TaxReversal.RowKey(row.ComponentId, row.Code));
                var legacy = row.ComponentId is null && source?.Treatment is null && rows.Count == 1; // the one legacy row keeps the id the SQL catch-up gives it
                returnLine.Taxes.Add(new PurchaseReturnLineTax
                {
                    Id = legacy ? TaxSeedData.LegacyLineTaxId(returnLine.Id) : Guid.NewGuid(),
                    PurchaseReturnLineId = returnLine.Id,
                    TaxComponentId = row.ComponentId,
                    ComponentCode = row.Code,
                    Kind = row.Kind,
                    Method = row.Method,
                    RatePercent = row.RatePercent,
                    AmountPerBaseUnit = row.AmountPerBaseUnit,
                    BaseAmount = row.BaseAmount,
                    TaxAmount = row.TaxAmount,
                    Treatment = source?.Treatment,
                    OriginalBaseAmount = originalRow?.BaseAmount,
                    OriginalTaxAmount = originalRow?.TaxAmount,
                });
            }
            purchaseReturn.Lines.Add(returnLine);
            priorReturns.Add(returnLine);
            purchaseReturn.TaxableTotal += taxable;
            purchaseReturn.TaxTotal += tax;

            // Take the stock out of the batch and off the product's on-hand figures, valuing it at the batch cost.
            await StockPosting.IssueFromBatchAsync(db, stockItems[(locationId, line.ProductId)], batch, baseQty, ct);

            db.StockMovements.Add(MovementSnapshot.Apply(new StockMovement
            {
                Id = Guid.NewGuid(),
                ProductId = line.ProductId,
                StockLocationId = locationId,
                Type = StockMovementType.PurchaseReturn,
                PurchaseBatchId = batch.Id,
                QuantityBaseUnits = -baseQty,
                ReferenceType = "PurchaseReturn",
                ReferenceId = purchaseReturn.Id,
                ReferenceNo = purchaseReturn.ReturnNo,
                UserId = userId,
                Notes = $"Returned to supplier from {purchase.PurchaseNo}",
            }, returnLine.ProductUnitId, returnLine.UnitCode, returnLine.ConversionFactorToBase, returnLine.Qty));
        }

        purchaseReturn.Total = purchaseReturn.TaxableTotal + purchaseReturn.TaxTotal;

        // The same credit in the purchase's own currency. A TP-3 purchase reversed its document-currency values line by line, so the credit
        // is their sum. A legacy purchase keeps the old rule: returning the last of the goods credits exactly what is left of the original
        // total; otherwise the base credit's share of the purchase is applied to it.
        decimal originalCredit;
        var originalLeft = purchase.OriginalGrandTotal - purchase.OriginalReturnedAmount;
        if (purchaseReturn.Lines.All(l => l.OriginalTaxableValue is not null))
        {
            originalCredit = purchaseReturn.Lines.Sum(l => l.OriginalTaxableValue!.Value + l.OriginalTaxAmount!.Value);
        }
        else if (purchase.ExchangeRateToBase == 1m)
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
        Idempotency.Record(db, userId, Idempotency.PurchaseReturn, input.IdempotencyKey, purchaseReturn.Id);

        if (input.Refund is { } refund)
        {
            await FinancialAccountRules.EnsureUsableAsync(db, refund.AccountId, purchase.BranchId, ct);
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
        var locked = await LockPurchaseAsync(purchaseOrderId, ct) ?? throw new DomainValidationException("Id", "Purchase not found.");
        BranchRules.EnsureCanAccessDocument(db, locked.BranchId);
        var locationId = locked.StockLocationId;
        await StockLocks.LockLocationsKeyShareAsync(db, [locationId], ct);
        var stockItems = await StockLocks.EnsureAndLockStockItemsAsync(db,
            (await db.PurchaseOrderLines.Where(l => l.PurchaseOrderId == purchaseOrderId).Select(l => l.ProductId).ToListAsync(ct)).Select(id => (locationId, id)), ct);

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
            if (batch.RemainingBaseQty != batch.PurchasedBaseQty
                || await StockPosting.BalanceAsync(db, batch.Id, locationId, ct) != batch.PurchasedBaseQty)
            {
                throw new DomainValidationException("Status", "Cannot cancel — stock from this purchase has already been sold.");
            }
            if (batch.PurchasedBaseQty != line.BaseQty)
            {
                throw new DomainValidationException("Lines", $"Cannot cancel — the stock batch of a line on {purchase.PurchaseNo} does not match the line quantity.");
            }
        }

        // FI-D32: removing the receipt must not take on-hand below what shipments have reserved (checked per product, summed over lines).
        await StockAvailability.EnsureAsync(db, stockItems,
            purchase.Lines.Select(l => new StockAvailability.Requirement(locationId, l.ProductId, l.BaseQty)),
            s => new DomainValidationException("Status",
                $"Cannot cancel — {Math.Max(0m, s.Requested - s.Available):0.###} of the {s.Requested:0.###} to remove is reserved for shipments; only {Math.Max(0m, s.Available):0.###} is unreserved. Release the reservations first."),
            ct);

        foreach (var line in purchase.Lines)
        {
            var batch = line.Batch!;
            await StockPosting.ReverseReceiptAsync(db, stockItems[(locationId, line.ProductId)], batch, ct);

            db.StockMovements.Add(MovementSnapshot.Apply(new StockMovement
            {
                Id = Guid.NewGuid(),
                ProductId = line.ProductId,
                StockLocationId = locationId,
                Type = StockMovementType.PurchaseCancellation,
                PurchaseBatchId = batch.Id,
                QuantityBaseUnits = -line.BaseQty,
                ReferenceType = "PurchaseOrder",
                ReferenceId = purchase.Id,
                ReferenceNo = purchase.PurchaseNo,
                UserId = cancelledByUserId,
                Notes = "Purchase cancelled",
            }, line.ProductUnitId, line.UnitCode, line.ConversionFactorToBase, line.Qty));
        }

        purchase.Status = PurchaseStatus.Cancelled;
        purchase.CancelledAt = DateTimeOffset.UtcNow;
        purchase.CancelledByUserId = cancelledByUserId;

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    // A tax row as the reversal helper reads it: the base-currency amounts, or (original) the document-currency ones.
    private static TaxRowResult ToRow(PurchaseLineTaxBase t, bool original) => new(
        t.TaxComponentId, t.ComponentCode, t.Kind, t.Method, t.RatePercent, t.AmountPerBaseUnit,
        original ? t.OriginalBaseAmount ?? t.BaseAmount : t.BaseAmount, original ? t.OriginalTaxAmount ?? t.TaxAmount : t.TaxAmount);

    // The rows of a purchase line to reverse. A line without rows (history that missed the catch-up) reverses as the one legacy row it would have had.
    private static IReadOnlyList<TaxRowResult> OriginalRows(PurchaseOrderLine line, bool original = false) =>
        line.Taxes.Count > 0
            ? line.Taxes.Select(t => ToRow(t, original)).ToList()
            : [new TaxRowResult(null, line.Tax.Name, TaxComponentKind.Other, TaxCalculationMethod.Percent, line.TaxRatePercent, null,
                original ? line.OriginalTaxableValue ?? line.TaxableValue : line.TaxableValue, original ? line.OriginalTaxAmount ?? line.TaxAmount : line.TaxAmount)];

    // The supplier's lot number is kept exactly as entered; a blank entry just means "none given".
    private static string? NormalizeSupplierBatchNumber(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 60)
        {
            throw new DomainValidationException(field, "Supplier batch number can be at most 60 characters.");
        }
        return value;
    }

    /// <summary>A line whose product, unit and quantities were checked (all in the entered document currency; nothing converted yet).</summary>
    private sealed record PreparedLine(PurchaseLineInput Input, Product Product, ProductUnit Unit, decimal BaseQty, string? SupplierBatchNumber);

    // The product, unit and quantity rules of a purchase line. Errors are keyed lines[i].field so the form can map them to the row.
    private async Task<PreparedLine> PrepareLineAsync(int index, PurchaseLineInput input, CancellationToken ct)
    {
        string Key(string field) => $"lines[{index}].{field}";
        var errors = new List<FieldError>();
        void Fail(string field, string message) => errors.Add(new FieldError(Key(field), message));

        if (input.Qty <= 0) Fail("qty", "Quantity must be greater than zero.");
        if (input.Rate < 0) Fail("rate", "Rate cannot be negative.");
        string? supplierBatchNumber = null;
        try
        {
            supplierBatchNumber = NormalizeSupplierBatchNumber(input.SupplierBatchNumber, Key("supplierBatchNumber"));
        }
        catch (DomainValidationException ex)
        {
            errors.AddRange(ex.Errors);
        }

        var product = await db.Products.Include(p => p.BaseUnit).SingleOrDefaultAsync(p => p.Id == input.ProductId, ct);
        if (product is null)
        {
            Fail("productId", "Select a valid product.");
            throw new DomainValidationException(errors);
        }
        if (!product.IsActive) Fail("productId", $"{product.Name} is inactive.");

        var productUnit = await db.ProductUnits.Include(u => u.Unit).SingleOrDefaultAsync(u => u.Id == input.ProductUnitId, ct);
        if (productUnit is null)
        {
            Fail("productUnitId", "Select a valid unit.");
            throw new DomainValidationException(errors);
        }
        if (productUnit.ProductId != product.Id)
        {
            Fail("productUnitId", "The selected unit does not belong to this product.");
            throw new DomainValidationException(errors);
        }
        var unitDisplay = productUnit.Label ?? productUnit.Unit.Name;
        if (!productUnit.IsActive) Fail("productUnitId", $"{unitDisplay} is no longer active.");
        else if (!productUnit.IsPurchasable) Fail("productUnitId", $"{unitDisplay} cannot be purchased for {product.Name}.");
        if (errors.Count > 0) throw new DomainValidationException(errors);

        // The stored factor is snapshotted onto the line; history is never recomputed from configuration.
        StockQuantity.EnsureEnteredDecimals(input.Qty, productUnit.Unit.DecimalPlaces, unitDisplay, Key("qty"));
        var baseQty = input.Qty * productUnit.ConversionFactorToBase;
        StockQuantity.EnsureStorable(baseQty, Key("qty"));
        StockQuantity.EnsureWithinBaseDecimals(input.Qty, productUnit.Unit.ShortCode, baseQty, product.BaseUnit, Key("qty"));
        return new PreparedLine(input, product, productUnit, baseQty, supplierBatchNumber);
    }

    // The tax calculator on the document as entered (document currency, precision of the supplier's currency; BR22). Its field names
    // (lines[i].unitPrice) are mapped to the request's (lines[i].rate).
    private static TaxDocumentResult Calculate(
        CreatePurchaseInput input, PlaceOfSupplyResolution place, IReadOnlyDictionary<Guid, ResolvedLineRates> rates, IReadOnlyList<PreparedLine> lines, int precision)
    {
        var calcLines = lines.Select(l => new TaxCalcLine(
            l.Input.Qty, l.BaseQty, l.Input.Rate, l.Input.Discount, l.Input.OtherCharges, input.RatesIncludeTax, rates[l.Product.Id].Rates)).ToList();
        try
        {
            return TaxCalculator.Calculate(new TaxCalcDocument(
                precision, place.Place, PartyTaxStatus.Unspecified, input.BillDiscountPercent, input.BillDiscountAmount, input.BillCharges, calcLines));
        }
        catch (DomainValidationException ex)
        {
            throw new DomainValidationException(ex.Errors.Select(e => e with { Field = e.Field.Replace(".unitPrice", ".rate", StringComparison.Ordinal) }).ToList());
        }
    }

    // A foreign invoice's figures exist in whole units of its own currency (the calculator rounded each to that precision), and only
    // those rounded figures are converted, so original x rate stays consistent with the base value (the rule of the old BuildLineAsync).
    private static decimal ToBase(decimal amount, decimal rateToBase, int basePrecision) =>
        rateToBase != 1m ? Math.Round(amount * rateToBase, basePrecision, MidpointRounding.AwayFromZero) : amount;

    // The line with every monetary field in the business base currency; the document-currency values of the calculator are kept in the
    // Original* columns (always set for a TP-3 purchase, so NULL means history). The taxable value includes the bill charge share and the
    // line's other charges and is net of line and bill discount, and the landed cost per base unit is derived from it (AC9). Cost excludes tax (TP3-Q3).
    private static PurchaseOrderLine BuildLine(
        PurchaseOrder purchase, PreparedLine prepared, TaxLineResult calc, ResolvedLineRates rates, CreatePurchaseInput input,
        PlaceOfSupply place, decimal rateToBase, int basePrecision)
    {
        decimal Base(decimal amount) => ToBase(amount, rateToBase, basePrecision);
        var gross = Base(calc.Gross);
        var lineDiscount = Base(calc.LineDiscount);
        var billDiscountShare = Base(calc.BillDiscountShare);
        var billChargeShare = Base(calc.BillChargeShare);
        var otherCharges = Base(calc.OtherCharges);
        var taxableValue = input.RatesIncludeTax ? Base(calc.Taxable) : gross - lineDiscount - billDiscountShare + billChargeShare + otherCharges;

        var lineId = Guid.NewGuid();
        var rows = calc.Rows.Select(r => new PurchaseOrderLineTax
        {
            Id = Guid.NewGuid(),
            PurchaseOrderLineId = lineId,
            TaxComponentId = r.ComponentId,
            ComponentCode = r.Code,
            Kind = r.Kind,
            Method = r.Method,
            RatePercent = r.RatePercent,
            AmountPerBaseUnit = r.AmountPerBaseUnit,
            // A per-unit row's base amount is the base quantity, not money (DQ-TP2-6): copied, not converted.
            BaseAmount = r.Method == TaxCalculationMethod.AmountPerBaseUnit ? r.BaseAmount : Base(r.BaseAmount),
            TaxAmount = Base(r.TaxAmount),
            Treatment = rates.Rates.Treatment,
            OriginalBaseAmount = r.BaseAmount,
            OriginalTaxAmount = r.TaxAmount,
        }).ToList();
        var taxAmount = rows.Sum(r => r.TaxAmount);
        var unitCost = prepared.BaseQty > 0 ? StockCost.PerBaseUnit(taxableValue / prepared.BaseQty) : 0;

        var line = new PurchaseOrderLine
        {
            Id = lineId,
            PurchaseOrderId = purchase.Id,
            ProductId = prepared.Product.Id,
            ProductUnitId = prepared.Unit.Id,
            UnitId = prepared.Unit.UnitId,
            UnitCode = prepared.Unit.Unit.ShortCode,
            Qty = prepared.Input.Qty,
            ConversionFactorToBase = prepared.Unit.ConversionFactorToBase,
            BaseQty = prepared.BaseQty,
            Rate = rateToBase == 1m ? prepared.Input.Rate : Math.Round(prepared.Input.Rate * rateToBase, 4, MidpointRounding.AwayFromZero),
            TaxId = rates.MirrorTaxId, // the purchase profile's mirror tax: keeps the TP-2 build and the T25 "used version" check working
            TaxRatePercent = calc.TaxRatePercent,
            OtherCharges = otherCharges,
            Discount = lineDiscount,
            TaxableValue = taxableValue,
            TaxAmount = taxAmount,
            LineTotal = taxableValue + taxAmount,
            UnitCost = unitCost,
            TaxProfileId = rates.Rates.ProfileId,
            TaxProfileVersionId = rates.Rates.VersionId,
            PlaceOfSupply = place,
            HsnCode = rates.HsnCode,
            PriceIncludesTax = input.RatesIncludeTax,
            BillDiscountShare = billDiscountShare,
            BillChargeShare = billChargeShare,
            OriginalRate = prepared.Input.Rate,
            OriginalDiscount = calc.LineDiscount,
            OriginalOtherCharges = calc.OtherCharges,
            OriginalBillDiscountShare = calc.BillDiscountShare,
            OriginalBillChargeShare = calc.BillChargeShare,
            OriginalTaxableValue = calc.Taxable,
            OriginalTaxAmount = calc.TaxTotal,
            OriginalLineTotal = calc.LineTotal,
        };
        foreach (var row in rows) line.Taxes.Add(row);
        return line;
    }

    private void ApplyPurchaseLineToStock(PurchaseOrderLine line, StockItem stockItem, string purchaseNo, DateOnly receivedDate, string? supplierBatchNumber, Guid userId)
    {
        var batch = new PurchaseBatch
        {
            Id = Guid.NewGuid(),
            PurchaseOrderLineId = line.Id,
            ProductId = line.ProductId,
            SupplierBatchNumber = supplierBatchNumber,
            ReceivedDate = receivedDate,
            PurchasedBaseQty = line.BaseQty,
            UnitCost = line.UnitCost,
        };
        db.PurchaseBatches.Add(batch);

        db.StockMovements.Add(MovementSnapshot.Apply(new StockMovement
        {
            Id = Guid.NewGuid(),
            ProductId = line.ProductId,
            StockLocationId = stockItem.StockLocationId,
            PurchaseBatchId = batch.Id,
            Type = StockMovementType.Purchase,
            QuantityBaseUnits = line.BaseQty,
            ReferenceType = "PurchaseOrder",
            ReferenceId = line.PurchaseOrderId,
            ReferenceNo = purchaseNo,
            UserId = userId,
        }, line.ProductUnitId, line.UnitCode, line.ConversionFactorToBase, line.Qty));

        StockPosting.Receive(db, stockItem, batch, line.BaseQty, line.UnitCost);
    }
}
