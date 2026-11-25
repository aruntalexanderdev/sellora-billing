using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Api.Validation;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Payments;
using Sellora.Domain.Purchasing;
using Sellora.Infrastructure.Services;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/purchases")]
public class PurchasesController(SelloraDbContext db, IPurchaseService purchaseService, IEffectivePermissionService effectivePermissions) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Purchase.View")]
    public async Task<ActionResult<PagedResult<PurchaseListItemDto>>> List(
        [FromQuery] string? search, [FromQuery] Guid? supplierId, [FromQuery] PurchaseStatus? status,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] Guid? branchId = null, [FromQuery] Guid? stockLocationId = null, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        if (await this.DenyUnlessAllowedAsync(db, branchId, ct) is { } denied) return denied;

        if (stockLocationId is not null
            && (await StockVisibility.LoadAsync(db, effectivePermissions, "Purchase.View", ct)).DenyUnlessVisible(this, stockLocationId) is { } locationDenied) return locationDenied;

        var query = db.PurchaseOrders.Include(p => p.Supplier).AsQueryable();
        if (branchId is not null) query = query.Where(p => p.BranchId == branchId);
        if (stockLocationId is not null) query = query.Where(p => p.StockLocationId == stockLocationId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(p => p.PurchaseNo.ToLower().Contains(s) || p.Supplier.Name.ToLower().Contains(s));
        }
        if (supplierId is not null) query = query.Where(p => p.SupplierId == supplierId);
        if (status is not null) query = query.Where(p => p.Status == status);
        if (dateFrom is not null) query = query.Where(p => p.PurchaseDate >= dateFrom);
        if (dateTo is not null) query = query.Where(p => p.PurchaseDate <= dateTo);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(p => p.PurchaseDate).ThenByDescending(p => p.PurchaseNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new PurchaseListItemDto(
                p.Id, p.PurchaseNo, p.Supplier.Name, p.PurchaseDate, p.GrandTotal,
                p.OriginalGrandTotal - p.OriginalReturnedAmount <= 0 || p.OriginalPaidAmount >= p.OriginalGrandTotal - p.OriginalReturnedAmount ? PaymentStatus.Paid
                    : p.OriginalPaidAmount <= 0 ? PaymentStatus.Due : PaymentStatus.PartiallyPaid,
                p.Status, p.Currency.Code, p.OriginalGrandTotal, p.BranchId, null, null, p.StockLocationId))
            .ToListAsync(ct);

        var labels = await BranchLabels.LoadAsync(db, ct);
        items = items.Select(i => i with { BranchCode = labels.Code(i.BranchId), BranchName = labels.Name(i.BranchId) }).ToList();
        var locLabels = await StockLocationLabels.LoadAsync(db, ct);
        items = items.Select(i => i with { StockLocationCode = locLabels.Code(i.StockLocationId!.Value), StockLocationName = locLabels.Name(i.StockLocationId!.Value) }).ToList();

        return Ok(new PagedResult<PurchaseListItemDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Purchase.View")]
    public async Task<ActionResult<PurchaseDetailDto>> Get(Guid id, [FromQuery] Guid? stockLocationId = null, CancellationToken ct = default)
    {
        // Phase 10 (D3): an explicit location must be visible to the caller; hidden/foreign = one 403.
        var visibility = await StockVisibility.LoadAsync(db, effectivePermissions, "Purchase.View", ct);
        if (visibility.DenyUnlessVisible(this, stockLocationId) is { } denied) return denied;

        var purchase = await db.PurchaseOrders
            .Include(p => p.Supplier)
            .Include(p => p.Currency)
            .Include(p => p.CreatedByUser)
            .Include(p => p.Lines).ThenInclude(l => l.Product).ThenInclude(pr => pr.BaseUnit)
            .Include(p => p.Lines).ThenInclude(l => l.Product).ThenInclude(pr => pr.Units).ThenInclude(u => u.Unit)
            .Include(p => p.Lines).ThenInclude(l => l.ProductUnit).ThenInclude(u => u.Unit)
            .Include(p => p.Lines).ThenInclude(l => l.Batch)
            .Include(p => p.Lines).ThenInclude(l => l.Taxes)
            .Include(p => p.Payments).ThenInclude(pay => pay.CreatedByUser)
            .Include(p => p.Payments).ThenInclude(pay => pay.PurchaseReturn)
            .Include(p => p.Returns).ThenInclude(r => r.Lines)
            .AsSplitQuery()
            .SingleOrDefaultAsync(p => p.Id == id, ct);

        if (purchase is null) return NotFound();

        var baseCurrency = await db.GetBaseCurrencyAsync(ct);
        var labels = await BranchLabels.LoadAsync(db, ct);
        var locLabels = await StockLocationLabels.LoadAsync(db, ct);
        // Phase 8: what can go back is limited by the batch's balance at the purchase's own location (not the company-wide batch total).
        var batchIds = purchase.Lines.Where(l => l.Batch != null).Select(l => l.Batch!.Id).ToList();
        var returnLocationId = stockLocationId ?? purchase.StockLocationId;
        var atLocation = await db.BatchLocationBalances.AsNoTracking()
            .Where(x => x.StockLocationId == returnLocationId && batchIds.Contains(x.PurchaseBatchId))
            .ToDictionaryAsync(x => x.PurchaseBatchId, x => x.RemainingBaseQty, ct);
        var visibleIds = visibility.Ids.ToList();
        var balanceRows = await db.BatchLocationBalances.AsNoTracking()
            .Where(x => batchIds.Contains(x.PurchaseBatchId) && x.RemainingBaseQty > 0 && visibleIds.Contains(x.StockLocationId))
            .Select(x => new { x.PurchaseBatchId, x.StockLocationId, x.RemainingBaseQty })
            .ToListAsync(ct);
        var byBatch = balanceRows
            .GroupBy(x => x.PurchaseBatchId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PurchaseLineLocationBalanceDto>)g
                .Select(x => new PurchaseLineLocationBalanceDto(x.StockLocationId, visibility.Code(x.StockLocationId) ?? "", visibility.Name(x.StockLocationId) ?? "", x.RemainingBaseQty))
                .OrderBy(x => x.Code, StringComparer.Ordinal).ToList());

        // TP-3: names of the snapshotted profiles and states (the rows only carry ids).
        var profileIds = purchase.Lines.Where(l => l.TaxProfileId != null).Select(l => l.TaxProfileId!.Value).Distinct().ToList();
        var profileNames = await db.TaxProfiles.AsNoTracking().Where(t => profileIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var stateIds = new[] { purchase.PlaceOfSupplyStateId, purchase.SupplierStateId }.Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        var stateNames = await db.States.AsNoTracking().Where(st => stateIds.Contains(st.Id)).ToDictionaryAsync(st => st.Id, st => st.Name, ct);

        return Ok(ToDetail(purchase, baseCurrency, atLocation, byBatch, profileNames) with
        {
            PlaceOfSupplyStateName = purchase.PlaceOfSupplyStateId is { } ps ? stateNames.GetValueOrDefault(ps) : null,
            SupplierStateName = purchase.SupplierStateId is { } ss ? stateNames.GetValueOrDefault(ss) : null,
            Warnings = purchase.Warnings.Select(w => new PurchaseWarningDto(w.Field, w.Message)).ToList(),
            BranchCode = labels.Code(purchase.BranchId), BranchName = labels.Name(purchase.BranchId), StockLocationId = purchase.StockLocationId, StockLocationCode = locLabels.Code(purchase.StockLocationId), StockLocationName = locLabels.Name(purchase.StockLocationId),
        });
    }

    [HttpPost]
    [Authorize(Policy = "Purchase.Create")]
    public async Task<ActionResult<PurchaseDetailDto>> Create(CreatePurchaseRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);

        var input = new CreatePurchaseInput(
            request.SupplierId!.Value, request.PurchaseDate, request.SupplierInvoiceNumber, request.Notes,
            request.PaymentMethod, request.PaidAmount,
            request.Lines.Select(l => new PurchaseLineInput(l.ProductId!.Value, l.ProductUnitId!.Value, l.Qty, l.Rate, l.OtherCharges, l.Discount, l.SupplierBatchNumber)).ToList(),
            request.AccountId, key, request.StockLocationId,
            request.RatesIncludeTax, request.BillDiscountPercent, request.BillDiscountAmount, request.BillCharges);

        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
            var purchase = await purchaseService.CreatePurchaseAsync(input, userId, ct);
            return await Get(purchase.Id, null, ct);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Purchase.Edit")]
    public async Task<ActionResult<PurchaseDetailDto>> UpdateHeader(Guid id, UpdatePurchaseHeaderRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var purchase = await purchaseService.UpdateHeaderAsync(
                id, new UpdatePurchaseHeaderInput(request.SupplierInvoiceNumber, request.Notes), ct);
            if (purchase is null) return NotFound();

            return await Get(id, null, ct);
        }
        catch (DomainValidationException ex) when (ex.Field == "Status")
        {
            return Problem(statusCode: 400, title: ex.Message);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("{id:guid}/payments")]
    [Authorize(Policy = "Purchase.Pay")]
    public async Task<ActionResult<PurchaseDetailDto>> AddPayment(Guid id, AddPurchasePaymentRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
            var purchase = await purchaseService.AddPaymentAsync(
                id, new AddPurchasePaymentInput(request.Amount, request.PaymentMethod, request.ExpectedDue, request.AccountId), userId, ct);
            if (purchase is null) return NotFound();

            return await Get(id, null, ct);
        }
        catch (DomainValidationException ex) when (ex.Field == "Status")
        {
            return Problem(statusCode: 400, title: ex.Message);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("{id:guid}/refunds")]
    [Authorize(Policy = "Purchase.Refund")]
    public async Task<ActionResult<PurchaseDetailDto>> AddRefund(Guid id, AddPurchaseRefundRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var purchase = await purchaseService.AddRefundAsync(
                id, new AddPurchaseRefundInput(request.Amount, request.PaymentMethod, request.ExpectedRefundable, request.AccountId, request.Reason, request.PurchaseReturnId),
                CurrentUserId(), ct);
            if (purchase is null) return NotFound();

            return await Get(id, null, ct);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("{id:guid}/payments/{paymentId:guid}/reverse")]
    [Authorize(Policy = "Purchase.Refund")]
    public async Task<ActionResult<PurchaseDetailDto>> ReversePayment(Guid id, Guid paymentId, ReversePaymentRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var purchase = await purchaseService.ReversePaymentAsync(id, paymentId, new ReversePaymentInput(request.Reason), CurrentUserId(), ct);
            if (purchase is null) return NotFound();

            return await Get(id, null, ct);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = "Purchase.Cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        try
        {
            await purchaseService.CancelPurchaseAsync(id, CurrentUserId(), ct);
            return NoContent();
        }
        catch (DomainValidationException ex)
        {
            return Problem(statusCode: 400, title: ex.Message);
        }
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private static PurchaseDetailDto ToDetail(PurchaseOrder p, Currency baseCurrency, Dictionary<Guid, decimal> balanceByBatch, Dictionary<Guid, IReadOnlyList<PurchaseLineLocationBalanceDto>> locationsByBatch, Dictionary<Guid, string> profileNames)
    {
        var returnedByLine = p.Returns.SelectMany(r => r.Lines).GroupBy(l => l.PurchaseOrderLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.BaseQty));
        var reversed = p.Payments.Where(x => x.ReversesPaymentId != null).Select(x => x.ReversesPaymentId!.Value).ToHashSet();
        return ToDetail(p, baseCurrency, returnedByLine, reversed, balanceByBatch, locationsByBatch, profileNames);
    }

    // What can still go back to the supplier, in base units: whatever was not returned yet, but no more than is still
    // in stock from this purchase's batch (goods already sold cannot be returned).
    private static decimal ReturnableBase(PurchaseOrderLine l, decimal returnedBase, Dictionary<Guid, decimal> balanceByBatch) =>
        Math.Max(0, Math.Min(l.BaseQty - returnedBase, l.Batch is { } b ? balanceByBatch.GetValueOrDefault(b.Id) : 0));

    // The line's own unit (even if deactivated since) plus every active unit of the product.
    private static List<PurchaseReturnUnitDto> ReturnUnits(PurchaseOrderLine l, decimal returnableBase) =>
        returnableBase <= 0
            ? []
            : l.Product.Units.Where(u => u.IsActive || u.Id == l.ProductUnitId).OrderBy(u => u.SortOrder)
                .Select(u => new PurchaseReturnUnitDto(
                    u.Id, u.Unit.ShortCode, u.Label ?? u.Unit.Name, u.Unit.DecimalPlaces, u.ConversionFactorToBase, u.Id == l.ProductUnitId,
                    StockQuantity.MaxEnterableQty(returnableBase, u.ConversionFactorToBase, u.Unit.DecimalPlaces, l.Product.BaseUnit.DecimalPlaces)))
                .ToList();

    private static PurchaseLineDto ToLineDto(PurchaseOrder p, PurchaseOrderLine l, Dictionary<Guid, decimal> returnedByLine, Dictionary<Guid, decimal> balanceByBatch, Dictionary<Guid, IReadOnlyList<PurchaseLineLocationBalanceDto>> locationsByBatch, Dictionary<Guid, string> profileNames)
    {
        var returned = returnedByLine.GetValueOrDefault(l.Id);
        var returnable = p.Status == PurchaseStatus.Cancelled ? 0 : ReturnableBase(l, returned, balanceByBatch);
        return new PurchaseLineDto(
            l.Id, l.ProductId, l.Product.Name, l.ProductUnitId, l.ProductUnit.Label ?? l.ProductUnit.Unit.Name,
            l.UnitCode, l.ConversionFactorToBase, l.ProductUnit.Unit.DecimalPlaces,
            l.Qty, l.BaseQty, l.Product.BaseUnit.ShortCode, l.Product.BaseUnit.DecimalPlaces, l.Rate,
            l.TaxRatePercent, l.OtherCharges, l.Discount, l.TaxableValue, l.TaxAmount, l.LineTotal, l.UnitCost,
            returned, returnable, ReturnUnits(l, returnable),
            l.Batch?.InternalBatchNo, l.Batch?.SupplierBatchNumber, l.Batch?.ReceivedDate,
            l.Batch is { } lb && locationsByBatch.TryGetValue(lb.Id, out var lbs) ? lbs : [],
            ToTaxDtos(l.Taxes),
            l.TaxProfileId, l.TaxProfileId is { } tp ? profileNames.GetValueOrDefault(tp) : null, l.TaxProfileVersionId,
            l.PlaceOfSupply, l.PriceIncludesTax, l.HsnCode, l.BillDiscountShare, l.BillChargeShare,
            l.OriginalRate, l.OriginalDiscount, l.OriginalOtherCharges, l.OriginalBillDiscountShare, l.OriginalBillChargeShare,
            l.OriginalTaxableValue, l.OriginalTaxAmount, l.OriginalLineTotal);
    }

    // A legacy row has neither a component nor a treatment (rows written before the calculator, T10).
    internal static List<PurchaseLineTaxDto> ToTaxDtos(IEnumerable<PurchaseLineTaxBase> rows) =>
        rows.OrderBy(t => t.Kind).ThenBy(t => t.ComponentCode, StringComparer.Ordinal).Select(t => new PurchaseLineTaxDto(
            t.TaxComponentId, t.ComponentCode, t.Kind, t.Method, t.RatePercent, t.AmountPerBaseUnit, t.BaseAmount, t.TaxAmount,
            t.OriginalBaseAmount, t.OriginalTaxAmount, t.Treatment, t.TaxComponentId == null && t.Treatment == null)).ToList();

    private static PurchaseDetailDto ToDetail(PurchaseOrder p, Currency baseCurrency, Dictionary<Guid, decimal> returnedByLine, HashSet<Guid> reversed, Dictionary<Guid, decimal> balanceByBatch, Dictionary<Guid, IReadOnlyList<PurchaseLineLocationBalanceDto>> locationsByBatch, Dictionary<Guid, string> profileNames) => new(
        p.Id, p.PurchaseNo, p.SupplierId, p.Supplier.Name, p.PurchaseDate,
        p.SupplierInvoiceNumber, p.Notes, p.PaymentMethod,
        p.PaidAmount, p.DueAmount, p.PaymentStatus,
        p.Subtotal, p.TaxTotal, p.OtherChargesTotal, p.DiscountTotal, p.GrandTotal,
        p.Status, $"{p.CreatedByUser.FirstName} {p.CreatedByUser.LastName}", p.CreatedAt, p.CancelledAt,
        p.CurrencyId, p.Currency.Code, p.Currency.Symbol, p.ExchangeRateToBase, p.OriginalGrandTotal, p.OriginalPaidAmount, p.OriginalDueAmount,
        baseCurrency.Code, baseCurrency.Symbol, p.Currency.DecimalPrecision, baseCurrency.DecimalPrecision,
        p.Lines.Select(l => ToLineDto(p, l, returnedByLine, balanceByBatch, locationsByBatch, profileNames)).ToList(),
        p.Payments.OrderBy(pay => pay.CreatedAt).Select(pay => ToPaymentDto(pay, reversed)).ToList(),
        p.ReturnedAmount, p.OriginalReturnedAmount, p.NetTotal, p.OriginalNetTotal, p.OriginalRefundableAmount,
        p.Returns.OrderBy(r => r.CreatedAt).Select(r => new PurchaseReturnSummaryDto(
            r.Id, r.ReturnNo, r.ReturnDate, r.Total, r.OriginalTotal,
            p.Payments.Where(x => x.PurchaseReturnId == r.Id && x.Kind == PaymentEntryKind.Refund).Sum(x => x.Amount)
        )).ToList(),
        p.BranchId, null, null,
        TaxableTotal: p.TaxableTotal ?? p.Subtotal + p.OtherChargesTotal - p.DiscountTotal, // NULL on history (TP3-Q1)
        RatesIncludeTax: p.RatesIncludeTax, BillDiscountPercent: p.BillDiscountPercent, BillDiscountAmount: p.BillDiscountAmount, BillCharges: p.BillCharges,
        OriginalBillDiscountAmount: p.OriginalBillDiscountAmount, OriginalBillCharges: p.OriginalBillCharges,
        PlaceOfSupply: p.PlaceOfSupply, PlaceOfSupplyStateId: p.PlaceOfSupplyStateId, SupplierStateId: p.SupplierStateId, PartyTaxStatus: p.PartyTaxStatus);

    internal static PurchasePaymentDto ToPaymentDto(PurchasePayment pay, HashSet<Guid> reversed) => new(
        pay.Id, pay.Kind, pay.PaymentDate, pay.Amount, pay.ExchangeRateToBase, pay.BaseAmount, pay.PaymentMethod,
        $"{pay.CreatedByUser.FirstName} {pay.CreatedByUser.LastName}", pay.CreatedAt,
        reversed.Contains(pay.Id), pay.Reason, pay.PurchaseReturn?.ReturnNo);
}
