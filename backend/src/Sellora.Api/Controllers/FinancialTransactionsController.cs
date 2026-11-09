using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Api.Controllers;

/// <summary>Read-only. Financial transactions are written only as a side effect of the source record
/// (a purchase payment, sales payment or expense) in the same database transaction.</summary>
[ApiController]
[Route("api/v1/financial-transactions")]
public class FinancialTransactionsController(SelloraDbContext db) : ControllerBase
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

        var query = db.FinancialTransactions.AsQueryable();
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
                (t.PurchasePayment != null && (t.PurchasePayment.PurchaseOrder.PurchaseNo.ToLower().Contains(s)
                    || db.Suppliers.Any(x => x.Id == t.PurchasePayment.PurchaseOrder.SupplierId && x.Name.ToLower().Contains(s))))
                || (t.SalesPayment != null && (t.SalesPayment.SalesInvoice.InvoiceNo.ToLower().Contains(s)
                    || db.Customers.Any(x => x.Id == t.SalesPayment.SalesInvoice.CustomerId && x.Name.ToLower().Contains(s))))
                || (t.Expense != null && (t.Expense.ExpenseNo.ToLower().Contains(s) || t.Expense.Category.Name.ToLower().Contains(s))));
        }

        var total = await query.CountAsync(ct);
        var external = query.Where(t => t.Type != FinancialTransactionType.OpeningBalance && t.Type != FinancialTransactionType.Transfer);
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
                    : t.Type == FinancialTransactionType.Transfer
                        ? db.FinancialTransactions.Where(o => o.TransferGroupId == t.TransferGroupId && o.Id != t.Id).Select(o => o.Account!.Name).FirstOrDefault() ?? ""
                        : t.Account!.Name,
                t.PurchasePayment != null ? "Purchase" : t.SalesPayment != null ? "Sale" : t.Expense != null ? "Expense" : null,
                t.PurchasePayment != null ? t.PurchasePayment.PurchaseOrderId
                    : t.SalesPayment != null ? t.SalesPayment.SalesInvoiceId
                    : t.ExpenseId,
                t.PurchasePayment != null ? t.PurchasePayment.PurchaseOrder.PurchaseNo
                    : t.SalesPayment != null ? t.SalesPayment.SalesInvoice.InvoiceNo
                    : t.Expense != null ? t.Expense.ExpenseNo : null,
                t.PaymentMethod, t.Amount, t.Currency.Code, t.ExchangeRateToBase, t.BaseAmount, t.CreatedAt,
                t.AccountId, t.Account != null ? t.Account.Name : null, t.ReconciledAt != null, t.Notes))
            .ToListAsync(ct);

        var baseCurrency = await db.GetBaseCurrencyAsync(ct);
        return Ok(new FinancialTransactionListDto(items, total, page, pageSize, moneyIn, moneyOut, baseCurrency.Code, baseCurrency.Symbol));
    }
}
