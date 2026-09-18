using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/customers")]
public class CustomersController(SelloraDbContext db) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Customers.View")]
    public async Task<ActionResult<PagedResult<CustomerDto>>> List(
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Customers.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(s) || (x.Phone != null && x.Phone.Contains(s)));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.IsWalkIn).ThenBy(x => x.Name).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new CustomerDto(x.Id, x.Name, x.Phone, x.Address, x.IsWalkIn, x.IsActive))
            .ToListAsync(ct);

        return Ok(new PagedResult<CustomerDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Customers.View")]
    public async Task<ActionResult<CustomerDto>> Get(Guid id, CancellationToken ct)
    {
        var customer = await db.Customers.SingleOrDefaultAsync(x => x.Id == id, ct);
        return customer is null ? NotFound() : Ok(ToDto(customer));
    }

    [HttpPost]
    [Authorize(Policy = "Customers.Create")]
    public async Task<ActionResult<CustomerDto>> Create(UpsertCustomerRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var customer = new Customer { Id = Guid.NewGuid(), Name = request.Name.Trim(), Phone = request.Phone, Address = request.Address };
        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = customer.Id }, ToDto(customer));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Customers.Edit")]
    public async Task<ActionResult<CustomerDto>> Update(Guid id, UpsertCustomerRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var customer = await db.Customers.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (customer is null) return NotFound();

        customer.Name = request.Name.Trim();
        customer.Phone = request.Phone;
        customer.Address = request.Address;
        await db.SaveChangesAsync(ct);

        return Ok(ToDto(customer));
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Customers.Disable")]
    public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        var customer = await db.Customers.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (customer is null) return NotFound();

        if (customer.IsWalkIn)
        {
            return Problem(statusCode: 400, title: "The walk-in customer cannot be deactivated.");
        }

        customer.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static CustomerDto ToDto(Customer c) => new(c.Id, c.Name, c.Phone, c.Address, c.IsWalkIn, c.IsActive);
}
