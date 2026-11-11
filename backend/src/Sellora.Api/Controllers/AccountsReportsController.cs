using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Api.Controllers;

/// <summary>
/// The Accounts reports. They read the same records the screens do — the purchase and sales payment ledgers,
/// expenses, and the financial transaction ledger — and never keep figures of their own. Everything is presented in
/// the business base currency, using the rate snapshotted on each record, so historical figures never move when
/// exchange rates change.
/// </summary>
[ApiController]
[Route("api/v1/reports/accounts")]
[Authorize(Policy = "Reports.Accounts")]
public class AccountsReportsController(SelloraDbContext db) : ControllerBase
{
    // ------------------------------------------------------------------ payment reports

    [HttpGet("purchase-payments")]
    public async Task<ActionResult<PaymentReportDto>> PurchasePayments(
        [FromQuery] string? search, [FromQuery] PaymentMethod? paymentMethod, [FromQuery] PaymentEntryKind? kind, [FromQuery] Guid? supplierId,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Phase 5: only payments whose purchase order is visible (filter the root before aggregating/projecting).
        var query = db.PurchasePayments.Where(p => db.PurchaseOrders.Any(o => o.Id == p.PurchaseOrderId));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(p => p.PurchaseOrder.PurchaseNo.ToLower().Contains(s) || p.PurchaseOrder.Supplier.Name.ToLower().Contains(s));
        }
        if (paymentMethod is not null) query = query.Where(p => p.PaymentMethod == paymentMethod);
        if (kind is not null) query = query.Where(p => p.Kind == kind);
        if (supplierId is not null) query = query.Where(p => p.PurchaseOrder.SupplierId == supplierId);
        if (dateFrom is not null) query = query.Where(p => p.PaymentDate >= dateFrom);
        if (dateTo is not null) query = query.Where(p => p.PaymentDate <= dateTo);

        var total = await query.CountAsync(ct);
        var sums = await query.GroupBy(p => p.Kind).Select(g => new { Kind = g.Key, Base = g.Sum(p => p.BaseAmount) }).ToListAsync(ct);

        var items = await query
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new PaymentReportRowDto(
                p.Id, p.PaymentDate, p.Kind, p.PurchaseOrder.Supplier.Name, p.PurchaseOrderId, p.PurchaseOrder.PurchaseNo,
                p.PaymentMethod, p.Amount, p.PurchaseOrder.Currency.Code, p.ExchangeRateToBase, p.BaseAmount,
                p.PurchaseOrder.OriginalGrandTotal - p.PurchaseOrder.OriginalReturnedAmount <= 0
                    || p.PurchaseOrder.OriginalPaidAmount >= p.PurchaseOrder.OriginalGrandTotal - p.PurchaseOrder.OriginalReturnedAmount ? PaymentStatus.Paid
                    : p.PurchaseOrder.OriginalPaidAmount <= 0 ? PaymentStatus.Due : PaymentStatus.PartiallyPaid,
                db.FinancialTransactions.Where(t => t.PurchasePaymentId == p.Id).Select(t => t.Account!.Name).FirstOrDefault(),
                p.Reason))
            .ToListAsync(ct);

        return Ok(ToPaymentReport(items, total, page, pageSize, sums.ToDictionary(x => x.Kind, x => x.Base), await BaseCurrencyAsync(ct)));
    }

    [HttpGet("sales-payments")]
    public async Task<ActionResult<PaymentReportDto>> SalesPayments(
        [FromQuery] string? search, [FromQuery] PaymentMethod? paymentMethod, [FromQuery] PaymentEntryKind? kind, [FromQuery] Guid? customerId,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var baseCurrency = await BaseCurrencyAsync(ct);
        var query = db.SalesPayments.Where(p => db.SalesInvoices.Any(i => i.Id == p.SalesInvoiceId));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(p => p.SalesInvoice.InvoiceNo.ToLower().Contains(s) || p.SalesInvoice.Customer.Name.ToLower().Contains(s));
        }
        if (paymentMethod is not null) query = query.Where(p => p.PaymentMethod == paymentMethod);
        if (kind is not null) query = query.Where(p => p.Kind == kind);
        if (customerId is not null) query = query.Where(p => p.SalesInvoice.CustomerId == customerId);
        if (dateFrom is not null) query = query.Where(p => p.PaymentDate >= dateFrom);
        if (dateTo is not null) query = query.Where(p => p.PaymentDate <= dateTo);

        var total = await query.CountAsync(ct);
        var sums = await query.GroupBy(p => p.Kind).Select(g => new { Kind = g.Key, Base = g.Sum(p => p.BaseAmount) }).ToListAsync(ct);

        var items = await query
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new PaymentReportRowDto(
                p.Id, p.PaymentDate, p.Kind, p.SalesInvoice.Customer.Name, p.SalesInvoiceId, p.SalesInvoice.InvoiceNo,
                p.PaymentMethod, p.Amount, baseCurrency.Code, p.ExchangeRateToBase, p.BaseAmount,
                p.SalesInvoice.GrandTotal - p.SalesInvoice.ReturnedAmount <= 0
                    || p.SalesInvoice.PaidAmount >= p.SalesInvoice.GrandTotal - p.SalesInvoice.ReturnedAmount ? PaymentStatus.Paid
                    : p.SalesInvoice.PaidAmount <= 0 ? PaymentStatus.Due : PaymentStatus.PartiallyPaid,
                db.FinancialTransactions.Where(t => t.SalesPaymentId == p.Id).Select(t => t.Account!.Name).FirstOrDefault(),
                p.Reason))
            .ToListAsync(ct);

        return Ok(ToPaymentReport(items, total, page, pageSize, sums.ToDictionary(x => x.Kind, x => x.Base), baseCurrency));
    }

    private static PaymentReportDto ToPaymentReport(
        IReadOnlyList<PaymentReportRowDto> items, int total, int page, int pageSize, Dictionary<PaymentEntryKind, decimal> sums, BaseCurrencyDto baseCurrency)
    {
        var payments = sums.GetValueOrDefault(PaymentEntryKind.Payment);
        var refunds = sums.GetValueOrDefault(PaymentEntryKind.Refund);
        var reversals = sums.GetValueOrDefault(PaymentEntryKind.Reversal);
        return new PaymentReportDto(items, total, page, pageSize, payments, refunds, reversals, payments - refunds - reversals, baseCurrency);
    }

    // ------------------------------------------------------------------ outstanding

    [HttpGet("supplier-outstanding")]
    public async Task<ActionResult<OutstandingReportDto>> SupplierOutstanding(
        [FromQuery] string? search, [FromQuery] bool onlyOutstanding, [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var docs = db.PurchaseOrders.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            docs = docs.Where(p => p.Supplier.Name.ToLower().Contains(s));
        }
        if (dateFrom is not null) docs = docs.Where(p => p.PurchaseDate >= dateFrom);
        if (dateTo is not null) docs = docs.Where(p => p.PurchaseDate <= dateTo);

        // Live documents carry the totals; money paid on a cancelled purchase is held by the supplier, so it only
        // counts toward the credit balance. A purchase is owed nothing once its transaction-currency due is zero, so
        // rounding on a foreign purchase never leaves a stray paisa outstanding.
        var rows = await docs
            .GroupBy(p => new { p.SupplierId, p.Supplier.Name })
            .Select(g => new
            {
                g.Key.SupplierId,
                g.Key.Name,
                Count = g.Count(p => p.Status == PurchaseStatus.Completed),
                Total = g.Where(p => p.Status == PurchaseStatus.Completed).Sum(p => p.GrandTotal),
                Returned = g.Where(p => p.Status == PurchaseStatus.Completed).Sum(p => p.ReturnedAmount),
                Paid = g.Where(p => p.Status == PurchaseStatus.Completed).Sum(p => p.PaidAmount),
                Outstanding = g.Where(p => p.Status == PurchaseStatus.Completed).Sum(p =>
                    p.OriginalGrandTotal - p.OriginalReturnedAmount - p.OriginalPaidAmount <= 0 ? 0m
                    : p.GrandTotal - p.ReturnedAmount - p.PaidAmount < 0m ? 0m : p.GrandTotal - p.ReturnedAmount - p.PaidAmount),
                Credit = g.Sum(p => p.Status == PurchaseStatus.Cancelled ? p.PaidAmount
                    : p.PaidAmount - (p.GrandTotal - p.ReturnedAmount) > 0m ? p.PaidAmount - (p.GrandTotal - p.ReturnedAmount) : 0m),
                Unpaid = g.Count(p => p.Status == PurchaseStatus.Completed && p.OriginalPaidAmount <= 0 && p.OriginalGrandTotal - p.OriginalReturnedAmount > 0),
                Partial = g.Count(p => p.Status == PurchaseStatus.Completed && p.OriginalPaidAmount > 0 && p.OriginalPaidAmount < p.OriginalGrandTotal - p.OriginalReturnedAmount),
            })
            .ToListAsync(ct);

        return Ok(ToOutstandingReport(
            rows.Select(r => new OutstandingRowDto(r.SupplierId, r.Name, r.Count, r.Total, r.Returned, r.Total - r.Returned, r.Paid,
                r.Outstanding, r.Credit, r.Unpaid, r.Partial, r.Count - r.Unpaid - r.Partial)).ToList(),
            onlyOutstanding, page, pageSize, await BaseCurrencyAsync(ct)));
    }

    [HttpGet("customer-outstanding")]
    public async Task<ActionResult<OutstandingReportDto>> CustomerOutstanding(
        [FromQuery] string? search, [FromQuery] bool onlyOutstanding, [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var docs = db.SalesInvoices.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            docs = docs.Where(i => i.Customer.Name.ToLower().Contains(s));
        }
        if (dateFrom is not null) docs = docs.Where(i => i.InvoiceDate >= dateFrom);
        if (dateTo is not null) docs = docs.Where(i => i.InvoiceDate <= dateTo);

        var rows = await docs
            .GroupBy(i => new { i.CustomerId, i.Customer.Name })
            .Select(g => new
            {
                g.Key.CustomerId,
                g.Key.Name,
                Count = g.Count(i => i.Status == SalesStatus.Completed),
                Total = g.Where(i => i.Status == SalesStatus.Completed).Sum(i => i.GrandTotal),
                Returned = g.Where(i => i.Status == SalesStatus.Completed).Sum(i => i.ReturnedAmount),
                Paid = g.Where(i => i.Status == SalesStatus.Completed).Sum(i => i.PaidAmount),
                Outstanding = g.Where(i => i.Status == SalesStatus.Completed).Sum(i =>
                    i.GrandTotal - i.ReturnedAmount - i.PaidAmount < 0m ? 0m : i.GrandTotal - i.ReturnedAmount - i.PaidAmount),
                Credit = g.Sum(i => i.Status == SalesStatus.Cancelled ? i.PaidAmount
                    : i.PaidAmount - (i.GrandTotal - i.ReturnedAmount) > 0m ? i.PaidAmount - (i.GrandTotal - i.ReturnedAmount) : 0m),
                Unpaid = g.Count(i => i.Status == SalesStatus.Completed && i.PaidAmount <= 0 && i.GrandTotal - i.ReturnedAmount > 0),
                Partial = g.Count(i => i.Status == SalesStatus.Completed && i.PaidAmount > 0 && i.PaidAmount < i.GrandTotal - i.ReturnedAmount),
            })
            .ToListAsync(ct);

        return Ok(ToOutstandingReport(
            rows.Select(r => new OutstandingRowDto(r.CustomerId, r.Name, r.Count, r.Total, r.Returned, r.Total - r.Returned, r.Paid,
                r.Outstanding, r.Credit, r.Unpaid, r.Partial, r.Count - r.Unpaid - r.Partial)).ToList(),
            onlyOutstanding, page, pageSize, await BaseCurrencyAsync(ct)));
    }

    // Parties are grouped in the database and paged here: the totals must cover every party, not just the page.
    private static OutstandingReportDto ToOutstandingReport(List<OutstandingRowDto> all, bool onlyOutstanding, int page, int pageSize, BaseCurrencyDto baseCurrency)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var shown = all.Where(r => r.DocumentCount > 0 || r.CreditBalance > 0);
        if (onlyOutstanding) shown = shown.Where(r => r.Outstanding > 0);
        var ordered = shown.OrderByDescending(r => r.Outstanding).ThenBy(r => r.PartyName).ToList();

        return new OutstandingReportDto(
            ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(), ordered.Count, page, pageSize,
            ordered.Sum(r => r.Net), ordered.Sum(r => r.Paid), ordered.Sum(r => r.Outstanding), ordered.Sum(r => r.CreditBalance), baseCurrency);
    }

    // ------------------------------------------------------------------ expenses

    [HttpGet("expenses")]
    public async Task<ActionResult<ExpenseReportDto>> Expenses(
        [FromQuery] string? search, [FromQuery] Guid? categoryId, [FromQuery] PaymentMethod? paymentMethod, [FromQuery] bool includeCancelled,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Expenses.AsQueryable();
        if (!includeCancelled) query = query.Where(e => e.Status == ExpenseStatus.Active);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(e => e.ExpenseNo.ToLower().Contains(s) || e.Category.Name.ToLower().Contains(s)
                || (e.Reference != null && e.Reference.ToLower().Contains(s)));
        }
        if (categoryId is not null) query = query.Where(e => e.CategoryId == categoryId);
        if (paymentMethod is not null) query = query.Where(e => e.PaymentMethod == paymentMethod);
        if (dateFrom is not null) query = query.Where(e => e.ExpenseDate >= dateFrom);
        if (dateTo is not null) query = query.Where(e => e.ExpenseDate <= dateTo);

        var total = await query.CountAsync(ct);
        var byCategory = await query.Where(e => e.Status == ExpenseStatus.Active)
            .GroupBy(e => e.Category.Name)
            .Select(g => new ExpenseCategoryTotalDto(g.Key, g.Count(), g.Sum(e => e.BaseAmount)))
            .ToListAsync(ct);

        var items = await query
            .OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.ExpenseNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new ExpenseReportRowDto(
                e.Id, e.ExpenseNo, e.ExpenseDate, e.Category.Name, e.Reference, e.PaymentMethod,
                e.Amount, e.Currency.Code, e.ExchangeRateToBase, e.BaseAmount, e.Status,
                db.FinancialTransactions.Where(t => t.ExpenseId == e.Id).Select(t => t.Account!.Name).FirstOrDefault()))
            .ToListAsync(ct);

        return Ok(new ExpenseReportDto(items, total, page, pageSize, byCategory.Sum(c => c.BaseTotal),
            byCategory.OrderByDescending(c => c.BaseTotal).ToList(), await BaseCurrencyAsync(ct)));
    }

    // ------------------------------------------------------------------ cash flow

    [HttpGet("cash-flow")]
    public async Task<ActionResult<CashFlowReportDto>> CashFlow([FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo, CancellationToken ct = default)
    {
        // Opening balances are not money movement; everything else is, active only.
        var query = db.FinancialTransactions.VisibleTo(db).Where(t => t.Status == FinancialTransactionStatus.Active && t.Type != FinancialTransactionType.OpeningBalance);
        if (dateFrom is not null) query = query.Where(t => t.TransactionDate >= dateFrom);
        if (dateTo is not null) query = query.Where(t => t.TransactionDate <= dateTo);

        var byType = await query
            .GroupBy(t => new { t.Type, t.Direction })
            .Select(g => new { g.Key.Type, g.Key.Direction, Sum = g.Sum(t => t.BaseAmount) })
            .ToListAsync(ct);
        decimal Sum(FinancialTransactionType type, MoneyDirection direction) =>
            byType.Where(x => x.Type == type && x.Direction == direction).Sum(x => x.Sum);

        var received = Sum(FinancialTransactionType.CustomerPayment, MoneyDirection.In);
        var paid = Sum(FinancialTransactionType.SupplierPayment, MoneyDirection.Out);
        var expenses = Sum(FinancialTransactionType.ExpensePayment, MoneyDirection.Out);
        var customerRefunds = Sum(FinancialTransactionType.CustomerRefund, MoneyDirection.Out);
        var supplierRefunds = Sum(FinancialTransactionType.SupplierRefund, MoneyDirection.In);
        var reversalsIn = Sum(FinancialTransactionType.PaymentReversal, MoneyDirection.In);
        var reversalsOut = Sum(FinancialTransactionType.PaymentReversal, MoneyDirection.Out);
        var transfersIn = Sum(FinancialTransactionType.Transfer, MoneyDirection.In);
        var transfersOut = Sum(FinancialTransactionType.Transfer, MoneyDirection.Out);

        var moneyIn = received + supplierRefunds + reversalsIn;
        var moneyOut = paid + expenses + customerRefunds + reversalsOut;

        var months = await query.Where(t => t.Type != FinancialTransactionType.Transfer)
            .GroupBy(t => new { t.TransactionDate.Year, t.TransactionDate.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                In = g.Where(t => t.Direction == MoneyDirection.In).Sum(t => t.BaseAmount),
                Out = g.Where(t => t.Direction == MoneyDirection.Out).Sum(t => t.BaseAmount),
                Received = g.Where(t => t.Type == FinancialTransactionType.CustomerPayment).Sum(t => t.BaseAmount),
                Paid = g.Where(t => t.Type == FinancialTransactionType.SupplierPayment).Sum(t => t.BaseAmount),
                Expenses = g.Where(t => t.Type == FinancialTransactionType.ExpensePayment).Sum(t => t.BaseAmount),
                CustomerRefunds = g.Where(t => t.Type == FinancialTransactionType.CustomerRefund).Sum(t => t.BaseAmount),
                SupplierRefunds = g.Where(t => t.Type == FinancialTransactionType.SupplierRefund).Sum(t => t.BaseAmount),
            })
            .OrderBy(m => m.Year).ThenBy(m => m.Month)
            .ToListAsync(ct);

        return Ok(new CashFlowReportDto(
            dateFrom, dateTo, received, paid, expenses, customerRefunds, supplierRefunds, customerRefunds - supplierRefunds,
            reversalsIn, reversalsOut, transfersIn, transfersOut, moneyIn, moneyOut, moneyIn - moneyOut,
            months.Select(m => new CashFlowPeriodDto($"{m.Year:D4}-{m.Month:D2}", m.In, m.Out, m.In - m.Out,
                m.Received, m.Paid, m.Expenses, m.CustomerRefunds - m.SupplierRefunds)).ToList(),
            await BaseCurrencyAsync(ct)));
    }

    // ------------------------------------------------------------------ cash & bank

    [HttpGet("cash-bank")]
    public async Task<ActionResult<CashBankReportDto>> CashBank([FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo, CancellationToken ct = default)
    {
        // Balances as at the end of the period: everything dated on or before it.
        var query = db.FinancialTransactions.Where(t => t.AccountId != null && t.Status == FinancialTransactionStatus.Active);
        if (dateTo is not null) query = query.Where(t => t.TransactionDate <= dateTo);

        var balances = await query
            .GroupBy(t => t.AccountId!.Value)
            .Select(g => new
            {
                AccountId = g.Key,
                // The balance before the period starts, plus an opening balance entered during it.
                Before = dateFrom == null ? 0m : g.Where(t => t.TransactionDate < dateFrom).Sum(t => t.Direction == MoneyDirection.In ? t.BaseAmount : -t.BaseAmount),
                OpeningInPeriod = g.Where(t => t.Type == FinancialTransactionType.OpeningBalance && (dateFrom == null || t.TransactionDate >= dateFrom)).Sum(t => t.BaseAmount),
                In = g.Where(t => (dateFrom == null || t.TransactionDate >= dateFrom) && t.Direction == MoneyDirection.In
                    && t.Type != FinancialTransactionType.OpeningBalance && t.Type != FinancialTransactionType.Transfer).Sum(t => t.BaseAmount),
                Out = g.Where(t => (dateFrom == null || t.TransactionDate >= dateFrom) && t.Direction == MoneyDirection.Out
                    && t.Type != FinancialTransactionType.OpeningBalance && t.Type != FinancialTransactionType.Transfer).Sum(t => t.BaseAmount),
                TransfersIn = g.Where(t => (dateFrom == null || t.TransactionDate >= dateFrom) && t.Type == FinancialTransactionType.Transfer && t.Direction == MoneyDirection.In).Sum(t => t.BaseAmount),
                TransfersOut = g.Where(t => (dateFrom == null || t.TransactionDate >= dateFrom) && t.Type == FinancialTransactionType.Transfer && t.Direction == MoneyDirection.Out).Sum(t => t.BaseAmount),
                Closing = g.Sum(t => t.Direction == MoneyDirection.In ? t.BaseAmount : -t.BaseAmount),
                Unreconciled = g.Count(t => t.ReconciledAt == null),
            })
            .ToDictionaryAsync(b => b.AccountId, ct);

        var accounts = await db.FinancialAccounts.OrderBy(a => a.Kind).ThenBy(a => a.Name).ToListAsync(ct);
        var rows = accounts
            // An inactive account with no balance and no activity in the period is noise.
            .Where(a => a.IsActive || balances.ContainsKey(a.Id))
            .Select(a =>
            {
                var b = balances.GetValueOrDefault(a.Id);
                return new CashBankReportRowDto(a.Id, a.Name, a.Kind, a.IsActive,
                    (b?.Before ?? 0m) + (b?.OpeningInPeriod ?? 0m), b?.In ?? 0m, b?.Out ?? 0m, b?.TransfersIn ?? 0m, b?.TransfersOut ?? 0m,
                    b?.Closing ?? 0m, b?.Unreconciled ?? 0);
            })
            .Where(r => r.IsActive || r.Closing != 0 || r.Opening != 0 || r.MoneyIn != 0 || r.MoneyOut != 0 || r.TransfersIn != 0 || r.TransfersOut != 0)
            .ToList();

        return Ok(new CashBankReportDto(dateFrom, dateTo, rows,
            rows.Sum(r => r.Opening), rows.Sum(r => r.MoneyIn), rows.Sum(r => r.MoneyOut),
            rows.Sum(r => r.TransfersIn), rows.Sum(r => r.TransfersOut), rows.Sum(r => r.Closing), await BaseCurrencyAsync(ct)));
    }

    // ------------------------------------------------------------------ payment methods

    [HttpGet("payment-methods")]
    public async Task<ActionResult<PaymentMethodSummaryDto>> PaymentMethods([FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo, CancellationToken ct = default)
    {
        // Only movements that came from a payment, refund or expense have a real method; opening balances and
        // transfers between accounts do not.
        var query = db.FinancialTransactions.VisibleTo(db).Where(t => t.Status == FinancialTransactionStatus.Active
            && t.Type != FinancialTransactionType.OpeningBalance && t.Type != FinancialTransactionType.Transfer);
        if (dateFrom is not null) query = query.Where(t => t.TransactionDate >= dateFrom);
        if (dateTo is not null) query = query.Where(t => t.TransactionDate <= dateTo);

        var rows = await query.GroupBy(t => t.PaymentMethod)
            .Select(g => new
            {
                Method = g.Key,
                Count = g.Count(),
                Received = g.Where(t => t.Direction == MoneyDirection.In).Sum(t => t.BaseAmount),
                Paid = g.Where(t => t.Direction == MoneyDirection.Out).Sum(t => t.BaseAmount),
            })
            .ToListAsync(ct);

        var items = rows.OrderBy(r => r.Method).Select(r => new PaymentMethodSummaryRowDto(r.Method, r.Count, r.Received, r.Paid, r.Received - r.Paid)).ToList();
        return Ok(new PaymentMethodSummaryDto(dateFrom, dateTo, items,
            items.Sum(r => r.Received), items.Sum(r => r.Paid), items.Sum(r => r.Net), await BaseCurrencyAsync(ct)));
    }

    private async Task<BaseCurrencyDto> BaseCurrencyAsync(CancellationToken ct)
    {
        var c = await db.GetBaseCurrencyAsync(ct);
        return new BaseCurrencyDto(c.Code, c.Symbol, c.DecimalPrecision);
    }
}
