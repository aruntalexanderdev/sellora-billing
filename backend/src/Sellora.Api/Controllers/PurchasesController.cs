using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Payments;
using Sellora.Domain.Purchasing;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/purchases")]
public class PurchasesController(SelloraDbContext db, IPurchaseService purchaseService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Purchase.View")]
    public async Task<ActionResult<PagedResult<PurchaseListItemDto>>> List(
        [FromQuery] string? search, [FromQuery] Guid? supplierId, [FromQuery] PurchaseStatus? status,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.PurchaseOrders.Include(p => p.Supplier).AsQueryable();

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
                p.Status, p.Currency.Code, p.OriginalGrandTotal))
            .ToListAsync(ct);

        return Ok(new PagedResult<PurchaseListItemDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Purchase.View")]
    public async Task<ActionResult<PurchaseDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var purchase = await db.PurchaseOrders
            .Include(p => p.Supplier)
            .Include(p => p.Currency)
            .Include(p => p.CreatedByUser)
            .Include(p => p.Lines).ThenInclude(l => l.Product).ThenInclude(pr => pr.BaseUnit)
            .Include(p => p.Lines).ThenInclude(l => l.SellingMode)
            .Include(p => p.Lines).ThenInclude(l => l.Batch)
            .Include(p => p.Payments).ThenInclude(pay => pay.CreatedByUser)
            .Include(p => p.Payments).ThenInclude(pay => pay.PurchaseReturn)
            .Include(p => p.Returns).ThenInclude(r => r.Lines)
            .AsSplitQuery()
            .SingleOrDefaultAsync(p => p.Id == id, ct);

        if (purchase is null) return NotFound();

        var baseCurrency = await db.BusinessSettings.Select(s => s.BaseCurrency).SingleAsync(ct);
        return Ok(ToDetail(purchase, baseCurrency));
    }

    [HttpPost]
    [Authorize(Policy = "Purchase.Create")]
    public async Task<ActionResult<PurchaseDetailDto>> Create(CreatePurchaseRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var input = new CreatePurchaseInput(
            request.SupplierId!.Value, request.PurchaseDate, request.SupplierInvoiceNumber, request.Notes,
            request.PaymentMethod, request.PaidAmount,
            request.Lines.Select(l => new PurchaseLineInput(l.ProductId!.Value, l.SellingModeId!.Value, l.Qty, l.Rate, l.OtherCharges, l.Discount, l.SupplierBatchNumber)).ToList(),
            request.AccountId);

        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
            var purchase = await purchaseService.CreatePurchaseAsync(input, userId, ct);
            return await Get(purchase.Id, ct);
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

            return await Get(id, ct);
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

            return await Get(id, ct);
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

            return await Get(id, ct);
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

            return await Get(id, ct);
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
            await purchaseService.CancelPurchaseAsync(id, ct);
            return NoContent();
        }
        catch (DomainValidationException ex)
        {
            return Problem(statusCode: 400, title: ex.Message);
        }
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private static PurchaseDetailDto ToDetail(PurchaseOrder p, Currency baseCurrency)
    {
        var returnedByLine = p.Returns.SelectMany(r => r.Lines).GroupBy(l => l.PurchaseOrderLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.Qty));
        var reversed = p.Payments.Where(x => x.ReversesPaymentId != null).Select(x => x.ReversesPaymentId!.Value).ToHashSet();
        return ToDetail(p, baseCurrency, returnedByLine, reversed);
    }

    // What can still go back to the supplier: whatever was not returned yet, but no more than is still in stock
    // from this purchase's batch (goods already sold cannot be returned).
    private static decimal Returnable(PurchaseOrderLine l, decimal returned)
    {
        var inStock = l.Batch is null || l.ConversionFactorToBase <= 0 ? 0 : l.Batch.RemainingBaseQty / l.ConversionFactorToBase;
        return Math.Max(0, Math.Min(l.Qty - returned, inStock));
    }

    private static PurchaseDetailDto ToDetail(PurchaseOrder p, Currency baseCurrency, Dictionary<Guid, decimal> returnedByLine, HashSet<Guid> reversed) => new(
        p.Id, p.PurchaseNo, p.SupplierId, p.Supplier.Name, p.PurchaseDate,
        p.SupplierInvoiceNumber, p.Notes, p.PaymentMethod,
        p.PaidAmount, p.DueAmount, p.PaymentStatus,
        p.Subtotal, p.TaxTotal, p.OtherChargesTotal, p.DiscountTotal, p.GrandTotal,
        p.Status, $"{p.CreatedByUser.FirstName} {p.CreatedByUser.LastName}", p.CreatedAt, p.CancelledAt,
        p.CurrencyId, p.Currency.Code, p.Currency.Symbol, p.ExchangeRateToBase, p.OriginalGrandTotal, p.OriginalPaidAmount, p.OriginalDueAmount,
        baseCurrency.Code, baseCurrency.Symbol, p.Currency.DecimalPrecision, baseCurrency.DecimalPrecision,
        p.Lines.Select(l => new PurchaseLineDto(
            l.Id, l.ProductId, l.Product.Name, l.SellingModeId, l.SellingMode.Label,
            l.Qty, l.BaseQty, l.Product.BaseUnit.ShortCode, l.Rate,
            l.TaxRatePercent, l.OtherCharges, l.Discount, l.TaxableValue, l.TaxAmount, l.LineTotal, l.UnitCost,
            returnedByLine.GetValueOrDefault(l.Id), p.Status == PurchaseStatus.Cancelled ? 0 : Returnable(l, returnedByLine.GetValueOrDefault(l.Id)),
            l.Batch != null ? l.Batch.InternalBatchNo : null, l.Batch != null ? l.Batch.SupplierBatchNumber : null, l.Batch != null ? l.Batch.ReceivedDate : null
        )).ToList(),
        p.Payments.OrderBy(pay => pay.CreatedAt).Select(pay => ToPaymentDto(pay, reversed)).ToList(),
        p.ReturnedAmount, p.OriginalReturnedAmount, p.NetTotal, p.OriginalNetTotal, p.OriginalRefundableAmount,
        p.Returns.OrderBy(r => r.CreatedAt).Select(r => new PurchaseReturnSummaryDto(
            r.Id, r.ReturnNo, r.ReturnDate, r.Total, r.OriginalTotal,
            p.Payments.Where(x => x.PurchaseReturnId == r.Id && x.Kind == PaymentEntryKind.Refund).Sum(x => x.Amount)
        )).ToList());

    internal static PurchasePaymentDto ToPaymentDto(PurchasePayment pay, HashSet<Guid> reversed) => new(
        pay.Id, pay.Kind, pay.PaymentDate, pay.Amount, pay.ExchangeRateToBase, pay.BaseAmount, pay.PaymentMethod,
        $"{pay.CreatedByUser.FirstName} {pay.CreatedByUser.LastName}", pay.CreatedAt,
        reversed.Contains(pay.Id), pay.Reason, pay.PurchaseReturn?.ReturnNo);
}
