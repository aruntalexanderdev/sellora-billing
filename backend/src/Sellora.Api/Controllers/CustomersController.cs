using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

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
            .Select(x => new CustomerDto(x.Id, x.Name, x.Phone, x.Address, x.IsWalkIn, x.IsActive,
                x.CountryId, x.CountryId == null ? null : db.Countries.Where(c => c.Id == x.CountryId).Select(c => c.Name).FirstOrDefault(), x.StateId, x.StateId == null ? null : db.States.Where(st => st.Id == x.StateId).Select(st => st.Name).FirstOrDefault()))
            .ToListAsync(ct);

        return Ok(new PagedResult<CustomerDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Customers.View")]
    public async Task<ActionResult<CustomerDto>> Get(Guid id, CancellationToken ct)
    {
        var customer = await db.Customers.SingleOrDefaultAsync(x => x.Id == id, ct);
        return customer is null ? NotFound() : Ok(await ToDtoAsync(customer, ct));
    }

    [HttpPost]
    [Authorize(Policy = "Customers.Create")]
    public async Task<ActionResult<CustomerDto>> Create(UpsertCustomerRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            await GeoLinkValidator.ValidateAsync(db, request.CountryId, request.StateId, null, null, ct);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }

        var customer = new Customer
        {
            Id = Guid.NewGuid(), Name = request.Name.Trim(), Phone = request.Phone, Address = request.Address,
            CountryId = request.CountryId, StateId = request.StateId,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = customer.Id }, await ToDtoAsync(customer, ct));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Customers.Edit")]
    public async Task<ActionResult<CustomerDto>> Update(Guid id, UpsertCustomerRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var customer = await db.Customers.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (customer is null) return NotFound();

        try
        {
            await GeoLinkValidator.ValidateAsync(db, request.CountryId, request.StateId, customer.CountryId, customer.StateId, ct);
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }

        customer.Name = request.Name.Trim();
        customer.Phone = request.Phone;
        customer.Address = request.Address;
        customer.CountryId = request.CountryId; // D17: null clears
        customer.StateId = request.StateId;
        await db.SaveChangesAsync(ct);

        return Ok(await ToDtoAsync(customer, ct));
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

    private async Task<CustomerDto> ToDtoAsync(Customer c, CancellationToken ct)
    {
        var geo = await GeoNames.LoadAsync(db, [c.CountryId], [c.StateId], ct);
        return new(c.Id, c.Name, c.Phone, c.Address, c.IsWalkIn, c.IsActive, c.CountryId, geo.Country(c.CountryId), c.StateId, geo.State(c.StateId));
    }
}
