using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
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
                p.PaidAmount <= 0 ? PaymentStatus.Due : p.PaidAmount >= p.GrandTotal ? PaymentStatus.Paid : PaymentStatus.PartiallyPaid,
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
            .Include(p => p.Payments).ThenInclude(pay => pay.CreatedByUser)
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
            request.Lines.Select(l => new PurchaseLineInput(l.ProductId!.Value, l.SellingModeId!.Value, l.Qty, l.Rate, l.OtherCharges, l.Discount)).ToList());

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
                id, new AddPurchasePaymentInput(request.Amount, request.PaymentMethod, request.ExpectedDue), userId, ct);
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

    private static PurchaseDetailDto ToDetail(PurchaseOrder p, Currency baseCurrency) => new(
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
            l.TaxRatePercent, l.OtherCharges, l.Discount, l.TaxableValue, l.TaxAmount, l.LineTotal, l.UnitCost
        )).ToList(),
        p.Payments.OrderBy(pay => pay.CreatedAt).Select(pay => new PurchasePaymentDto(
            pay.Id, pay.PaymentDate, pay.Amount, pay.ExchangeRateToBase, pay.BaseAmount,
            pay.PaymentMethod, $"{pay.CreatedByUser.FirstName} {pay.CreatedByUser.LastName}", pay.CreatedAt
        )).ToList());
}
