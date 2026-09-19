using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/expense-categories")]
public class ExpenseCategoriesController(SelloraDbContext db) : ControllerBase
{
    // Anyone who can record or view expenses needs the category list; managing them is a separate permission.
    [HttpGet]
    [Authorize(Policy = "Expenses.View")]
    public async Task<ActionResult<IReadOnlyList<ExpenseCategoryDto>>> List(CancellationToken ct) =>
        Ok(await db.ExpenseCategories.OrderBy(c => c.Name)
            .Select(c => new ExpenseCategoryDto(c.Id, c.Name, c.IsActive)).ToListAsync(ct));

    [HttpPost]
    [Authorize(Policy = "Expenses.Categories")]
    public async Task<ActionResult<ExpenseCategoryDto>> Create(UpsertExpenseCategoryRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var name = request.Name.Trim();
        if (await db.ExpenseCategories.AnyAsync(c => c.Name.ToLower() == name.ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Name), "A category with this name already exists.");
            return ValidationProblem(ModelState);
        }

        var category = new ExpenseCategory { Id = Guid.NewGuid(), Name = name };
        db.ExpenseCategories.Add(category);
        await db.SaveChangesAsync(ct);
        return Ok(new ExpenseCategoryDto(category.Id, category.Name, category.IsActive));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Expenses.Categories")]
    public async Task<ActionResult<ExpenseCategoryDto>> Rename(Guid id, UpsertExpenseCategoryRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var category = await db.ExpenseCategories.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (category is null) return NotFound();

        var name = request.Name.Trim();
        if (await db.ExpenseCategories.AnyAsync(c => c.Id != id && c.Name.ToLower() == name.ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Name), "A category with this name already exists.");
            return ValidationProblem(ModelState);
        }

        category.Name = name;
        await db.SaveChangesAsync(ct);
        return Ok(new ExpenseCategoryDto(category.Id, category.Name, category.IsActive));
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Expenses.Categories")]
    public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        var category = await db.ExpenseCategories.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (category is null) return NotFound();

        category.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
