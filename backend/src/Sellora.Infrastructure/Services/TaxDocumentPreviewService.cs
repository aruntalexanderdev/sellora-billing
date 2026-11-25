using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// The tax of a document before it is saved (TP-2): working branch, place of supply, rate sets on the document date, price basis (sales: product, then company;
/// purchases: the document flag), precision (sales: base currency; purchases: the supplier's currency) and the unit and quantity checks of the save, then
/// <see cref="TaxCalculator"/>. Read-only: no locks, no writes.
/// </summary>
public class TaxDocumentPreviewService(SelloraDbContext db, ITaxResolver resolver) : ITaxDocumentPreviewService
{
    public async Task<TaxDocumentPreview> PreviewAsync(TaxDocumentPreviewInput input, CancellationToken ct = default)
    {
        if (input.Lines is null || input.Lines.Count == 0) throw new DomainValidationException("lines", "Add at least one item.");
        var sale = input.Direction == TaxDirection.Sale;
        var companyId = db.CompanyContext.RequireCompanyId();

        var branch = await BranchRules.ResolveWorkingBranchAsync(db, ct);
        var settings = await db.BusinessSettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => new { s.SellingPricesIncludeTax, s.MaxBillDiscountPercent }).SingleAsync(ct);

        // The party and the currency whose precision rounds the document.
        string currencyCode;
        int precision;
        if (sale)
        {
            if (input.PartyId is { } customerId)
            {
                var customer = await db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => new { c.IsActive }).SingleOrDefaultAsync(ct)
                    ?? throw new DomainValidationException("partyId", "Select a valid customer.");
                if (!customer.IsActive) throw new DomainValidationException("partyId", "This customer is inactive.");
            }
            var company = await db.GetCurrentCompanySettingsAsync(ct);
            currencyCode = company.BaseCurrencyCode;
            precision = company.BaseCurrencyDecimalPrecision;
        }
        else
        {
            if (input.PartyId is not { } supplierId) throw new DomainValidationException("partyId", "Select a supplier.");
            var supplier = await db.Suppliers.AsNoTracking().Where(s => s.Id == supplierId)
                .Select(s => new { s.IsActive, s.Currency.Code, s.Currency.DecimalPrecision }).SingleOrDefaultAsync(ct)
                ?? throw new DomainValidationException("partyId", "Select a valid supplier.");
            if (!supplier.IsActive) throw new DomainValidationException("partyId", "This supplier is inactive.");
            currencyCode = supplier.Code;
            precision = supplier.DecimalPrecision;
        }

        var place = await resolver.ResolvePlaceOfSupplyAsync(input.Direction, branch.Id, input.PartyId, ct);

        // ---- units and quantities, as on save
        var productIds = input.Lines.Select(l => l.ProductId).Distinct().ToArray();
        var unitIds = input.Lines.Select(l => l.ProductUnitId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Include(p => p.BaseUnit).Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var units = await db.ProductUnits.AsNoTracking().Include(u => u.Unit).Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);

        var errors = new List<FieldError>();
        var baseQtys = new decimal[input.Lines.Count];
        var unitCodes = new string[input.Lines.Count];
        for (var i = 0; i < input.Lines.Count; i++)
        {
            try
            {
                (baseQtys[i], unitCodes[i]) = CheckLine(i, input.Lines[i], sale, products, units);
            }
            catch (DomainValidationException ex)
            {
                errors.AddRange(ex.Errors);
            }
        }
        if (errors.Count > 0) throw new DomainValidationException(errors);

        // ---- rate sets on the document date
        var rates = await resolver.ResolveRateSetsAsync(input.Direction, place.CountryId, input.Date, input.Lines.Select(l => l.ProductId).ToList(), "lines", ct);

        var calcLines = new List<TaxCalcLine>();
        for (var i = 0; i < input.Lines.Count; i++)
        {
            var line = input.Lines[i];
            var includes = sale ? products[line.ProductId].PriceIncludesTax ?? settings.SellingPricesIncludeTax : input.RatesIncludeTax ?? false;
            calcLines.Add(new TaxCalcLine(line.Qty, baseQtys[i], line.UnitPrice, line.Discount, sale ? 0m : line.OtherCharges, includes, rates[line.ProductId].Rates));
        }

        var document = new TaxCalcDocument(
            precision, place.Place, PartyTaxStatus.Unspecified, input.BillDiscountPercent, input.BillDiscountAmount, input.BillCharges ?? 0m, calcLines);
        var result = TaxCalculator.Calculate(document);

        // T15 (BR19a): the bill discount of a sale may not exceed the company cap (a percent of the bill's entered amount; purchases are not limited).
        if (sale && settings.MaxBillDiscountPercent is { } cap && result.BillDiscountAmount > 0)
        {
            var billAmount = result.Lines.Sum(l => l.Gross - l.LineDiscount);
            if (result.BillDiscountAmount > TaxMoney.Round(billAmount * cap / 100m, precision))
            {
                throw new DomainValidationException("billDiscount", $"The bill discount cannot exceed {cap.ToString("0.##", CultureInfo.InvariantCulture)}% of the bill.");
            }
        }

        var lines = new List<TaxPreviewLine>();
        var warnings = place.Warnings.Select(w => new TaxPreviewWarning("partyId", w)).ToList();
        var warnedProducts = new HashSet<Guid>();
        for (var i = 0; i < input.Lines.Count; i++)
        {
            var line = input.Lines[i];
            var r = rates[line.ProductId];
            lines.Add(new TaxPreviewLine(
                i, line.ProductId, products[line.ProductId].Name, line.ProductUnitId, unitCodes[i], baseQtys[i], r.Rates.ProfileId, r.ProfileCode, r.ProfileName,
                r.Rates.VersionId, r.VersionEffectiveFrom, r.Source, r.SourceName, r.HsnCode, r.HsnSource, result.Lines[i]));
            if (r.HsnWarning && r.HsnWarningMessage is { } message && warnedProducts.Add(line.ProductId)) warnings.Add(new TaxPreviewWarning($"lines[{i}].hsnCode", message));
        }

        return new TaxDocumentPreview(input.Direction, place.Place, place.Regime, place.CountryId, currencyCode, precision, result, lines, warnings);
    }

    /// <summary>The product, unit and quantity rules of the sale and purchase save paths (a zero quantity is allowed in a preview, E10).</summary>
    private static (decimal BaseQty, string UnitCode) CheckLine(
        int i, TaxPreviewLineInput line, bool sale, IReadOnlyDictionary<Guid, Product> products, IReadOnlyDictionary<Guid, ProductUnit> units)
    {
        var productField = $"lines[{i}].productId";
        var unitField = $"lines[{i}].productUnitId";
        var qtyField = $"lines[{i}].qty";
        if (!products.TryGetValue(line.ProductId, out var product)) throw new DomainValidationException(productField, "Select a valid product.");
        if (!product.IsActive) throw new DomainValidationException(productField, $"{product.Name} is inactive.");
        if (!units.TryGetValue(line.ProductUnitId, out var unit)) throw new DomainValidationException(unitField, "Select a valid unit.");
        if (unit.ProductId != product.Id) throw new DomainValidationException(unitField, "The selected unit does not belong to this product.");
        var display = unit.Label ?? unit.Unit.Name;
        if (!unit.IsActive) throw new DomainValidationException(unitField, $"{display} is no longer active.");
        if (sale && !unit.IsSellable) throw new DomainValidationException(unitField, $"{display} cannot be sold for {product.Name}.");
        if (!sale && !unit.IsPurchasable) throw new DomainValidationException(unitField, $"{display} cannot be purchased for {product.Name}.");

        if (line.Qty < 0) throw new DomainValidationException(qtyField, "Quantity cannot be negative.");
        StockQuantity.EnsureEnteredDecimals(line.Qty, unit.Unit.DecimalPlaces, display, qtyField);
        var baseQty = line.Qty * unit.ConversionFactorToBase;
        StockQuantity.EnsureStorable(baseQty, qtyField);
        StockQuantity.EnsureWithinBaseDecimals(line.Qty, unit.Unit.ShortCode, baseQty, product.BaseUnit, qtyField);
        return (baseQty, unit.Unit.ShortCode);
    }
}
