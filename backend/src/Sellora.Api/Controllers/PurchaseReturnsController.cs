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
[Route("api/v1/purchase-returns")]
public class PurchaseReturnsController(SelloraDbContext db, IPurchaseService purchaseService) : ControllerBase
{
    // Viewing a return needs the same permission as viewing purchases; creating one is its own permission.
    [HttpGet]
    [Authorize(Policy = "Purchase.View")]
    public async Task<ActionResult<PagedResult<PurchaseReturnListItemDto>>> List(
        [FromQuery] string? search, [FromQuery] Guid? purchaseOrderId, [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.PurchaseReturns.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(r => r.ReturnNo.ToLower().Contains(s) || r.PurchaseOrder.PurchaseNo.ToLower().Contains(s) || r.PurchaseOrder.Supplier.Name.ToLower().Contains(s));
        }
        if (purchaseOrderId is not null) query = query.Where(r => r.PurchaseOrderId == purchaseOrderId);
        if (dateFrom is not null) query = query.Where(r => r.ReturnDate >= dateFrom);
        if (dateTo is not null) query = query.Where(r => r.ReturnDate <= dateTo);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.ReturnDate).ThenByDescending(r => r.ReturnNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new PurchaseReturnListItemDto(
                r.Id, r.ReturnNo, r.ReturnDate, r.PurchaseOrderId, r.PurchaseOrder.PurchaseNo, r.PurchaseOrder.Supplier.Name,
                r.Total, r.OriginalTotal, r.PurchaseOrder.Currency.Code,
                db.PurchasePayments.Where(p => p.PurchaseReturnId == r.Id && p.Kind == PaymentEntryKind.Refund).Sum(p => (decimal?)p.Amount) ?? 0m))
            .ToListAsync(ct);

        return Ok(new PagedResult<PurchaseReturnListItemDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Purchase.View")]
    public async Task<ActionResult<PurchaseReturnDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var r = await db.PurchaseReturns
            .Include(x => x.PurchaseOrder).ThenInclude(p => p.Supplier)
            .Include(x => x.PurchaseOrder).ThenInclude(p => p.Currency)
            .Include(x => x.CreatedByUser)
            .Include(x => x.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.BaseUnit)
            .Include(x => x.Lines).ThenInclude(l => l.PurchaseOrderLine).ThenInclude(l => l.SellingMode)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();

        var refunds = await db.PurchasePayments.Include(p => p.CreatedByUser).Include(p => p.PurchaseReturn)
            .Where(p => p.PurchaseReturnId == id && p.Kind == PaymentEntryKind.Refund).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var baseCurrency = await db.BusinessSettings.Select(s => s.BaseCurrency).SingleAsync(ct);
        var currency = r.PurchaseOrder.Currency;

        return Ok(new PurchaseReturnDetailDto(
            r.Id, r.ReturnNo, r.ReturnDate, r.PurchaseOrderId, r.PurchaseOrder.PurchaseNo, r.PurchaseOrder.Supplier.Name,
            r.Reason, r.TaxableTotal, r.TaxTotal, r.Total, r.OriginalTotal, refunds.Sum(p => p.Amount),
            currency.Code, currency.Symbol, currency.DecimalPrecision,
            baseCurrency.Code, baseCurrency.Symbol, baseCurrency.DecimalPrecision,
            $"{r.CreatedByUser.FirstName} {r.CreatedByUser.LastName}", r.CreatedAt,
            r.Lines.Select(l => new PurchaseReturnLineDto(
                l.Id, l.Product.Name, l.PurchaseOrderLine.SellingMode.Label, l.Qty, l.BaseQty, l.Product.BaseUnit.ShortCode,
                l.TaxableValue, l.TaxAmount, l.LineTotal)).ToList(),
            refunds.Select(p => PurchasesController.ToPaymentDto(p, new HashSet<Guid>())).ToList()));
    }

    [HttpPost]
    [Authorize(Policy = "Purchase.Return")]
    public async Task<ActionResult<PurchaseReturnDetailDto>> Create(CreatePurchaseReturnRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
            var refund = request.Refund is null ? null : new RefundDetails(request.Refund.Amount, request.Refund.PaymentMethod, request.Refund.AccountId, request.Refund.Reason);
            var created = await purchaseService.CreateReturnAsync(
                new CreatePurchaseReturnInput(request.PurchaseOrderId!.Value, request.ReturnDate, request.Reason,
                    request.Lines.Select(l => new PurchaseReturnLineInput(l.PurchaseOrderLineId!.Value, l.Qty)).ToList(), refund),
                userId, ct);
            return await Get(created.Id, ct);
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
}
