using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// The single legacy tax row of a document line (TP-1 design D2, T10): what the four write paths store until the calculators of TP-3/TP-4 split taxes
/// into components. Same values as <c>AddTaxProfiles.legacy-rows.sql</c> (component NULL, code = the tax name, kind Other, method Percent, rate = the line's
/// rate, base = the taxable value, amount = the tax amount; purchase rows carry no document-currency amounts) and the same id
/// (<see cref="TaxSeedData.LegacyLineTaxId"/>), so the SQL catch-up never duplicates a row. Return lines take the rate and name of the line they return.
/// </summary>
internal static class LegacyLineTax
{
    public static SalesInvoiceLineTax ForSale(SalesInvoiceLine line, string taxName) => Fill(new SalesInvoiceLineTax { SalesInvoiceLineId = line.Id }, line.Id, taxName, line.TaxRatePercent, line.TaxableValue, line.TaxAmount);

    public static PurchaseOrderLineTax ForPurchase(PurchaseOrderLine line, string taxName) => Fill(new PurchaseOrderLineTax { PurchaseOrderLineId = line.Id }, line.Id, taxName, line.TaxRatePercent, line.TaxableValue, line.TaxAmount);

    public static SalesReturnLineTax ForSalesReturn(SalesReturnLine line, SalesInvoiceLine original) =>
        Fill(new SalesReturnLineTax { SalesReturnLineId = line.Id }, line.Id, original.Tax.Name, original.TaxRatePercent, line.TaxableValue, line.TaxAmount);

    public static PurchaseReturnLineTax ForPurchaseReturn(PurchaseReturnLine line, PurchaseOrderLine original) =>
        Fill(new PurchaseReturnLineTax { PurchaseReturnLineId = line.Id }, line.Id, original.Tax.Name, original.TaxRatePercent, line.TaxableValue, line.TaxAmount);

    private static T Fill<T>(T row, Guid lineId, string taxName, decimal ratePercent, decimal taxableValue, decimal taxAmount) where T : LineTaxBase
    {
        row.Id = TaxSeedData.LegacyLineTaxId(lineId);
        row.TaxComponentId = null;
        row.ComponentCode = taxName.Length > 60 ? taxName[..60] : taxName;
        row.Kind = TaxComponentKind.Other;
        row.Method = TaxCalculationMethod.Percent;
        row.RatePercent = ratePercent;
        row.AmountPerBaseUnit = null;
        row.BaseAmount = taxableValue;
        row.TaxAmount = taxAmount;
        return row;
    }
}
