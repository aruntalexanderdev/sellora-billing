using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/pricing")]
[Authorize(Policy = "Inventory.Price")]
public class PricingController(SelloraDbContext db, IPricingService pricingService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PricingRowDto>>> List([FromQuery] string? search, CancellationToken ct)
    {
        var query = db.ProductSellingModes
            .Include(m => m.Product).ThenInclude(p => p.BaseUnit)
            .Include(m => m.Product).ThenInclude(p => p.Tax)
            .Where(m => m.IsActive && m.Product.IsActive)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(m => m.Product.Name.ToLower().Contains(s) || m.Product.Sku.ToLower().Contains(s));
        }

        var modes = await query.OrderBy(m => m.Product.Name).ThenBy(m => m.ConversionFactorToBase).ToListAsync(ct);
        var modeIds = modes.Select(m => m.Id).ToList();
        var productIds = modes.Select(m => m.ProductId).Distinct().ToList();

        var currentPrices = await db.SellingPrices
            .Where(p => modeIds.Contains(p.ProductSellingModeId) && p.EffectiveTo == null)
            .ToDictionaryAsync(p => p.ProductSellingModeId, p => p.Price, ct);

        var avgCosts = await db.StockItems
            .Where(s => productIds.Contains(s.ProductId))
            .ToDictionaryAsync(s => s.ProductId, s => s.AverageCost, ct);

        var rows = modes.Select(m =>
        {
            var avgCost = avgCosts.GetValueOrDefault(m.ProductId, 0m);
            var costPerUnit = avgCost * m.ConversionFactorToBase;
            var hasPrice = currentPrices.TryGetValue(m.Id, out var price);
            var margin = hasPrice && price > 0 ? Math.Round((price - costPerUnit) / price * 100, 2) : (decimal?)null;

            return new PricingRowDto(
                m.ProductId, m.Product.Sku, m.Product.Name,
                m.Id, m.Label, m.ConversionFactorToBase, m.Product.BaseUnit.ShortCode,
                costPerUnit, hasPrice ? price : null, margin, m.Product.Tax.Name);
        }).ToList();

        return Ok(rows);
    }

    [HttpGet("{sellingModeId:guid}/history")]
    public async Task<ActionResult<IReadOnlyList<SellingPriceHistoryDto>>> History(Guid sellingModeId, CancellationToken ct)
    {
        var history = await db.SellingPrices
            .Where(p => p.ProductSellingModeId == sellingModeId)
            .OrderByDescending(p => p.EffectiveFrom)
            .Select(p => new SellingPriceHistoryDto(p.Id, p.Price, p.EffectiveFrom, p.EffectiveTo))
            .ToListAsync(ct);

        return Ok(history);
    }

    [HttpPut("{sellingModeId:guid}")]
    public async Task<IActionResult> SetPrice(Guid sellingModeId, SetPriceRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
            await pricingService.SetPriceAsync(sellingModeId, request.Price, userId, ct);
            return NoContent();
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }
}
