using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Expenses;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/expenses")]
public class ExpensesController(SelloraDbContext db, IExpenseService expenseService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Expenses.View")]
    public async Task<ActionResult<PagedResult<ExpenseListItemDto>>> List(
        [FromQuery] string? search, [FromQuery] Guid? categoryId, [FromQuery] ExpenseStatus? status,
        [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Expenses.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(e => e.ExpenseNo.ToLower().Contains(s) || e.Category.Name.ToLower().Contains(s)
                || (e.Reference != null && e.Reference.ToLower().Contains(s)));
        }
        if (categoryId is not null) query = query.Where(e => e.CategoryId == categoryId);
        if (status is not null) query = query.Where(e => e.Status == status);
        if (dateFrom is not null) query = query.Where(e => e.ExpenseDate >= dateFrom);
        if (dateTo is not null) query = query.Where(e => e.ExpenseDate <= dateTo);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.ExpenseNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new ExpenseListItemDto(
                e.Id, e.ExpenseNo, e.ExpenseDate, e.Category.Name, e.Reference,
                e.PaymentMethod, e.Amount, e.Currency.Code, e.BaseAmount, e.Status))
            .ToListAsync(ct);

        return Ok(new PagedResult<ExpenseListItemDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Expenses.View")]
    public async Task<ActionResult<ExpenseDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var expense = await db.Expenses
            .Include(e => e.Category).Include(e => e.Currency)
            .Include(e => e.CreatedByUser).Include(e => e.CancelledByUser)
            .SingleOrDefaultAsync(e => e.Id == id, ct);
        if (expense is null) return NotFound();

        var baseCurrency = await db.BusinessSettings.Select(s => s.BaseCurrency).SingleAsync(ct);
        return Ok(ToDetail(expense, baseCurrency));
    }

    [HttpPost]
    [Authorize(Policy = "Expenses.Create")]
    public async Task<ActionResult<ExpenseDetailDto>> Create(CreateExpenseRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var expense = await expenseService.CreateAsync(
                new CreateExpenseInput(request.CategoryId!.Value, request.ExpenseDate, request.CurrencyId!.Value,
                    request.Amount, request.PaymentMethod, request.Reference, request.Notes, request.AccountId),
                CurrentUserId(), ct);
            return await Get(expense.Id, ct);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Expenses.Edit")]
    public async Task<ActionResult<ExpenseDetailDto>> Update(Guid id, UpdateExpenseRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var expense = await expenseService.UpdateAsync(id, new UpdateExpenseInput(request.CategoryId!.Value, request.Reference, request.Notes), ct);
            if (expense is null) return NotFound();
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
    [Authorize(Policy = "Expenses.Cancel")]
    public async Task<ActionResult<ExpenseDetailDto>> Cancel(Guid id, CancelExpenseRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var expense = await expenseService.CancelAsync(id, request.Reason, CurrentUserId(), ct);
            if (expense is null) return NotFound();
            return await Get(id, ct);
        }
        catch (DomainValidationException ex)
        {
            return Problem(statusCode: 400, title: ex.Message);
        }
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private static ExpenseDetailDto ToDetail(Expense e, Currency baseCurrency) => new(
        e.Id, e.ExpenseNo, e.ExpenseDate, e.CategoryId, e.Category.Name,
        e.CurrencyId, e.Currency.Code, e.Currency.Symbol, e.Currency.DecimalPrecision,
        e.Amount, e.ExchangeRateToBase, e.BaseAmount,
        baseCurrency.Code, baseCurrency.Symbol, baseCurrency.DecimalPrecision,
        e.PaymentMethod, e.Reference, e.Notes, e.Status,
        $"{e.CreatedByUser.FirstName} {e.CreatedByUser.LastName}", e.CreatedAt, e.UpdatedAt,
        e.CancelledAt, e.CancelledByUser is null ? null : $"{e.CancelledByUser.FirstName} {e.CancelledByUser.LastName}", e.CancellationReason);
}
