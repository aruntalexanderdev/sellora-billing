using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Payments;
using Sellora.Domain.Sales;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

public class SalesService(SelloraDbContext db, IEffectivePermissionService? permissions = null, ITaxResolver? taxResolver = null) : ISalesService
{
    private readonly ITaxResolver resolver = taxResolver ?? new TaxResolver(db);

    public async Task<Guid> ResolveSellingLocationAsync(Guid? stockLocationId, CancellationToken ct = default)
    {
        var branch = await BranchRules.ResolveWorkingBranchAsync(db, ct);
        var companyLevel = await StockLocationRules.HasCompanyLevelAsync(permissions, "Sales.Create", ct);
        return await StockLocationRules.ResolveAsync(db, PostingKind.Sale, stockLocationId, branch.Id, companyLevel, ct);
    }

    public async Task<SalesInvoice> CreateSaleAsync(CreateSaleInput input, Guid createdByUserId, CancellationToken ct = default)
    {
        if (input.Lines.Count == 0)
        {
            throw new DomainValidationException("lines", "Add at least one item.");
        }

        await using var transaction = await StockLocks.BeginAsync(db, ct);

        if (await Idempotency.FindAsync(db, createdByUserId, Idempotency.Sale, input.IdempotencyKey, ct) is { } existingId)
        {
            // The stored rows (lines and their tax rows) are returned, never recalculated.
            return await db.SalesInvoices.Include(i => i.Lines).ThenInclude(l => l.Taxes).SingleOrDefaultAsync(i => i.Id == existingId, ct)
                ?? throw new DomainValidationException("IdempotencyKey", "This request was already processed, but its result is no longer visible with your current branch access.");
        }

        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == input.CustomerId, ct)
            ?? throw new DomainValidationException("customerId", "Select a valid customer.");
        if (!customer.IsActive)
        {
            throw new DomainValidationException("customerId", "This customer is inactive.");
        }

        var branch = await BranchRules.ResolveWorkingBranchAsync(db, ct);
        await FinancialAccountRules.EnsureUsableAsync(db, input.PaidAmount > 0 ? input.AccountId : null, branch.Id, ct);

        // TP-4 (BR19a): a whole-bill discount needs Sales.BillDiscount at the working branch. The controller answers 403 first; this is the same check for
        // every other caller. Without a permission service it fails closed, like the other permission checks of this service.
        var discountField = input.BillDiscountPercent is not null ? "billDiscountPercent" : "billDiscountAmount";
        if ((input.BillDiscountPercent is > 0 || input.BillDiscountAmount is > 0) && !await CanGiveBillDiscountAsync(branch.Id, ct))
        {
            throw new DomainValidationException(discountField, "You are not allowed to give a whole-bill discount.");
        }

        var invoiceNo = await DocumentNumbers.NextNumberAsync(db, DocumentSeries.Invoice, branch, ct);

        // Phase 8b: issued from the requested location (a store or vehicle that can sell), else the working branch's store.
        // A central vehicle needs Sales.Create company-wide. Re-read and re-checked under the location lock below.
        var companyLevel = await StockLocationRules.HasCompanyLevelAsync(permissions, "Sales.Create", ct);
        var locationId = await StockLocationRules.ResolveAsync(db, PostingKind.Sale, input.StockLocationId, branch.Id, companyLevel, ct);

        var invoice = new SalesInvoice
        {
            Id = Guid.NewGuid(),
            InvoiceNo = invoiceNo,
            BranchId = branch.Id,
            StockLocationId = locationId,
            CustomerId = customer.Id,
            InvoiceDate = input.InvoiceDate,
            PaymentMethod = input.PaymentMethod,
            Status = SalesStatus.Completed,
            CreatedByUserId = createdByUserId,
            BillDiscountPercent = input.BillDiscountPercent,
        };

        // FI-D24 Q1a: money is rounded per line to the base currency precision; header totals are sums of rounded line values.
        var precision = (await db.GetCurrentCompanySettingsAsync(ct)).BaseCurrencyDecimalPrecision;
        var companyId = db.CompanyContext.RequireCompanyId();
        var settings = await db.BusinessSettings.AsNoTracking().Where(s => s.CompanyId == companyId).SingleAsync(ct);

        // Product KEY SHARE before building lines: a concurrent product-unit edit (FOR UPDATE) waits for this sale to
        // commit, so the factors snapshotted below cannot change underneath it. StockItems locks follow (§5.4).
        await StockLocks.LockProductsKeyShareAsync(db, input.Lines.Select(l => l.ProductId), ct);

        // TP-4: place of supply (BR12; a walk-in customer is intra-state) and the sales rate sets valid on the invoice date (BR5). The profiles are then locked
        // FOR KEY SHARE (after the products, before the locations and StockItems: no new deadlock path) so a version cannot be superseded under this sale, and the
        // versions are resolved again under the lock (TP3-Q6).
        var place = await resolver.ResolvePlaceOfSupplyAsync(TaxDirection.Sale, branch.Id, customer.Id, ct);
        var productIds = input.Lines.Select(l => l.ProductId).ToList();
        var rates = await resolver.ResolveRateSetsAsync(TaxDirection.Sale, place.CountryId, input.InvoiceDate, productIds, "lines", ct);
        await StockLocks.LockTaxProfilesKeyShareAsync(db, rates.Values.Select(r => r.Rates.ProfileId), ct);
        if (db.Database.IsRelational())
        {
            var again = await resolver.ResolveRateSetsAsync(TaxDirection.Sale, place.CountryId, input.InvoiceDate, productIds, "lines", ct);
            if (again.Any(kv => !rates.TryGetValue(kv.Key, out var first) || first.Rates.VersionId != kv.Value.Rates.VersionId || first.Rates.ProfileId != kv.Value.Rates.ProfileId))
            {
                throw new DomainValidationException("lines", "A tax rate changed while the sale was being saved. Review the tax and save again.");
            }
        }

        // ---- 1. validate every line (units, quantities, prices), collecting the errors of all lines
        var errors = new List<FieldError>();
        var prepared = new List<PreparedLine>(input.Lines.Count);
        for (var i = 0; i < input.Lines.Count; i++)
        {
            try
            {
                prepared.Add(await PrepareLineAsync(i, input.Lines[i], input.CanOverridePrice, precision, ct));
            }
            catch (DomainValidationException ex)
            {
                errors.AddRange(ex.Errors);
            }
        }
        // TP-5a (T11): GST-taxable product without an HSN is an error once the company's HsnRequiredFrom date is reached.
        errors.AddRange(HsnRules.DocumentErrors(settings.HsnRequiredFrom, input.InvoiceDate, productIds, rates));
        if (errors.Count > 0) throw new DomainValidationException(errors);

        // ---- 2. the calculator (base currency precision, place of supply, bill discount and charges), then the checks that need its result
        var calc = Calculate(input, discountField, place, rates, prepared, settings, precision);
        SalesTaxRules.EnsureBillDiscountCap(calc, settings.MaxBillDiscountPercent, precision, discountField);
        if (input.ExpectedGrandTotal is { } expected && expected != calc.GrandTotal)
        {
            throw new DomainValidationException("expectedGrandTotal", $"The total is now {calc.GrandTotal.ToString("F" + precision, System.Globalization.CultureInfo.InvariantCulture)}.");
        }

        // ---- 3. build the lines; requested base quantity per product, summed across lines, so a product picked in two
        // different units (e.g. one bag + some loose kg) can't together oversell stock.
        var requestedBaseQtyByProduct = new Dictionary<Guid, decimal>();
        var warnings = new List<SaleWarning>();
        var warnedProducts = new HashSet<Guid>();
        for (var i = 0; i < prepared.Count; i++)
        {
            var line = BuildLine(invoice, prepared[i], calc.Lines[i], rates[prepared[i].Product.Id], settings, place.Place);
            invoice.Lines.Add(line);
            if (rates[line.ProductId] is { HsnWarning: true, HsnWarningMessage: { } hsnMessage } && warnedProducts.Add(line.ProductId))
            {
                warnings.Add(new SaleWarning($"lines[{i}].hsnCode", hsnMessage));
            }

            requestedBaseQtyByProduct[line.ProductId] = requestedBaseQtyByProduct.GetValueOrDefault(line.ProductId) + line.BaseQty;
        }

        // Hold each product's stock lock from here to commit, so the availability check and the FIFO batch
        // consumption below see the same, current quantities.
        await StockLocks.LockLocationsKeyShareAsync(db, [locationId], ct);
        await StockLocationRules.ResolveAsync(db, PostingKind.Sale, locationId, branch.Id, companyLevel, ct);
        var stockItems = await StockLocks.EnsureAndLockStockItemsAsync(db, requestedBaseQtyByProduct.Keys.Select(id => (locationId, id)), ct);

        if (await StockAvailability.FindShortageAsync(db, stockItems,
                requestedBaseQtyByProduct.Select(r => new StockAvailability.Requirement(locationId, r.Key, r.Value)), ct) is { } shortage)
        {
            var product = await db.Products.Include(p => p.BaseUnit).SingleAsync(p => p.Id == shortage.ProductId, ct);
            var code = product.BaseUnit.ShortCode;
            throw new DomainValidationException("lines", $"Only {StockQuantity.Format(shortage.Available)} {code} of {product.Name} in stock — cannot sell {StockQuantity.Format(shortage.Requested)} {code}.");
        }

        foreach (var line in invoice.Lines)
        {
            await ApplySaleLineToStockAsync(line, stockItems[(locationId, line.ProductId)], invoiceNo, createdByUserId, ct);
        }

        // Header (BR20, BR21): sums of the rounded lines; the grand total is the sum of the line totals (the old Subtotal - Discount + Tax identity breaks for
        // tax-inclusive lines).
        invoice.Subtotal = calc.Subtotal;
        invoice.TaxTotal = invoice.Lines.Sum(l => l.TaxAmount);
        invoice.DiscountTotal = invoice.Lines.Sum(l => l.Discount + l.BillDiscountShare);
        invoice.TaxableTotal = invoice.Lines.Sum(l => l.TaxableValue);
        invoice.BillDiscountAmount = invoice.Lines.Sum(l => l.BillDiscountShare);
        invoice.BillCharges = invoice.Lines.Sum(l => l.BillChargeShare);
        invoice.GrandTotal = invoice.Lines.Sum(l => l.LineTotal);
        invoice.PlaceOfSupply = place.Place;
        // The buyer's state when the buyer is in the same country, else the seller's own (walk-in, customer without state). A buyer in another country has no GST
        // state here (T31c): the supply is inter-state and the state stays empty.
        var abroad = customer.CountryId is { } customerCountry && customerCountry != place.CountryId;
        invoice.PlaceOfSupplyStateId = abroad ? null : place.PartyStateId ?? place.OwnStateId;
        invoice.SellerStateId = place.OwnStateId;
        invoice.PartyTaxStatus = PartyTaxStatus.Unspecified;
        invoice.Warnings = warnings;

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

    // Sales.BillDiscount at the working branch (or company-wide). Fail closed without a permission service.
    private async Task<bool> CanGiveBillDiscountAsync(Guid branchId, CancellationToken ct)
    {
        if (permissions is null) return false;
        var set = await permissions.GetCurrentAsync(ct);
        return set.HasCompanyLevel("Sales.BillDiscount") || set.HasAtBranch("Sales.BillDiscount", branchId);
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

        BranchRules.EnsureCanAccessDocument(db, invoice.BranchId);
        await FinancialAccountRules.EnsureUsableAsync(db, input.AccountId, invoice.BranchId, ct);

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
                .FromSqlInterpolated($"SELECT * FROM \"SalesInvoices\" WHERE \"CompanyId\" = {db.CompanyContext.RequireCompanyId()} AND \"Id\" = {salesInvoiceId} FOR UPDATE")
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
        var baseCurrencyId = (await db.GetCurrentCompanySettingsAsync(ct)).BaseCurrencyId;
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

        BranchRules.EnsureCanAccessDocument(db, invoice.BranchId);
        await FinancialAccountRules.EnsureUsableAsync(db, input.AccountId, invoice.BranchId, ct);
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
        BranchRules.EnsureCanAccessDocument(db, invoice.BranchId);

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

        if (await Idempotency.FindAsync(db, userId, Idempotency.SalesReturn, input.IdempotencyKey, ct) is { } existingReturnId)
        {
            var replayed = await db.SalesReturns.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == existingReturnId, ct)
                ?? throw new DomainValidationException("IdempotencyKey", "This request was already processed, but its result is no longer visible with your current branch access.");
            // A key reused for another sale must not answer with an unrelated return (no refund would happen for this one).
            if (replayed.SalesInvoiceId != input.SalesInvoiceId)
            {
                throw new DomainValidationException("IdempotencyKey", "This Idempotency-Key was already used for a return against a different sale. Use a new key.");
            }
            return replayed;
        }

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

        // The return belongs to the branch of the sale (goods go back where they were sold).
        var returnBranch = await BranchRules.ResolveReturnBranchAsync(db, invoice.BranchId, ct);

        // Lock the stock rows before reading the batches the goods go back into.
        var lineProductIds = await db.SalesInvoiceLines.Where(l => l.SalesInvoiceId == invoice.Id).Select(l => l.ProductId).ToListAsync(ct);
        // Lock order: SalesInvoices (above) -> Products KEY SHARE -> StockItems. The return reads the factor of a unit
        // that history may not yet reference, so a concurrent product-unit edit must wait until this commits.
        await StockLocks.LockProductsKeyShareAsync(db, lineProductIds, ct);
        // Phase 10 D1: goods go back to the location the sale issued from while it is active and still the invoice branch's;
        // a vehicle that is inactive or reassigned falls back to the invoice branch's store. Resolved before the lock for an
        // early failure; both candidates (invoice location, branch store) are locked in id order, then it is re-resolved under
        // the lock, so the answer cannot change before commit and the chosen row is always one we hold.
        await StockLocationRules.ResolveRestockAsync(db, invoice, false, ct);
        await StockLocks.LockLocationsKeyShareAsync(db, await StockLocationRules.RestockCandidatesAsync(db, invoice, false, ct), ct);
        var locationId = await StockLocationRules.ResolveRestockAsync(db, invoice, false, ct);
        var stockItems = await StockLocks.EnsureAndLockStockItemsAsync(db, lineProductIds.Select(id => (locationId, id)), ct);
        var lines = await db.SalesInvoiceLines
            .Include(l => l.Product).ThenInclude(p => p.BaseUnit).Include(l => l.Tax).Include(l => l.Taxes)
            .Include(l => l.BatchConsumptions).ThenInclude(c => c.PurchaseBatch)
            .Where(l => l.SalesInvoiceId == invoice.Id).ToListAsync(ct);
        var priorReturns = await db.SalesReturnLines.Include(r => r.Taxes)
            .Where(r => r.SalesInvoiceLine.SalesInvoiceId == invoice.Id).ToListAsync(ct);
        var productUnits = await db.ProductUnits.Include(u => u.Unit)
            .Where(u => lineProductIds.Contains(u.ProductId)).ToListAsync(ct);
        var precision = (await db.GetCurrentCompanySettingsAsync(ct)).BaseCurrencyDecimalPrecision;

        var salesReturn = new SalesReturn
        {
            Id = Guid.NewGuid(),
            ReturnNo = await DocumentNumbers.NextNumberAsync(db, DocumentSeries.SalesReturn, returnBranch, ct),
            BranchId = returnBranch.Id,
            StockLocationId = locationId,
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
            // by base quantity, so returns in different units of the same line add up. Every tax row of the original is
            // reversed on its own (BR25, TaxReversal) and the return line's tax is the sum of the returned rows (DQ-TP2-4).
            // A line without rows (history that missed the catch-up) reverses as the one legacy row it would have had.
            var taxable = TaxReversal.Share(line.TaxableValue, prior.Sum(r => r.TaxableValue), baseQty, returnableBase, line.BaseQty, precision);
            var originalRows = OriginalRows(line);
            var rows = TaxReversal.ReverseRows(originalRows, PriorRows(prior, originalRows, line.Taxes.Count == 0), baseQty, returnableBase, line.BaseQty, precision);
            var tax = rows.Sum(r => r.TaxAmount);

            // Put the goods back into the batches they were sold from, oldest first.
            var remaining = baseQty;
            decimal costRestored = 0;
            foreach (var consumption in line.BatchConsumptions.OrderBy(c => c.PurchaseBatch.CreatedAt).ThenBy(c => c.PurchaseBatch.BatchSequence))
            {
                if (remaining <= 0) break;
                var alloc = Math.Min(remaining, consumption.QtyBaseUnits - consumption.ReturnedQtyBaseUnits);
                if (alloc <= 0) continue;

                consumption.ReturnedQtyBaseUnits += alloc;
                await StockPosting.RestoreAsync(db, stockItems[(locationId, line.ProductId)], consumption.PurchaseBatch, alloc, ct);
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
                TaxProfileId = line.TaxProfileId,
                TaxProfileVersionId = line.TaxProfileVersionId,
                PlaceOfSupply = line.PlaceOfSupply,
            };
            foreach (var row in rows)
            {
                var source = line.Taxes.FirstOrDefault(t => TaxReversal.RowKey(t.TaxComponentId, t.ComponentCode) == TaxReversal.RowKey(row.ComponentId, row.Code));
                var legacy = row.ComponentId is null && source?.Treatment is null && rows.Count == 1; // the one legacy row keeps the id the SQL catch-up gives it
                returnLine.Taxes.Add(new SalesReturnLineTax
                {
                    Id = legacy ? TaxSeedData.LegacyLineTaxId(returnLine.Id) : Guid.NewGuid(),
                    SalesReturnLineId = returnLine.Id,
                    TaxComponentId = row.ComponentId,
                    ComponentCode = row.Code,
                    Kind = row.Kind,
                    Method = row.Method,
                    RatePercent = row.RatePercent,
                    AmountPerBaseUnit = row.AmountPerBaseUnit,
                    BaseAmount = row.BaseAmount,
                    TaxAmount = row.TaxAmount,
                    Treatment = source?.Treatment,
                });
            }
            salesReturn.Lines.Add(returnLine);
            priorReturns.Add(returnLine);
            salesReturn.TaxableTotal += taxable;
            salesReturn.TaxTotal += tax;

            db.StockMovements.Add(MovementSnapshot.Apply(new StockMovement
            {
                Id = Guid.NewGuid(),
                ProductId = line.ProductId,
                StockLocationId = locationId,
                Type = StockMovementType.SaleReturn,
                QuantityBaseUnits = baseQty,
                ReferenceType = "SalesReturn",
                ReferenceId = salesReturn.Id,
                ReferenceNo = salesReturn.ReturnNo,
                UserId = userId,
                Notes = $"Returned from {invoice.InvoiceNo}",
            }, returnLine.ProductUnitId, returnLine.UnitCode, returnLine.ConversionFactorToBase, returnLine.Qty));
        }

        salesReturn.Total = salesReturn.TaxableTotal + salesReturn.TaxTotal;
        invoice.ReturnedAmount += salesReturn.Total;
        db.SalesReturns.Add(salesReturn);
        Idempotency.Record(db, userId, Idempotency.SalesReturn, input.IdempotencyKey, salesReturn.Id);

        if (input.Refund is { } refund)
        {
            await FinancialAccountRules.EnsureUsableAsync(db, refund.AccountId, invoice.BranchId, ct);
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
        var locked = await LockInvoiceAsync(salesInvoiceId, ct) ?? throw new DomainValidationException("Id", "Sale not found.");
        BranchRules.EnsureCanAccessDocument(db, locked.BranchId);
        // Phase 10 D1: a cancellation is an exact undo at the sale's own location; no fallback. Re-resolved under the lock.
        await StockLocationRules.ResolveRestockAsync(db, locked, true, ct);
        await StockLocks.LockLocationsKeyShareAsync(db, [locked.StockLocationId], ct);
        var locationId = await StockLocationRules.ResolveRestockAsync(db, locked, true, ct);
        var stockItems = await StockLocks.EnsureAndLockStockItemsAsync(db,
            (await db.SalesInvoiceLines.Where(l => l.SalesInvoiceId == salesInvoiceId).Select(l => l.ProductId).ToListAsync(ct)).Select(id => (locationId, id)), ct);

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

        foreach (var line in invoice.Lines)
        {
            foreach (var consumption in line.BatchConsumptions)
            {
                await StockPosting.RestoreAsync(db, stockItems[(locationId, line.ProductId)], consumption.PurchaseBatch, consumption.QtyBaseUnits, ct);
            }

            // One ledger row per sales line (mixed units on one invoice need each line's own unit snapshot).
            db.StockMovements.Add(MovementSnapshot.Apply(new StockMovement
            {
                Id = Guid.NewGuid(),
                ProductId = line.ProductId,
                StockLocationId = locationId,
                Type = StockMovementType.SaleCancellation,
                QuantityBaseUnits = line.BaseQty,
                ReferenceType = "SalesInvoice",
                ReferenceId = invoice.Id,
                ReferenceNo = invoice.InvoiceNo,
                UserId = cancelledByUserId,
                Notes = "Sale cancelled",
            }, line.ProductUnitId, line.UnitCode, line.ConversionFactorToBase, line.Qty));
        }

        invoice.Status = SalesStatus.Cancelled;
        invoice.CancelledAt = DateTimeOffset.UtcNow;
        invoice.CancelledByUserId = cancelledByUserId;

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    // A tax row as the reversal helper reads it.
    private static TaxRowResult ToRow(LineTaxBase t) => new(
        t.TaxComponentId, t.ComponentCode, t.Kind, t.Method, t.RatePercent, t.AmountPerBaseUnit, t.BaseAmount, t.TaxAmount);

    // The rows of a sales line to reverse. A line without rows reverses as the one legacy row it would have had (named after its tax).
    private static IReadOnlyList<TaxRowResult> OriginalRows(SalesInvoiceLine line) =>
        line.Taxes.Count > 0
            ? line.Taxes.Select(ToRow).ToList()
            : [new TaxRowResult(null, line.Tax.Name.Length > 60 ? line.Tax.Name[..60] : line.Tax.Name, TaxComponentKind.Other, TaxCalculationMethod.Percent, line.TaxRatePercent, null, line.TaxableValue, line.TaxAmount)];

    // The rows already returned against a line. When the original has no rows, an earlier return has none either (or only a legacy one), so each is read as the
    // legacy row under the original's synthetic key from its own figures.
    private static IReadOnlyList<TaxRowResult> PriorRows(IEnumerable<SalesReturnLine> prior, IReadOnlyList<TaxRowResult> originalRows, bool syntheticOriginal) =>
        prior.SelectMany(r => syntheticOriginal
                ? [originalRows[0] with { BaseAmount = r.TaxableValue, TaxAmount = r.TaxAmount }]
                : r.Taxes.Select(ToRow))
            .ToList();

    /// <summary>A line whose product, unit, quantity and price were checked (nothing calculated yet).</summary>
    private sealed record PreparedLine(SaleLineInput Input, Product Product, ProductUnit Unit, decimal BaseQty, decimal ListPrice);

    // The product, unit, quantity and price rules of a sale line. Errors are keyed lines[i].field so the POS can map them to the row.
    private async Task<PreparedLine> PrepareLineAsync(int index, SaleLineInput input, bool canOverridePrice, int precision, CancellationToken ct)
    {
        string Key(string field) => $"lines[{index}].{field}";
        var errors = new List<FieldError>();
        void Fail(string field, string message) => errors.Add(new FieldError(Key(field), message));

        if (input.Qty <= 0) Fail("qty", "Quantity must be greater than zero.");
        if (input.UnitPrice < 0) Fail("unitPrice", "Unit price cannot be negative.");
        if (input.Discount < 0) Fail("discount", "Discount cannot be negative.");
        // The column is numeric(18,4): a finer discount would be rounded on save while the line and header totals were
        // computed from the unrounded value, so it is refused instead.
        // FI-D24: a discount is a money amount, so it has at most the base currency's decimals. It then never needs rounding, which keeps
        // CK_SalesInvoiceLines_Discount_WithinGross (Discount <= Qty * UnitPrice) true for a discount equal to an unrounded gross.
        else if (Math.Round(input.Discount, precision, MidpointRounding.AwayFromZero) != input.Discount)
        {
            Fail("discount", $"Discount can have at most {precision} decimal places.");
        }
        if (input.UnitPrice >= 0 && Math.Round(input.UnitPrice, 4, MidpointRounding.AwayFromZero) != input.UnitPrice)
        {
            Fail("unitPrice", "Unit price has too many decimals (at most 4).");
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
        else if (!productUnit.IsSellable) Fail("productUnitId", $"{unitDisplay} cannot be sold for {product.Name}.");
        if (errors.Count > 0) throw new DomainValidationException(errors);

        // The stored factor is snapshotted onto the line; history is never recomputed from configuration.
        StockQuantity.EnsureEnteredDecimals(input.Qty, productUnit.Unit.DecimalPlaces, unitDisplay, Key("qty"));
        var baseQty = input.Qty * productUnit.ConversionFactorToBase;
        StockQuantity.EnsureStorable(baseQty, Key("qty"));
        StockQuantity.EnsureWithinBaseDecimals(input.Qty, productUnit.Unit.ShortCode, baseQty, product.BaseUnit, Key("qty"));

        // The open list price is stable here: the product KEY SHARE lock blocks PricingService.SetPriceAsync (FOR UPDATE). The list price is the shelf
        // price in the product's own basis (T16), so the entered price is compared and stored as it is.
        var listPrice = await db.SellingPrices
            .Where(p => p.ProductUnitId == productUnit.Id && p.EffectiveTo == null)
            .Select(p => (decimal?)p.Price)
            .SingleOrDefaultAsync(ct);
        if (listPrice is not { } list)
        {
            Fail("productUnitId", $"{unitDisplay} of {product.Name} has no price. Set a price before selling it.");
            throw new DomainValidationException(errors);
        }

        var priceDiffers = input.UnitPrice != list;
        if (priceDiffers && !input.OverridePrice)
        {
            Fail("unitPrice", $"The price of {product.Name} ({unitDisplay}) is now {list:F2}. Refresh the item and try again.");
            throw new DomainValidationException(errors);
        }
        if ((priceDiffers || input.OverridePrice || input.Discount > 0) && !canOverridePrice)
        {
            if (priceDiffers || input.OverridePrice) Fail("unitPrice", $"You are not allowed to change the price of {product.Name}.");
            else Fail("discount", $"You are not allowed to give a discount on {product.Name}.");
            throw new DomainValidationException(errors);
        }

        var gross = input.Qty * input.UnitPrice;
        if (input.Discount > gross)
        {
            Fail("discount", $"Discount on {product.Name} cannot exceed the line amount of {gross:F2}.");
            throw new DomainValidationException(errors);
        }
        return new PreparedLine(input, product, productUnit, baseQty, list);
    }

    // The tax calculator on the cart as entered (base currency precision). Its field names are mapped to the request's: the bill discount is whichever of
    // percent/amount the caller sent; lines[i].unitPrice, discount and qty are the request's own names.
    private static TaxDocumentResult Calculate(
        CreateSaleInput input, string discountField, PlaceOfSupplyResolution place, IReadOnlyDictionary<Guid, ResolvedLineRates> rates,
        IReadOnlyList<PreparedLine> lines, BusinessSettings settings, int precision)
    {
        var calcLines = lines.Select(l => new TaxCalcLine(
            l.Input.Qty, l.BaseQty, l.Input.UnitPrice, l.Input.Discount, 0m, SalesTaxRules.PriceIncludesTax(l.Product, settings), rates[l.Product.Id].Rates)).ToList();
        try
        {
            return TaxCalculator.Calculate(new TaxCalcDocument(
                precision, place.Place, PartyTaxStatus.Unspecified, input.BillDiscountPercent, input.BillDiscountAmount, input.BillCharges, calcLines));
        }
        catch (DomainValidationException ex)
        {
            throw new DomainValidationException(ex.Errors.Select(e => e with { Field = e.Field == "billDiscount" ? discountField : e.Field }).ToList());
        }
    }

    private static SalesInvoiceLine BuildLine(
        SalesInvoice invoice, PreparedLine prepared, TaxLineResult calc, ResolvedLineRates rates, BusinessSettings settings, PlaceOfSupply place)
    {
        var lineId = Guid.NewGuid();
        var line = new SalesInvoiceLine
        {
            Id = lineId,
            SalesInvoiceId = invoice.Id,
            ProductId = prepared.Product.Id,
            ProductUnitId = prepared.Unit.Id,
            UnitId = prepared.Unit.UnitId,
            UnitCode = prepared.Unit.Unit.ShortCode,
            Qty = prepared.Input.Qty,
            ConversionFactorToBase = prepared.Unit.ConversionFactorToBase,
            BaseQty = prepared.BaseQty,
            UnitPrice = prepared.Input.UnitPrice,
            ListUnitPrice = prepared.ListPrice,
            PriceOverridden = prepared.Input.UnitPrice != prepared.ListPrice,
            TaxId = rates.MirrorTaxId, // the sales profile's mirror tax: keeps the older builds and the T25 "used version" check working
            TaxRatePercent = calc.TaxRatePercent,
            Discount = calc.LineDiscount, // FI-D24: rounded like the invoice DiscountTotal
            TaxableValue = calc.Taxable,
            TaxAmount = calc.TaxTotal,
            LineTotal = calc.LineTotal,
            TaxProfileId = rates.Rates.ProfileId,
            TaxProfileVersionId = rates.Rates.VersionId,
            PlaceOfSupply = place,
            HsnCode = rates.HsnCode,
            PriceIncludesTax = calc.PriceIncludesTax,
            BillDiscountShare = calc.BillDiscountShare,
            BillChargeShare = calc.BillChargeShare,
        };
        foreach (var r in calc.Rows)
        {
            line.Taxes.Add(new SalesInvoiceLineTax
            {
                Id = Guid.NewGuid(),
                SalesInvoiceLineId = lineId,
                TaxComponentId = r.ComponentId,
                ComponentCode = r.Code,
                Kind = r.Kind,
                Method = r.Method,
                RatePercent = r.RatePercent,
                AmountPerBaseUnit = r.AmountPerBaseUnit,
                BaseAmount = r.BaseAmount, // a per-unit row's base amount is the base quantity, not money (DQ-TP2-6)
                TaxAmount = r.TaxAmount,
                Treatment = rates.Rates.Treatment,
            });
        }
        return line;
    }

    private async Task ApplySaleLineToStockAsync(SalesInvoiceLine line, StockItem stockItem, string invoiceNo, Guid userId, CancellationToken ct)
    {
        // Stock on hand and batch remainders are kept in step, so the shortage error is a safety net: never record a
        // sale whose cost (and batch trail) covers only part of the quantity.
        var allocations = await StockPosting.IssueFifoAsync(db, stockItem, line.BaseQty,
            shortBy => new DomainValidationException("lines", $"Batch stock is short by {StockQuantity.Format(shortBy)} for this item — cannot complete the sale."), ct);

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
        line.CostOfGoodsSold = StockCost.Money(costConsumed);

        db.StockMovements.Add(MovementSnapshot.Apply(new StockMovement
        {
            Id = Guid.NewGuid(),
            ProductId = line.ProductId,
            StockLocationId = stockItem.StockLocationId,
            Type = StockMovementType.Sale,
            QuantityBaseUnits = -line.BaseQty,
            ReferenceType = "SalesInvoice",
            ReferenceId = line.SalesInvoiceId,
            ReferenceNo = invoiceNo,
            UserId = userId,
        }, line.ProductUnitId, line.UnitCode, line.ConversionFactorToBase, line.Qty));
    }
}
