using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Sellora.Api.Contracts;
using Sellora.Api.Validation;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Accounts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Api.Controllers;

/// <summary>Reads, plus reversal of manual entries. Financial transactions are otherwise written only as a side effect of the source record
/// (a purchase payment, sales payment or expense) in the same database transaction, or by the account endpoints (opening balance, transfer, funding).</summary>
[ApiController]
[Route("api/v1/financial-transactions")]
public class FinancialTransactionsController(SelloraDbContext db, IFinancialAccountService accountService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "FinancialTransactions.View")]
    public async Task<ActionResult<FinancialTransactionListDto>> List(
        [FromQuery] string? search, [FromQuery] FinancialTransactionType? type, [FromQuery] MoneyDirection? direction,
        [FromQuery] FinancialTransactionStatus? status, [FromQuery] Guid? accountId, [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Phase 01 (G7): an explicit account filter must be a visible account (Branch filter = read scope; company-level accounts are visible); else one 403, no existence leak.
        if (accountId is { } filterAccount && !await db.FinancialAccounts.AnyAsync(x => x.Id == filterAccount, ct))
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "You do not have access to this account.",
                extensions: new Dictionary<string, object?> { ["code"] = Sellora.Api.Authorization.CompanyContextMiddleware.BranchAccessDeniedCode });
        }

        // Phase 5 D7: visibility follows the source document / account (FinancialTransactionVisibility.VisibleTo); roots are filtered before projection.
        var query = db.FinancialTransactions.VisibleTo(db);
        if (type is not null) query = query.Where(t => t.Type == type);
        if (direction is not null) query = query.Where(t => t.Direction == direction);
        if (status is not null) query = query.Where(t => t.Status == status);
        if (accountId is not null) query = query.Where(t => t.AccountId == accountId);
        if (dateFrom is not null) query = query.Where(t => t.TransactionDate >= dateFrom);
        if (dateTo is not null) query = query.Where(t => t.TransactionDate <= dateTo);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(t =>
                (t.PurchasePayment != null && (db.PurchaseOrders.Any(o => o.Id == t.PurchasePayment.PurchaseOrderId && o.PurchaseNo.ToLower().Contains(s))
                    || db.Suppliers.Any(x => x.Id == t.PurchasePayment.PurchaseOrder.SupplierId && x.Name.ToLower().Contains(s))))
                || (t.SalesPayment != null && (db.SalesInvoices.Any(i => i.Id == t.SalesPayment.SalesInvoiceId && i.InvoiceNo.ToLower().Contains(s))
                    || db.Customers.Any(x => x.Id == t.SalesPayment.SalesInvoice.CustomerId && x.Name.ToLower().Contains(s))))
                || (t.Expense != null && (t.Expense.ExpenseNo.ToLower().Contains(s) || t.Expense.Category.Name.ToLower().Contains(s)))
                || (t.Counterparty != null && t.Counterparty.ToLower().Contains(s))
                || (t.SourceReference != null && t.SourceReference.ToLower().Contains(s)));
        }

        var total = await query.CountAsync(ct);
        // Opening balances, transfers and the reversals of either are not money entering or leaving the business; funding is, and its reversal nets it off.
        // Money in/out = business flows only, matching the cash-flow report (FI-D19, review): opening balances, transfers, funding and every
        // reversal are capital/internal movements, shown as rows but never counted here.
        var external = query.Where(t => t.Type != FinancialTransactionType.OpeningBalance && t.Type != FinancialTransactionType.Transfer
            && t.Type != FinancialTransactionType.Funding && t.Type != FinancialTransactionType.Reversal);
        var moneyIn = await external.Where(t => t.Status == FinancialTransactionStatus.Active && t.Direction == MoneyDirection.In).SumAsync(t => (decimal?)t.BaseAmount, ct) ?? 0m;
        var moneyOut = await external.Where(t => t.Status == FinancialTransactionStatus.Active && t.Direction == MoneyDirection.Out).SumAsync(t => (decimal?)t.BaseAmount, ct) ?? 0m;

        var items = await query
            .OrderByDescending(t => t.TransactionDate).ThenByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(t => new FinancialTransactionDto(
                t.Id, t.TransactionDate, t.Type, t.Direction, t.Status,
                // Party names are looked up by id rather than through PurchaseOrder.Supplier / SalesInvoice.Customer:
                // with the company query filter EF turns a second required hop after an optional navigation into a
                // top-level INNER JOIN, which silently dropped every transaction without that payment kind.
                t.PurchasePayment != null ? db.Suppliers.Where(x => x.Id == t.PurchasePayment.PurchaseOrder.SupplierId).Select(x => x.Name).FirstOrDefault() ?? ""
                    : t.SalesPayment != null ? db.Customers.Where(x => x.Id == t.SalesPayment.SalesInvoice.CustomerId).Select(x => x.Name).FirstOrDefault() ?? ""
                    : t.Expense != null ? t.Expense.Category.Name
                    : t.Type == FinancialTransactionType.Transfer || (t.Type == FinancialTransactionType.Reversal && t.TransferGroupId != null)
                        ? db.FinancialTransactions.Where(o => o.TransferGroupId == t.TransferGroupId && o.Id != t.Id).Select(o => o.Account!.Name).FirstOrDefault() ?? ""
                    : t.CustomerId != null ? db.Customers.Where(x => x.Id == t.CustomerId).Select(x => x.Name).FirstOrDefault() ?? ""
                    : t.Type == FinancialTransactionType.Funding && t.Counterparty != null ? t.Counterparty
                        : t.Account!.Name,
                t.PurchasePayment != null ? "Purchase" : t.SalesPayment != null ? "Sale" : t.Expense != null ? "Expense" : null,
                t.PurchasePayment != null ? t.PurchasePayment.PurchaseOrderId
                    : t.SalesPayment != null ? t.SalesPayment.SalesInvoiceId
                    : t.ExpenseId,
                t.PurchasePayment != null ? db.PurchaseOrders.Where(o => o.Id == t.PurchasePayment.PurchaseOrderId).Select(o => o.PurchaseNo).FirstOrDefault()
                    : t.SalesPayment != null ? db.SalesInvoices.Where(i => i.Id == t.SalesPayment.SalesInvoiceId).Select(i => i.InvoiceNo).FirstOrDefault()
                    : t.Expense != null ? t.Expense.ExpenseNo : null,
                t.PaymentMethod, t.Amount, t.Currency.Code, t.ExchangeRateToBase, t.BaseAmount, t.CreatedAt,
                t.AccountId, t.Account != null ? t.Account.Name : null, t.ReconciledAt != null, t.Notes,
                t.FundingSource, t.SourceReference, t.Counterparty, t.Reason, t.ReversalOfTransactionId,
                db.FinancialTransactions.Where(r => r.ReversalOfTransactionId == t.Id).Select(r => (Guid?)r.Id).FirstOrDefault()))
            .ToListAsync(ct);

        var baseCurrency = await db.GetBaseCurrencyAsync(ct);
        return Ok(new FinancialTransactionListDto(items, total, page, pageSize, moneyIn, moneyOut, baseCurrency.Code, baseCurrency.Symbol));
    }

    /// <summary>FI-D21: posts the opposite entries of a funding, a transfer (both legs together) or an opening balance. The originals stay active.</summary>
    [HttpPost("{id:guid}/reverse")]
    [Authorize(Policy = "CashBank.Reverse")]
    public async Task<IActionResult> Reverse(Guid id, ReverseTransactionRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);

        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
            var ids = await accountService.ReverseAsync(new ReverseTransactionInput(id, request.Reason) { IdempotencyKey = key }, userId, HttpContext.TraceIdentifier, ct);
            return ids is null ? NotFound() : StatusCode(StatusCodes.Status201Created, new ReversalResultDto(ids));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }
}
