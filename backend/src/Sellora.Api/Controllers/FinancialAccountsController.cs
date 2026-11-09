using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Accounts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/financial-accounts")]
public class FinancialAccountsController(SelloraDbContext db, IFinancialAccountService accountService) : ControllerBase
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
        var balances = await BalancesAsync(null, ct);
        var labels = await BranchLabels.LoadAsync(db, ct);

        var items = accounts.Select(a => ToDto(a, balances.GetValueOrDefault(a.Id), labels)).ToList();
        var baseCurrency = await db.GetBaseCurrencyAsync(ct);
        return Ok(new FinancialAccountListDto(
            items,
            items.Where(a => a.Kind == FinancialAccountKind.Cash).Sum(a => a.Balance),
            items.Where(a => a.Kind == FinancialAccountKind.Bank).Sum(a => a.Balance),
            baseCurrency.Code, baseCurrency.Symbol, baseCurrency.DecimalPrecision));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "CashBank.View")]
    public async Task<ActionResult<FinancialAccountDto>> Get(Guid id, CancellationToken ct)
    {
        var account = await db.FinancialAccounts.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (account is null) return NotFound();

        var balances = await BalancesAsync(id, ct);
        return Ok(ToDto(account, balances.GetValueOrDefault(id), await BranchLabels.LoadAsync(db, ct)));
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
                    request.OpeningDate ?? DateOnly.FromDateTime(DateTime.UtcNow)) { BranchId = request.BranchId },
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
            var account = await accountService.UpdateAsync(id, new UpdateAccountInput(request.Name, request.Reference) { BranchId = request.BranchId }, ct);
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

        try
        {
            await accountService.TransferAsync(
                new TransferInput(request.FromAccountId!.Value, request.ToAccountId!.Value, request.Amount, request.TransferDate, request.Notes),
                CurrentUserId(), ct);
            return NoContent();
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

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
    private async Task<Dictionary<Guid, Balance>> BalancesAsync(Guid? accountId, CancellationToken ct)
    {
        var query = db.FinancialTransactions.Where(t => t.AccountId != null && t.Status == FinancialTransactionStatus.Active);
        if (accountId is not null) query = query.Where(t => t.AccountId == accountId);

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

    private static FinancialAccountDto ToDto(FinancialAccount a, Balance? b, BranchLabels labels) => new(
        a.Id, a.Name, a.Kind, a.Reference, a.IsActive, b?.Book ?? 0m, b?.Reconciled ?? 0m, b?.Unreconciled ?? 0,
        a.BranchId, labels.Code(a.BranchId), labels.Name(a.BranchId));
}
