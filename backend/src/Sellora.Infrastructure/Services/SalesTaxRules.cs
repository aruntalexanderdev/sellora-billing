using System.Globalization;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// The sales tax rules the preview (<see cref="TaxDocumentPreviewService"/>) and the save (<see cref="SalesService"/>) must agree on (TP-4): the price basis of a
/// line and the cap on the whole-bill discount (T15, BR19a). One implementation, so the total a cashier sees is the total that is saved.
/// </summary>
public static class SalesTaxRules
{
    /// <summary>Price basis of a sale line (T16, T18): the product's own setting, else the company's <c>SellingPricesIncludeTax</c>.</summary>
    public static bool PriceIncludesTax(Product product, BusinessSettings settings) => product.PriceIncludesTax ?? settings.SellingPricesIncludeTax;

    /// <summary>
    /// T15: the bill discount may not exceed the company cap, a percent of the bill's entered amount (Σ gross − line discount). 400 on <paramref name="field"/>
    /// (the field the caller sent: <c>billDiscountPercent</c>, <c>billDiscountAmount</c>, or <c>billDiscount</c> in the preview). No cap, or no discount: allowed.
    /// </summary>
    public static void EnsureBillDiscountCap(TaxDocumentResult result, decimal? capPercent, int precision, string field)
    {
        if (capPercent is not { } cap || result.BillDiscountAmount <= 0) return;
        var billAmount = result.Lines.Sum(l => l.Gross - l.LineDiscount);
        if (result.BillDiscountAmount > TaxMoney.Round(billAmount * cap / 100m, precision))
        {
            throw new DomainValidationException(field, $"The bill discount cannot exceed {cap.ToString("0.##", CultureInfo.InvariantCulture)}% of the bill.");
        }
    }
}
