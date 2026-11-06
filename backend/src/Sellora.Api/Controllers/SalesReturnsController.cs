using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Payments;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/sales-returns")]
public class SalesReturnsController(SelloraDbContext db, ISalesService salesService) : ControllerBase
{
    // Viewing a return needs the same permission as viewing sales; creating one is its own permission.
    [HttpGet]
    [Authorize(Policy = "Sales.View")]
    public async Task<ActionResult<PagedResult<SalesReturnListItemDto>>> List(
        [FromQuery] string? search, [FromQuery] Guid? salesInvoiceId, [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.SalesReturns.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(r => r.ReturnNo.ToLower().Contains(s) || r.SalesInvoice.InvoiceNo.ToLower().Contains(s) || r.SalesInvoice.Customer.Name.ToLower().Contains(s));
        }
        if (salesInvoiceId is not null) query = query.Where(r => r.SalesInvoiceId == salesInvoiceId);
        if (dateFrom is not null) query = query.Where(r => r.ReturnDate >= dateFrom);
        if (dateTo is not null) query = query.Where(r => r.ReturnDate <= dateTo);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.ReturnDate).ThenByDescending(r => r.ReturnNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new SalesReturnListItemDto(
                r.Id, r.ReturnNo, r.ReturnDate, r.SalesInvoiceId, r.SalesInvoice.InvoiceNo, r.SalesInvoice.Customer.Name, r.Total,
                db.SalesPayments.Where(p => p.SalesReturnId == r.Id && p.Kind == PaymentEntryKind.Refund).Sum(p => (decimal?)p.Amount) ?? 0m))
            .ToListAsync(ct);

        return Ok(new PagedResult<SalesReturnListItemDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Sales.View")]
    public async Task<ActionResult<SalesReturnDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var r = await db.SalesReturns
            .Include(x => x.SalesInvoice).ThenInclude(i => i.Customer)
            .Include(x => x.CreatedByUser)
            .Include(x => x.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.BaseUnit)
            .Include(x => x.Lines).ThenInclude(l => l.ProductUnit).ThenInclude(u => u.Unit)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();

        var refunds = await db.SalesPayments.Include(p => p.CreatedByUser).Include(p => p.SalesReturn)
            .Where(p => p.SalesReturnId == id && p.Kind == PaymentEntryKind.Refund).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var noReversed = new HashSet<Guid>();

        return Ok(new SalesReturnDetailDto(
            r.Id, r.ReturnNo, r.ReturnDate, r.SalesInvoiceId, r.SalesInvoice.InvoiceNo, r.SalesInvoice.Customer.Name,
            r.Reason, r.TaxableTotal, r.TaxTotal, r.Total, refunds.Sum(p => p.Amount),
            $"{r.CreatedByUser.FirstName} {r.CreatedByUser.LastName}", r.CreatedAt,
            r.Lines.Select(l => new SalesReturnLineDto(
                l.Id, l.Product.Name, l.ProductUnit.Label ?? l.ProductUnit.Unit.Name, l.UnitCode, l.Qty, l.BaseQty, l.Product.BaseUnit.ShortCode, l.Product.BaseUnit.DecimalPlaces,
                l.TaxableValue, l.TaxAmount, l.LineTotal)).ToList(),
            refunds.Select(p => SalesController.ToPaymentDto(p, noReversed)).ToList()));
    }

    [HttpPost]
    [Authorize(Policy = "Sales.Return")]
    public async Task<ActionResult<SalesReturnDetailDto>> Create(CreateSaleReturnRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
            var refund = request.Refund is null ? null : new RefundDetails(request.Refund.Amount, request.Refund.PaymentMethod, request.Refund.AccountId, request.Refund.Reason);
            var created = await salesService.CreateReturnAsync(
                new CreateSaleReturnInput(request.SalesInvoiceId!.Value, request.ReturnDate, request.Reason,
                    request.Lines.Select(l => new SaleReturnLineInput(l.SalesInvoiceLineId!.Value, l.ProductUnitId, l.Qty)).ToList(), refund),
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
