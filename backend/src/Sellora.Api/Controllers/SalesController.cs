using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Validation;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Payments;
using Sellora.Domain.Sales;
using Sellora.Domain.Auth;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

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
        var query = CatalogQuery();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var s = term.ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(s) || p.Sku.ToLower().Contains(s) || (p.Category != null && p.Category.ToLower().Contains(s))
                || p.Units.Any(u => u.IsActive && u.IsSellable && u.Barcode == term));
        }

        return Ok(await BuildCatalogItemsAsync(query.OrderBy(p => p.Name).Take(50), ct));
    }

    /// <summary>Exact barcode match on an active, sellable product unit. Lives under Sales (not Products) so a
    /// cashier holding only Sales.* can scan. Weighed/price-embedded codes are not supported and simply miss.
    /// An unpriced unit is returned with a null price; the POS refuses it.</summary>
    [HttpGet("catalog/by-barcode/{code}")]
    [Authorize(Policy = "Sales.Create")]
    public async Task<ActionResult<SaleBarcodeMatchDto>> CatalogByBarcode(string code, CancellationToken ct)
    {
        var term = code.Trim();
        var unit = string.IsNullOrEmpty(term) ? null : await db.ProductUnits.AsNoTracking()
            .Where(u => u.Barcode == term && u.IsActive && u.IsSellable && u.Product.IsActive)
            .Select(u => new { u.Id, u.ProductId })
            .SingleOrDefaultAsync(ct);
        if (unit is null)
        {
            return Problem(statusCode: 404, title: $"No sellable item has barcode {term}.");
        }

        var item = (await BuildCatalogItemsAsync(CatalogQuery().Where(p => p.Id == unit.ProductId), ct)).Single();
        return Ok(new SaleBarcodeMatchDto(item, unit.Id));
    }

    private IQueryable<Product> CatalogQuery() =>
        db.Products.AsNoTracking().Where(p => p.IsActive)
            .Include(p => p.BaseUnit).Include(p => p.Tax).Include(p => p.Units.Where(u => u.IsActive && u.IsSellable).OrderBy(u => u.SortOrder)).ThenInclude(u => u.Unit);

    private async Task<List<SaleCatalogProductDto>> BuildCatalogItemsAsync(IQueryable<Product> query, CancellationToken ct)
    {
        var products = await query.ToListAsync(ct);
        var productIds = products.Select(p => p.Id).ToList();
        var unitIds = products.SelectMany(p => p.Units).Select(u => u.Id).ToList();

        var qtyByProduct = await db.StockItems
            .Where(s => productIds.Contains(s.ProductId))
            .ToDictionaryAsync(s => s.ProductId, s => s.QuantityOnHand, ct);

        var priceByUnit = await db.SellingPrices
            .Where(p => unitIds.Contains(p.ProductUnitId) && p.EffectiveTo == null)
            .ToDictionaryAsync(p => p.ProductUnitId, p => p.Price, ct);

        return products.Select(p => new SaleCatalogProductDto(
            p.Id, p.Sku, p.Name, p.Category, p.BaseUnit.ShortCode,
            qtyByProduct.GetValueOrDefault(p.Id, 0m), p.Tax.RatePercent,
            p.Units.OrderBy(u => u.SortOrder).Select(u => new SaleCatalogUnitDto(
                u.Id, u.UnitId, u.Unit.ShortCode, u.Label ?? u.Unit.Name,
                u.ConversionFactorToBase, u.Unit.DecimalPlaces, u.IsBaseUnit, u.IsDefaultSalesUnit,
                priceByUnit.TryGetValue(u.Id, out var price) ? price : null, u.Barcode)).ToList()
        )).ToList();
    }

    [HttpGet]
    [Authorize(Policy = "Sales.View")]
    public async Task<ActionResult<PagedResult<SalesInvoiceListItemDto>>> List(
        [FromQuery] string? search, [FromQuery] Guid? customerId, [FromQuery] PaymentStatus? paymentStatus, [FromQuery] SalesStatus? status,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] Guid? branchId = null, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        if (await this.DenyUnlessAllowedAsync(db, branchId, ct) is { } denied) return denied;

        var query = db.SalesInvoices.Include(i => i.Customer).AsQueryable();
        if (branchId is not null) query = query.Where(i => i.BranchId == branchId);

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
                PaymentStatus.Due => query.Where(i => i.PaidAmount <= 0 && i.GrandTotal - i.ReturnedAmount > 0),
                PaymentStatus.Paid => query.Where(i => i.GrandTotal - i.ReturnedAmount <= 0 || i.PaidAmount >= i.GrandTotal - i.ReturnedAmount),
                _ => query.Where(i => i.PaidAmount > 0 && i.PaidAmount < i.GrandTotal - i.ReturnedAmount),
            };
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.InvoiceNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(i => new SalesInvoiceListItemDto(
                i.Id, i.InvoiceNo, i.Customer.Name, i.InvoiceDate, i.GrandTotal, i.PaidAmount,
                i.Status == SalesStatus.Cancelled || i.GrandTotal - i.ReturnedAmount - i.PaidAmount < 0 ? 0 : i.GrandTotal - i.ReturnedAmount - i.PaidAmount,
                i.GrandTotal - i.ReturnedAmount <= 0 || i.PaidAmount >= i.GrandTotal - i.ReturnedAmount ? PaymentStatus.Paid
                    : i.PaidAmount <= 0 ? PaymentStatus.Due : PaymentStatus.PartiallyPaid,
                i.Status, i.BranchId, null, null))
            .ToListAsync(ct);

        var labels = await BranchLabels.LoadAsync(db, ct);
        items = items.Select(i => i with { BranchCode = labels.Code(i.BranchId), BranchName = labels.Name(i.BranchId) }).ToList();

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
            .Include(i => i.Lines).ThenInclude(l => l.ProductUnit).ThenInclude(u => u.Unit)
            .Include(i => i.Lines).ThenInclude(l => l.Product).ThenInclude(pr => pr.Units).ThenInclude(u => u.Unit)
            .Include(i => i.Lines).ThenInclude(l => l.BatchConsumptions).ThenInclude(c => c.PurchaseBatch)
            .Include(i => i.Payments).ThenInclude(pay => pay.CreatedByUser)
            .Include(i => i.Payments).ThenInclude(pay => pay.SalesReturn)
            .Include(i => i.Returns).ThenInclude(r => r.Lines)
            .AsSplitQuery()
            .SingleOrDefaultAsync(i => i.Id == id, ct);

        if (invoice is null) return NotFound();

        // Seller GSTIN = the branch's own GSTIN, else the company's (current value; a snapshot on the invoice is Phase 10).
        var labels = await BranchLabels.LoadAsync(db, ct);
        var branchGstin = await db.Branches.AsNoTracking()
            .Where(b => b.CompanyId == invoice.CompanyId && b.Id == invoice.BranchId).Select(b => b.Gstin).SingleOrDefaultAsync(ct);
        var sellerGstin = branchGstin ?? await db.GetCompanyGstinAsync(ct);
        // Batch source labels: documented "Branch"-filter bypass (D7). A batch bought or adjusted at another branch must still be
        // labelled for a branch-restricted user (the reference number is company-internal traceability); the label is looked up
        // by batch id instead of Include-ing PurchaseOrder/StockAdjustment, which the filter would null out.
        var batchIds = invoice.Lines.SelectMany(l => l.BatchConsumptions).Select(c => c.PurchaseBatchId).Distinct().ToList();
        var batchSources = (await db.PurchaseBatches.IgnoreQueryFilters([BranchQueryFilter.Name])
            .Where(b => batchIds.Contains(b.Id))
            .Select(b => new
            {
                b.Id,
                IsPurchase = b.PurchaseOrderLineId != null,
                RefNo = b.PurchaseOrderLineId != null ? b.PurchaseOrderLine!.PurchaseOrder.PurchaseNo : b.StockAdjustment!.AdjustmentNo,
                PurchaseOrderId = b.PurchaseOrderLineId != null ? b.PurchaseOrderLine!.PurchaseOrderId : (Guid?)null,
            })
            .ToListAsync(ct))
            .ToDictionary(b => b.Id, b => new BatchSource(b.IsPurchase ? "Purchase" : "Adjustment", b.RefNo, b.PurchaseOrderId));
        return Ok(ToDetail(invoice, batchSources) with { BranchCode = labels.Code(invoice.BranchId), BranchName = labels.Name(invoice.BranchId), SellerGstin = sellerGstin });
    }

    [HttpPost]
    [Authorize(Policy = "Sales.Create")]
    public async Task<ActionResult<SalesInvoiceDetailDto>> Create(CreateSaleRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);

        var input = new CreateSaleInput(
            request.CustomerId!.Value, request.InvoiceDate, request.PaymentMethod, request.PaidAmount,
            request.Lines.Select(l => new SaleLineInput(l.ProductId!.Value, l.ProductUnitId!.Value, l.Qty, l.UnitPrice, l.Discount, l.OverridePrice)).ToList(),
            request.AccountId, key, User.HasClaim(AuthClaimTypes.Permission, "Sales.PriceOverride"));

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

    [HttpPost("{id:guid}/payments")]
    [Authorize(Policy = "Sales.Receive")]
    public async Task<ActionResult<SalesInvoiceDetailDto>> AddPayment(Guid id, AddSalePaymentRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
            var invoice = await salesService.AddPaymentAsync(
                id, new AddSalePaymentInput(request.Amount, request.PaymentMethod, request.ExpectedDue, request.AccountId), userId, ct);
            if (invoice is null) return NotFound();

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
    [Authorize(Policy = "Sales.Refund")]
    public async Task<ActionResult<SalesInvoiceDetailDto>> AddRefund(Guid id, AddSaleRefundRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var invoice = await salesService.AddRefundAsync(
                id, new AddSaleRefundInput(request.Amount, request.PaymentMethod, request.ExpectedRefundable, request.AccountId, request.Reason, request.SalesReturnId),
                CurrentUserId(), ct);
            if (invoice is null) return NotFound();

            return await Get(id, ct);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("{id:guid}/payments/{paymentId:guid}/reverse")]
    [Authorize(Policy = "Sales.Refund")]
    public async Task<ActionResult<SalesInvoiceDetailDto>> ReversePayment(Guid id, Guid paymentId, ReversePaymentRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var invoice = await salesService.ReversePaymentAsync(id, paymentId, new ReversePaymentInput(request.Reason), CurrentUserId(), ct);
            if (invoice is null) return NotFound();

            return await Get(id, ct);
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
            await salesService.CancelSaleAsync(id, CurrentUserId(), ct);
            return NoContent();
        }
        catch (DomainValidationException ex)
        {
            return Problem(statusCode: 400, title: ex.Message);
        }
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private sealed record BatchSource(string SourceType, string RefNo, Guid? PurchaseOrderId);

    private static SalesInvoiceDetailDto ToDetail(SalesInvoice i, IReadOnlyDictionary<Guid, BatchSource> batchSources)
    {
        var returnedByLine = i.Returns.SelectMany(r => r.Lines).GroupBy(l => l.SalesInvoiceLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.BaseQty));
        var reversed = i.Payments.Where(p => p.ReversesPaymentId != null).Select(p => p.ReversesPaymentId!.Value).ToHashSet();
        return ToDetail(i, returnedByLine, reversed, batchSources);
    }

    private static SalesInvoiceDetailDto ToDetail(SalesInvoice i, Dictionary<Guid, decimal> returnedByLine, HashSet<Guid> reversed, IReadOnlyDictionary<Guid, BatchSource> batchSources) => new(
        i.Id, i.InvoiceNo, i.CustomerId, i.Customer.Name, i.InvoiceDate,
        i.PaymentMethod, i.PaidAmount, i.DueAmount, i.PaymentStatus,
        i.Subtotal, i.TaxTotal, i.DiscountTotal, i.GrandTotal,
        i.Status, $"{i.CreatedByUser.FirstName} {i.CreatedByUser.LastName}", i.CreatedAt, i.CancelledAt,
        i.Lines.Select(l => new SalesInvoiceLineDto(
            l.Id, l.ProductId, l.Product.Name, l.ProductUnitId, l.ProductUnit.Label ?? l.ProductUnit.Unit.Name, l.UnitCode,
            l.ConversionFactorToBase, l.ProductUnit.Unit.DecimalPlaces,
            l.Qty, l.BaseQty, l.Product.BaseUnit.ShortCode, l.Product.BaseUnit.DecimalPlaces,
            l.UnitPrice, l.ListUnitPrice, l.PriceOverridden,
            l.TaxRatePercent, l.Discount, l.TaxableValue, l.TaxAmount, l.LineTotal, l.CostOfGoodsSold,
            returnedByLine.GetValueOrDefault(l.Id), ReturnableBase(i, l, returnedByLine), ReturnUnits(i, l, returnedByLine),
            l.BatchConsumptions.OrderBy(c => c.PurchaseBatch.BatchSequence).Select(c => new SaleBatchConsumptionDto(
                c.PurchaseBatchId, PurchaseBatch.FormatInternalBatchNo(c.PurchaseBatch.BatchSequence), c.PurchaseBatch.SupplierBatchNumber,
                batchSources[c.PurchaseBatchId].SourceType,
                batchSources[c.PurchaseBatchId].RefNo,
                batchSources[c.PurchaseBatchId].PurchaseOrderId,
                c.QtyBaseUnits, c.ReturnedQtyBaseUnits, c.UnitCost)).ToList()
        )).ToList(),
        i.Payments.OrderBy(pay => pay.CreatedAt).Select(pay => ToPaymentDto(pay, reversed)).ToList(),
        i.ReturnedAmount, i.NetTotal, i.RefundableAmount,
        i.Returns.OrderBy(r => r.CreatedAt).Select(r => new SalesReturnSummaryDto(
            r.Id, r.ReturnNo, r.ReturnDate, r.Total,
            i.Payments.Where(p => p.SalesReturnId == r.Id && p.Kind == PaymentEntryKind.Refund).Sum(p => p.Amount)
        )).ToList(),
        i.BranchId, null, null, null);

    private static decimal ReturnableBase(SalesInvoice i, SalesInvoiceLine l, Dictionary<Guid, decimal> returnedByLine) =>
        i.Status == SalesStatus.Cancelled ? 0 : Math.Max(0, l.BaseQty - returnedByLine.GetValueOrDefault(l.Id));

    // The line's own unit (even if deactivated since) plus every active unit of the product.
    private static List<SaleReturnUnitDto> ReturnUnits(SalesInvoice i, SalesInvoiceLine l, Dictionary<Guid, decimal> returnedByLine)
    {
        var returnable = ReturnableBase(i, l, returnedByLine);
        return returnable <= 0
            ? []
            : l.Product.Units.Where(u => u.IsActive || u.Id == l.ProductUnitId).OrderBy(u => u.SortOrder)
                .Select(u => new SaleReturnUnitDto(
                    u.Id, u.Unit.ShortCode, u.Label ?? u.Unit.Name, u.Unit.DecimalPlaces, u.ConversionFactorToBase, u.Id == l.ProductUnitId,
                    StockQuantity.MaxEnterableQty(returnable, u.ConversionFactorToBase, u.Unit.DecimalPlaces, l.Product.BaseUnit.DecimalPlaces)))
                .ToList();
    }

    internal static SalesPaymentDto ToPaymentDto(SalesPayment pay, HashSet<Guid> reversed) => new(
        pay.Id, pay.Kind, pay.PaymentDate, pay.Amount, pay.PaymentMethod,
        $"{pay.CreatedByUser.FirstName} {pay.CreatedByUser.LastName}", pay.CreatedAt,
        reversed.Contains(pay.Id), pay.Reason, pay.SalesReturn?.ReturnNo);
}
