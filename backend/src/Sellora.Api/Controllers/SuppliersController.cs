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
[Route("api/v1/suppliers")]
public class SuppliersController(SelloraDbContext db) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "Suppliers.View")]
    public async Task<ActionResult<PagedResult<SupplierDto>>> List(
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Suppliers.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(s) || (x.City != null && x.City.ToLower().Contains(s)));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.Name).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new SupplierDto(x.Id, x.Name, x.City, x.Gstin, x.PaymentTerms, x.IsActive, x.CurrencyId, x.Currency.Code, x.Currency.Symbol, x.Currency.DecimalPrecision,
                x.CountryId, x.CountryId == null ? null : db.Countries.Where(c => c.Id == x.CountryId).Select(c => c.Name).FirstOrDefault(), x.StateId, x.StateId == null ? null : db.States.Where(st => st.Id == x.StateId).Select(st => st.Name).FirstOrDefault()))
            .ToListAsync(ct);

        return Ok(new PagedResult<SupplierDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Suppliers.View")]
    public async Task<ActionResult<SupplierDto>> Get(Guid id, CancellationToken ct)
    {
        var supplier = await db.Suppliers.Include(x => x.Currency).SingleOrDefaultAsync(x => x.Id == id, ct);
        return supplier is null ? NotFound() : Ok(SupplierMapping.ToDto(supplier, await GeoOf(supplier, ct)));
    }

    [HttpPost]
    [Authorize(Policy = "Suppliers.Create")]
    public async Task<ActionResult<SupplierDto>> Create(UpsertSupplierRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        if (await db.Suppliers.AnyAsync(x => x.Name.ToLower() == request.Name.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Name), "A supplier with this name already exists.");
            return ValidationProblem(ModelState);
        }

        if (!await ValidateCurrencyAsync(request.CurrencyId!.Value, currentCurrencyId: null, ct)) return ValidationProblem(ModelState);
        if (!await ValidateGeoAsync(request.CountryId, request.StateId, null, null, ct)) return ValidationProblem(ModelState);

        var supplier = new Supplier
        {
            Id = Guid.NewGuid(), Name = request.Name.Trim(), City = request.City, Gstin = request.Gstin,
            PaymentTerms = request.PaymentTerms, CurrencyId = request.CurrencyId!.Value,
            CountryId = request.CountryId, StateId = request.StateId,
        };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync(ct);
        await db.Entry(supplier).Reference(x => x.Currency).LoadAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = supplier.Id }, SupplierMapping.ToDto(supplier, await GeoOf(supplier, ct)));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Suppliers.Edit")]
    public async Task<ActionResult<SupplierDto>> Update(Guid id, UpsertSupplierRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var supplier = await db.Suppliers.Include(x => x.Currency).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (supplier is null) return NotFound();

        if (await db.Suppliers.AnyAsync(x => x.Id != id && x.Name.ToLower() == request.Name.Trim().ToLower(), ct))
        {
            ModelState.AddModelError(nameof(request.Name), "A supplier with this name already exists.");
            return ValidationProblem(ModelState);
        }

        if (!await ValidateCurrencyAsync(request.CurrencyId!.Value, supplier.CurrencyId, ct)) return ValidationProblem(ModelState);
        if (!await ValidateGeoAsync(request.CountryId, request.StateId, supplier.CountryId, supplier.StateId, ct)) return ValidationProblem(ModelState);

        supplier.Name = request.Name.Trim();
        supplier.City = request.City;
        supplier.Gstin = request.Gstin;
        supplier.PaymentTerms = request.PaymentTerms;
        supplier.CurrencyId = request.CurrencyId!.Value;
        supplier.CountryId = request.CountryId; // D17: null clears
        supplier.StateId = request.StateId;
        await db.SaveChangesAsync(ct);
        await db.Entry(supplier).Reference(x => x.Currency).LoadAsync(ct);

        return Ok(SupplierMapping.ToDto(supplier, await GeoOf(supplier, ct)));
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Suppliers.Disable")]
    public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        var supplier = await db.Suppliers.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (supplier is null) return NotFound();

        supplier.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private Task<GeoNames> GeoOf(Supplier s, CancellationToken ct) => GeoNames.LoadAsync(db, [s.CountryId], [s.StateId], ct);

    private async Task<bool> ValidateGeoAsync(Guid? countryId, Guid? stateId, Guid? currentCountryId, Guid? currentStateId, CancellationToken ct)
    {
        try
        {
            await GeoLinkValidator.ValidateAsync(db, countryId, stateId, currentCountryId, currentStateId, ct);
            return true;
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return false;
        }
    }

    // A currency must exist and be active to be newly assigned; a supplier that already uses a since-
    // deactivated currency can still be edited without being forced to switch.
    private async Task<bool> ValidateCurrencyAsync(Guid currencyId, Guid? currentCurrencyId, CancellationToken ct)
    {
        var currency = await db.Currencies.SingleOrDefaultAsync(c => c.Id == currencyId, ct);
        if (currency is null)
        {
            ModelState.AddModelError(nameof(UpsertSupplierRequest.CurrencyId), "Select a valid currency.");
            return false;
        }
        if (!currency.IsActive && currency.Id != currentCurrencyId)
        {
            ModelState.AddModelError(nameof(UpsertSupplierRequest.CurrencyId), "This currency is inactive.");
            return false;
        }
        return true;
    }
}
