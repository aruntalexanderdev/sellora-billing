using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/sales")]
public class SalesController(SelloraDbContext db, ISalesService salesService) : ControllerBase
{
    /// <summary>Product/price/stock lookup for the POS cart — kept under the Sales module so a Sales-only
    /// user can bill without needing Inventory permissions.</summary>
    [HttpGet("catalog")]
    [Authorize(Policy = "Sales.Create")]
    public async Task<ActionResult<IReadOnlyList<SaleCatalogProductDto>>> Catalog([FromQuery] string? search, CancellationToken ct)
    {
        var query = db.Products.Where(p => p.IsActive)
            .Include(p => p.BaseUnit).Include(p => p.Tax).Include(p => p.SellingModes.Where(m => m.IsActive))
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(s) || p.Sku.ToLower().Contains(s) || (p.Category != null && p.Category.ToLower().Contains(s)));
        }

        var products = await query.OrderBy(p => p.Name).Take(50).ToListAsync(ct);
        var productIds = products.Select(p => p.Id).ToList();
        var modeIds = products.SelectMany(p => p.SellingModes).Select(m => m.Id).ToList();

        var qtyByProduct = await db.StockItems
            .Where(s => productIds.Contains(s.ProductId))
            .ToDictionaryAsync(s => s.ProductId, s => s.QuantityOnHand, ct);

        var priceByMode = await db.SellingPrices
            .Where(p => modeIds.Contains(p.ProductSellingModeId) && p.EffectiveTo == null)
            .ToDictionaryAsync(p => p.ProductSellingModeId, p => p.Price, ct);

        var items = products.Select(p => new SaleCatalogProductDto(
            p.Id, p.Sku, p.Name, p.Category, p.BaseUnit.ShortCode,
            qtyByProduct.GetValueOrDefault(p.Id, 0m), p.Tax.RatePercent,
            p.SellingModes.Select(m => new SaleCatalogModeDto(m.Id, m.Label, m.ConversionFactorToBase, priceByMode.GetValueOrDefault(m.Id))).ToList()
        )).ToList();

        return Ok(items);
    }

    [HttpGet]
    [Authorize(Policy = "Sales.View")]
    public async Task<ActionResult<PagedResult<SalesInvoiceListItemDto>>> List(
        [FromQuery] string? search, [FromQuery] Guid? customerId, [FromQuery] PaymentStatus? paymentStatus, [FromQuery] SalesStatus? status,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.SalesInvoices.Include(i => i.Customer).AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(i => i.InvoiceNo.ToLower().Contains(s) || i.Customer.Name.ToLower().Contains(s));
        }
        if (customerId is not null) query = query.Where(i => i.CustomerId == customerId);
        if (status is not null) query = query.Where(i => i.Status == status);
        if (dateFrom is not null) query = query.Where(i => i.InvoiceDate >= dateFrom);
        if (dateTo is not null) query = query.Where(i => i.InvoiceDate <= dateTo);
        if (paymentStatus is not null)
        {
            query = paymentStatus switch
            {
                PaymentStatus.Due => query.Where(i => i.PaidAmount <= 0),
                PaymentStatus.Paid => query.Where(i => i.PaidAmount >= i.GrandTotal),
                _ => query.Where(i => i.PaidAmount > 0 && i.PaidAmount < i.GrandTotal),
            };
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.InvoiceNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(i => new SalesInvoiceListItemDto(
                i.Id, i.InvoiceNo, i.Customer.Name, i.InvoiceDate, i.GrandTotal, i.PaidAmount,
                i.GrandTotal - i.PaidAmount < 0 ? 0 : i.GrandTotal - i.PaidAmount,
                i.PaidAmount <= 0 ? PaymentStatus.Due : i.PaidAmount >= i.GrandTotal ? PaymentStatus.Paid : PaymentStatus.PartiallyPaid,
                i.Status))
            .ToListAsync(ct);

        return Ok(new PagedResult<SalesInvoiceListItemDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Sales.View")]
    public async Task<ActionResult<SalesInvoiceDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var invoice = await db.SalesInvoices
            .Include(i => i.Customer)
            .Include(i => i.CreatedByUser)
            .Include(i => i.Lines).ThenInclude(l => l.Product).ThenInclude(pr => pr.BaseUnit)
            .Include(i => i.Lines).ThenInclude(l => l.SellingMode)
            .SingleOrDefaultAsync(i => i.Id == id, ct);

        return invoice is null ? NotFound() : Ok(ToDetail(invoice));
    }

    [HttpPost]
    [Authorize(Policy = "Sales.Create")]
    public async Task<ActionResult<SalesInvoiceDetailDto>> Create(CreateSaleRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var input = new CreateSaleInput(
            request.CustomerId!.Value, request.InvoiceDate, request.PaymentMethod, request.PaidAmount,
            request.Lines.Select(l => new SaleLineInput(l.ProductId!.Value, l.SellingModeId!.Value, l.Qty, l.UnitPrice, l.Discount)).ToList());

        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
            var invoice = await salesService.CreateSaleAsync(input, userId, ct);
            return await Get(invoice.Id, ct);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = "Sales.Cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        try
        {
            await salesService.CancelSaleAsync(id, ct);
            return NoContent();
        }
        catch (DomainValidationException ex)
        {
            return Problem(statusCode: 400, title: ex.Message);
        }
    }

    private static SalesInvoiceDetailDto ToDetail(SalesInvoice i) => new(
        i.Id, i.InvoiceNo, i.CustomerId, i.Customer.Name, i.InvoiceDate,
        i.PaymentMethod, i.PaidAmount, i.DueAmount, i.PaymentStatus,
        i.Subtotal, i.TaxTotal, i.DiscountTotal, i.GrandTotal,
        i.Status, $"{i.CreatedByUser.FirstName} {i.CreatedByUser.LastName}", i.CreatedAt, i.CancelledAt,
        i.Lines.Select(l => new SalesInvoiceLineDto(
            l.Id, l.ProductId, l.Product.Name, l.SellingModeId, l.SellingMode.Label,
            l.Qty, l.BaseQty, l.Product.BaseUnit.ShortCode, l.UnitPrice,
            l.TaxRatePercent, l.Discount, l.TaxableValue, l.TaxAmount, l.LineTotal, l.CostOfGoodsSold
        )).ToList());
}
