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
public class PricingController(SelloraDbContext db, IPricingService pricingService, IEffectivePermissionService effectivePermissions) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PricingRowDto>>> List([FromQuery] string? search, CancellationToken ct)
    {
        var query = db.ProductUnits
            .Include(u => u.Unit)
            .Include(u => u.Product).ThenInclude(p => p.BaseUnit)
            .Include(u => u.Product).ThenInclude(p => p.Tax)
            .Where(u => u.IsActive && u.IsSellable && u.Product.IsActive)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(u => u.Product.Name.ToLower().Contains(s) || u.Product.Sku.ToLower().Contains(s));
        }

        var units = await query.OrderBy(u => u.Product.Name).ThenBy(u => u.ProductId).ThenBy(u => u.SortOrder).ToListAsync(ct);
        var unitIds = units.Select(u => u.Id).ToList();
        var productIds = units.Select(u => u.ProductId).Distinct().ToList();

        var currentPrices = await db.SellingPrices
            .Where(p => unitIds.Contains(p.ProductUnitId) && p.EffectiveTo == null)
            .ToDictionaryAsync(p => p.ProductUnitId, p => p.Price, ct);

        // Phase 8: company weighted average over the per-location rows: sum(qty x avg) / sum(qty); with nothing on hand, the highest stored average.
        // Phase 01 (G4): cost is derived only from the locations the caller may see (branch access + Inventory.View scope + company-level locations).
        var visibleLocations = (await StockVisibility.LoadAsync(db, effectivePermissions, "Inventory.View", ct)).Ids.ToList();
        var stockRows = await db.StockItems
            .Where(s => productIds.Contains(s.ProductId) && visibleLocations.Contains(s.StockLocationId))
            .Select(s => new { s.ProductId, s.QuantityOnHand, s.AverageCost })
            .ToListAsync(ct);
        var avgCosts = stockRows.GroupBy(s => s.ProductId).ToDictionary(
            g => g.Key,
            g => StockVisibility.WeightedAverage(g.Sum(s => s.QuantityOnHand), g.Sum(s => s.QuantityOnHand * s.AverageCost), g.Max(s => s.AverageCost)));

        // D2: a per-base-unit anchor price per product — the base row's price, otherwise the smallest-factor priced
        // row's price divided by its factor. Used only as a hint; nothing is priced automatically.
        var anchorPerBase = units
            .Where(u => currentPrices.ContainsKey(u.Id) && u.ConversionFactorToBase > 0)
            .GroupBy(u => u.ProductId)
            .ToDictionary(g => g.Key, g =>
            {
                var anchor = g.OrderByDescending(u => u.IsBaseUnit).ThenBy(u => u.ConversionFactorToBase).ThenBy(u => u.SortOrder).First();
                return currentPrices[anchor.Id] / anchor.ConversionFactorToBase;
            });

        var rows = units.Select(u =>
        {
            var avgCost = avgCosts.GetValueOrDefault(u.ProductId, 0m);
            var costPerUnit = avgCost * u.ConversionFactorToBase;
            var hasPrice = currentPrices.TryGetValue(u.Id, out var price);
            var margin = hasPrice && price > 0 ? Math.Round((price - costPerUnit) / price * 100, 2) : (decimal?)null;
            decimal? suggested = anchorPerBase.TryGetValue(u.ProductId, out var perBase)
                ? Math.Round(perBase * u.ConversionFactorToBase, 2, MidpointRounding.AwayFromZero)
                : null;

            return new PricingRowDto(
                u.ProductId, u.Product.Sku, u.Product.Name,
                u.Id, u.Unit.ShortCode, u.Label ?? u.Unit.Name,
                u.ConversionFactorToBase, u.Product.BaseUnit.ShortCode, u.SortOrder, u.IsBaseUnit,
                costPerUnit, hasPrice ? price : null, suggested, margin, u.Product.Tax.Name);
        }).ToList();

        return Ok(rows);
    }

    [HttpGet("{productUnitId:guid}/history")]
    public async Task<ActionResult<IReadOnlyList<SellingPriceHistoryDto>>> History(Guid productUnitId, CancellationToken ct)
    {
        var history = await db.SellingPrices
            .Where(p => p.ProductUnitId == productUnitId)
            .OrderByDescending(p => p.EffectiveFrom)
            .Select(p => new SellingPriceHistoryDto(p.Id, p.Price, p.EffectiveFrom, p.EffectiveTo))
            .ToListAsync(ct);

        return Ok(history);
    }

    [HttpPut("{productUnitId:guid}")]
    public async Task<IActionResult> SetPrice(Guid productUnitId, SetPriceRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
            await pricingService.SetPriceAsync(productUnitId, request.Price, userId, ct);
            return NoContent();
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }
}
