using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Validation;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Accounts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/financial-accounts")]
public class FinancialAccountsController(SelloraDbContext db, IFinancialAccountService accountService, IEffectivePermissionService effectivePermissions) : ControllerBase
{
    /// <summary>The active accounts a payment can be recorded against. Open to any signed-in user, because
    /// choosing where money went is part of paying or receiving, not of viewing balances.</summary>
    [HttpGet("options")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<AccountOptionDto>>> Options(CancellationToken ct) =>
        Ok(await db.FinancialAccounts.Where(a => a.IsActive).OrderBy(a => a.Kind).ThenBy(a => a.Name)
            .Select(a => new AccountOptionDto(a.Id, a.Name, a.Kind, a.BranchId)).ToListAsync(ct));

    [HttpGet]
    [Authorize(Policy = "CashBank.View")]
    public async Task<ActionResult<FinancialAccountListDto>> List([FromQuery] Guid? branchId, CancellationToken ct)
    {
        if (await this.DenyUnlessAllowedAsync(db, branchId, ct) is { } denied) return denied;

        // branchId = only accounts tagged to that branch; absent = every account (company-level ones included).
        var accountQuery = db.FinancialAccounts.AsQueryable();
        if (branchId is not null) accountQuery = accountQuery.Where(a => a.BranchId == branchId);
        var accounts = await accountQuery.OrderBy(a => a.Kind).ThenBy(a => a.Name).ToListAsync(ct);
        // Aggregate only the accounts the caller can see (branch-filtered above), not the whole company ledger.
        var balances = await BalancesAsync(accounts.Select(a => a.Id).ToList(), ct);
        var labels = await BranchLabels.LoadAsync(db, ct);
        var sharedBalances = await CanSeeSharedBalancesAsync(ct);

        var items = accounts.Select(a => ToDto(a, balances.GetValueOrDefault(a.Id), labels, sharedBalances)).ToList();
        var baseCurrency = await db.GetBaseCurrencyAsync(ct);
        return Ok(new FinancialAccountListDto(
            items,
            items.Where(a => a.Kind == FinancialAccountKind.Cash).Sum(a => a.Balance ?? 0m),
            items.Where(a => a.Kind == FinancialAccountKind.Bank).Sum(a => a.Balance ?? 0m),
            baseCurrency.Code, baseCurrency.Symbol, baseCurrency.DecimalPrecision));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "CashBank.View")]
    public async Task<ActionResult<FinancialAccountDto>> Get(Guid id, CancellationToken ct)
    {
        var account = await db.FinancialAccounts.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (account is null) return NotFound();

        var balances = await BalancesAsync([id], ct);
        return Ok(ToDto(account, balances.GetValueOrDefault(id), await BranchLabels.LoadAsync(db, ct), await CanSeeSharedBalancesAsync(ct)));
    }

    [HttpPost]
    [Authorize(Policy = "CashBank.Manage")]
    public async Task<ActionResult<FinancialAccountDto>> Create(CreateFinancialAccountRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var account = await accountService.CreateAsync(
                new CreateAccountInput(request.Name, request.Kind, request.Reference, request.OpeningBalance,
                    request.OpeningDate ?? DateOnly.FromDateTime(DateTime.UtcNow)) { BranchId = request.BranchId, OverdraftLimit = request.OverdraftLimit },
                CurrentUserId(), ct);
            return await Get(account.Id, ct);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "CashBank.Manage")]
    public async Task<ActionResult<FinancialAccountDto>> Update(Guid id, UpdateFinancialAccountRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var account = await accountService.UpdateAsync(id, new UpdateAccountInput(request.Name, request.Reference) { BranchId = request.BranchId, OverdraftLimit = request.OverdraftLimit }, ct);
            if (account is null) return NotFound();
            return await Get(id, ct);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "CashBank.Manage")]
    public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        var account = await db.FinancialAccounts.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (account is null) return NotFound();

        account.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("transfers")]
    [Authorize(Policy = "CashBank.Transfer")]
    public async Task<IActionResult> Transfer(TransferRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);

        try
        {
            await accountService.TransferAsync(
                new TransferInput(request.FromAccountId!.Value, request.ToAccountId!.Value, request.Amount, request.TransferDate, request.Notes) { IdempotencyKey = key },
                CurrentUserId(), HttpContext.TraceIdentifier, ct);
            return NoContent();
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    /// <summary>FI-D19: money into this account from outside the business's trading (capital, loan, deposit, unallocated customer receipt).</summary>
    [HttpPost("{id:guid}/fundings")]
    [Authorize(Policy = "CashBank.Fund")]
    public async Task<IActionResult> Fund(Guid id, FundingRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);

        try
        {
            var transactionId = await accountService.FundAsync(
                new FundingInput(id, request.Source!.Value, request.Amount, request.FundingDate!.Value, request.SourceReference, request.Counterparty,
                    request.CustomerId, request.Reason, request.Notes) { IdempotencyKey = key },
                CurrentUserId(), HttpContext.TraceIdentifier, ct);
            return StatusCode(StatusCodes.Status201Created, new FundingResultDto(transactionId));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    /// <summary>FI-D22: opening balance, dated entries with running balance, closing balance. Company-level accounts whose figures are hidden (FI-D12) answer 403.</summary>
    [HttpGet("{id:guid}/statement")]
    [Authorize(Policy = "CashBank.View")]
    public async Task<ActionResult<AccountStatementDto>> Statement(
        Guid id, [FromQuery(Name = "from")] DateOnly? dateFrom, [FromQuery(Name = "to")] DateOnly? dateTo, CancellationToken ct)
    {
        var account = await db.FinancialAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
        if (account is null) return NotFound();
        if (account.BranchId is null && !await CanSeeSharedBalancesAsync(ct))
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "You do not have access to this account.",
                extensions: new Dictionary<string, object?> { ["code"] = Sellora.Api.Authorization.CompanyContextMiddleware.BranchAccessDeniedCode });
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = dateFrom ?? new DateOnly(today.Year, today.Month, 1);
        var to = dateTo ?? new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        if (from > to)
        {
            ModelState.AddModelError("from", "The start date cannot be after the end date.");
            return ValidationProblem(ModelState);
        }

        var active = db.FinancialTransactions.Where(t => t.AccountId == id && t.Status == FinancialTransactionStatus.Active);
        var opening = await active.Where(t => t.TransactionDate < from)
            .SumAsync(t => (decimal?)(t.Direction == MoneyDirection.In ? t.BaseAmount : -t.BaseAmount), ct) ?? 0m;

        // Party names are looked up by id (see FinancialTransactionsController.List for why navigation hops are avoided).
        var entries = await active.Where(t => t.TransactionDate >= from && t.TransactionDate <= to)
            .OrderBy(t => t.TransactionDate).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => new
            {
                t.Id, t.TransactionDate, t.Type, t.Direction, t.BaseAmount, t.FundingSource, t.SourceReference, t.Counterparty, t.Reason,
                Reconciled = t.ReconciledAt != null, t.ReversalOfTransactionId,
                Party = t.PurchasePayment != null ? db.Suppliers.Where(x => x.Id == t.PurchasePayment.PurchaseOrder.SupplierId).Select(x => x.Name).FirstOrDefault()
                    : t.SalesPayment != null ? db.Customers.Where(x => x.Id == t.SalesPayment.SalesInvoice.CustomerId).Select(x => x.Name).FirstOrDefault()
                    : t.Expense != null ? t.Expense.Category.Name
                    : t.CustomerId != null ? db.Customers.Where(x => x.Id == t.CustomerId).Select(x => x.Name).FirstOrDefault()
                    : null,
                DocumentNo = t.PurchasePayment != null ? db.PurchaseOrders.Where(o => o.Id == t.PurchasePayment.PurchaseOrderId).Select(o => o.PurchaseNo).FirstOrDefault()
                    : t.SalesPayment != null ? db.SalesInvoices.Where(i => i.Id == t.SalesPayment.SalesInvoiceId).Select(i => i.InvoiceNo).FirstOrDefault()
                    : t.Expense != null ? t.Expense.ExpenseNo : null,
                OtherAccount = t.TransferGroupId != null
                    ? db.FinancialTransactions.Where(o => o.TransferGroupId == t.TransferGroupId && o.Id != t.Id).Select(o => o.Account!.Name).FirstOrDefault()
                    : null,
                ReversedType = t.ReversalOfTransactionId != null
                    ? db.FinancialTransactions.Where(o => o.Id == t.ReversalOfTransactionId).Select(o => (FinancialTransactionType?)o.Type).FirstOrDefault()
                    : null,
            })
            .ToListAsync(ct);

        // Only the originals shown in this statement matter (a reversal may be dated outside the range, so its own date must not be filtered).
        var entryIds = entries.Select(e => e.Id).ToList();
        var reversedBy = (await db.FinancialTransactions
                .Where(r => r.AccountId == id && r.ReversalOfTransactionId != null && entryIds.Contains(r.ReversalOfTransactionId.Value))
                .Select(r => new { Original = r.ReversalOfTransactionId!.Value, r.Id }).ToListAsync(ct))
            .ToDictionary(r => r.Original, r => r.Id);

        var rows = new List<AccountStatementRowDto>(entries.Count);
        var running = opening;
        foreach (var e in entries)
        {
            var moneyIn = e.Direction == MoneyDirection.In ? e.BaseAmount : 0m;
            var moneyOut = e.Direction == MoneyDirection.Out ? e.BaseAmount : 0m;
            running += moneyIn - moneyOut;
            rows.Add(new AccountStatementRowDto(
                e.Id, e.TransactionDate, e.Type, e.FundingSource?.ToString(), Describe(e.Type, e.Direction, e.Party, e.OtherAccount, e.FundingSource, e.Counterparty, e.Reason, e.ReversedType),
                e.DocumentNo ?? e.SourceReference, moneyIn, moneyOut, running, e.Reconciled, e.Type == FinancialTransactionType.Reversal,
                reversedBy.TryGetValue(e.Id, out var reversal) ? reversal : null));
        }

        return Ok(new AccountStatementDto(id, from, to, opening, running, rows.Sum(r => r.MoneyIn), rows.Sum(r => r.MoneyOut), rows));
    }

    private static string Words(string name) => System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();

    private static string Describe(
        FinancialTransactionType type, MoneyDirection direction, string? party, string? otherAccount, FundingSource? source, string? counterparty, string? reason,
        FinancialTransactionType? reversedType) => type switch
    {
        FinancialTransactionType.CustomerPayment => $"Payment from {party}",
        FinancialTransactionType.SupplierPayment => $"Payment to {party}",
        FinancialTransactionType.ExpensePayment => $"Expense: {party}",
        FinancialTransactionType.CustomerRefund => $"Refund to {party}",
        FinancialTransactionType.SupplierRefund => $"Refund from {party}",
        FinancialTransactionType.PaymentReversal => $"Payment reversal: {party}",
        FinancialTransactionType.OpeningBalance => "Opening balance",
        FinancialTransactionType.Transfer => direction == MoneyDirection.Out ? $"Transfer to {otherAccount}" : $"Transfer from {otherAccount}",
        FinancialTransactionType.Funding => "Funding: " + (source is { } s ? Words(s.ToString()) : "") + (counterparty is null ? "" : $" ({counterparty})") + (party is null ? "" : $" - {party}"),
        FinancialTransactionType.Reversal => "Reversal of " + (reversedType is { } r ? Words(r.ToString()) : "entry") + (reason is null ? "" : $": {reason}"),
        _ => type.ToString(),
    };

    [HttpPost("{id:guid}/reconcile")]
    [Authorize(Policy = "CashBank.Reconcile")]
    public Task<IActionResult> Reconcile(Guid id, ReconcileRequest request, CancellationToken ct) =>
        ReconcileCoreAsync(id, request, reconcile: true, ct);

    [HttpPost("{id:guid}/unreconcile")]
    [Authorize(Policy = "CashBank.Reconcile")]
    public Task<IActionResult> Unreconcile(Guid id, ReconcileRequest request, CancellationToken ct) =>
        ReconcileCoreAsync(id, request, reconcile: false, ct);

    private async Task<IActionResult> ReconcileCoreAsync(Guid id, ReconcileRequest request, bool reconcile, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var changed = reconcile
                ? await accountService.ReconcileAsync(id, request.TransactionIds, CurrentUserId(), ct)
                : await accountService.UnreconcileAsync(id, request.TransactionIds, ct);
            return changed is null ? NotFound() : Ok(new { changed });
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private sealed record Balance(decimal Book, decimal Reconciled, int Unreconciled);

    // Balances are derived from the ledger, never stored: active money in less money out per account.
    private async Task<Dictionary<Guid, Balance>> BalancesAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken ct)
    {
        if (accountIds.Count == 0) return [];
        var ids = accountIds.ToList();
        var query = db.FinancialTransactions.Where(t => t.AccountId != null && t.Status == FinancialTransactionStatus.Active && ids.Contains(t.AccountId.Value));

        var rows = await query
            .GroupBy(t => t.AccountId!.Value)
            .Select(g => new
            {
                Id = g.Key,
                Book = g.Sum(t => t.Direction == MoneyDirection.In ? t.BaseAmount : -t.BaseAmount),
                Reconciled = g.Where(t => t.ReconciledAt != null).Sum(t => t.Direction == MoneyDirection.In ? t.BaseAmount : -t.BaseAmount),
                Unreconciled = g.Count(t => t.ReconciledAt == null),
            })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => new Balance(r.Book, r.Reconciled, r.Unreconciled));
    }

    // FI-D12: balances of company-level accounts (BranchId null) are shared money; only a company-level CashBank.View holder whose membership
    // covers every branch sees them (a member limited to some branches sees null even with a company role - their totals would include hidden branches).
    private async Task<bool> CanSeeSharedBalancesAsync(CancellationToken ct) =>
        db.BranchContext.AllowedBranchIds is null
        && (await effectivePermissions.GetCurrentAsync(ct)).PermittedBranches("CashBank.View") is null;

    private static FinancialAccountDto ToDto(FinancialAccount a, Balance? b, BranchLabels labels, bool sharedBalancesVisible) =>
        a.BranchId is null && !sharedBalancesVisible
            ? new(a.Id, a.Name, a.Kind, a.Reference, a.IsActive, null, null, 0, a.BranchId, null, null, a.OverdraftLimit)
            : new(a.Id, a.Name, a.Kind, a.Reference, a.IsActive, b?.Book ?? 0m, b?.Reconciled ?? 0m, b?.Unreconciled ?? 0,
                a.BranchId, labels.Code(a.BranchId), labels.Name(a.BranchId), a.OverdraftLimit);
}
