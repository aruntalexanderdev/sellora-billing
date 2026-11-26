using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>
/// The tax of a document before it is saved (TP-2). Any-of <c>Sales.Create|Purchase.Create</c> opens the endpoint; the code matching <c>direction</c> is then required (403),
/// and a sale with a whole-bill discount needs <c>Sales.BillDiscount</c> (BR19a, 403). Read-only.
/// </summary>
[ApiController]
[Route("api/v1/tax")]
public class TaxPreviewController(ITaxDocumentPreviewService service, IEffectivePermissionService effectivePermissions) : ControllerBase
{
    [HttpPost("preview")]
    [Authorize(Policy = "Sales.Create|Purchase.Create")]
    public async Task<ActionResult<TaxDocumentPreviewDto>> Preview(TaxDocumentPreviewRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var direction = request.Direction!.Value;
        var set = await effectivePermissions.GetCurrentAsync(ct);
        Guid working;
        try
        {
            working = await service.ResolveWorkingBranchIdAsync(ct); // the branch the service previews at, so the check and the preview agree
        }
        catch (DomainValidationException ex)
        {
            foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
            return ValidationProblem(ModelState);
        }
        bool Has(string code) => set.HasCompanyLevel(code) || set.HasAtBranch(code, working);

        var required = direction == TaxDirection.Sale ? "Sales.Create" : "Purchase.Create";
        if (!Has(required)) return Forbidden($"You need the {required} permission to preview this document.");
        if (direction == TaxDirection.Sale && (request.BillDiscountPercent is > 0 || request.BillDiscountAmount is > 0) && !Has("Sales.BillDiscount"))
        {
            return Forbidden("You are not allowed to give a whole-bill discount.");
        }

        var input = new TaxDocumentPreviewInput(
            direction, request.PartyId, request.Date!.Value, request.RatesIncludeTax, request.BillDiscountPercent, request.BillDiscountAmount, request.BillCharges,
            request.Lines.Select(l => new TaxPreviewLineInput(l.ProductId!.Value, l.ProductUnitId!.Value, l.Qty!.Value, l.UnitPrice!.Value, l.Discount, l.OtherCharges ?? 0m)).ToList());

        try
        {
            return Ok(ToDto(await service.PreviewAsync(input, ct)));
        }
        catch (DomainValidationException ex)
        {
            foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
            return ValidationProblem(ModelState);
        }
    }

    private ObjectResult Forbidden(string title) => Problem(statusCode: StatusCodes.Status403Forbidden, title: title);

    internal static TaxDocumentPreviewDto ToDto(TaxDocumentPreview p) => new(
        p.Direction, p.PlaceOfSupply, p.Regime, p.CountryId, p.CurrencyCode, p.Precision,
        p.Totals.Subtotal, p.Totals.DiscountTotal, p.Totals.ChargesTotal, p.Totals.TaxableTotal, p.Totals.TaxTotal, p.Totals.GrandTotal,
        p.Totals.BillDiscountAmount, p.Totals.EffectiveBillDiscountPercent,
        p.Lines.Select(l => new TaxPreviewLineDto(
            l.Index, l.ProductId, l.ProductName, l.ProductUnitId, l.UnitCode, l.BaseQty, l.ProfileId, l.ProfileCode, l.ProfileName, l.VersionId, l.VersionEffectiveFrom,
            l.Source, l.SourceName, l.HsnCode, l.HsnSource.ToString(), l.Calc.PriceIncludesTax,
            l.Calc.Gross, l.Calc.LineDiscount, l.Calc.BillDiscountShare, l.Calc.BillChargeShare, l.Calc.OtherCharges, l.Calc.Taxable,
            l.Calc.TaxRatePercent, l.Calc.TaxTotal, l.Calc.LineTotal,
            l.Calc.Rows.Select(r => new TaxRowDto(r.ComponentId, r.Code, r.Kind, r.Method, r.RatePercent, r.AmountPerBaseUnit, r.BaseAmount, r.TaxAmount)).ToList())).ToList(),
        p.Warnings.Select(w => new TaxPreviewWarningDto(w.Field, w.Message)).ToList());
}
