using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>Phase 7: the active sellable (and base) units of the given products, largest factor first, for the client-side stock breakdown.</summary>
internal static class BreakdownUnits
{
    public static async Task<Dictionary<Guid, IReadOnlyList<BreakdownUnitDto>>> LoadAsync(
        SelloraDbContext db, IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        if (productIds.Count == 0) return [];

        var rows = await db.ProductUnits.AsNoTracking()
            .Where(u => productIds.Contains(u.ProductId) && u.IsActive && (u.IsSellable || u.IsBaseUnit) && u.ConversionFactorToBase > 0)
            .OrderByDescending(u => u.ConversionFactorToBase).ThenBy(u => u.SortOrder)
            .Select(u => new
            {
                u.ProductId, u.Id, UnitCode = u.Unit.ShortCode, Label = u.Label ?? u.Unit.Name,
                u.ConversionFactorToBase, u.IsBaseUnit,
            })
            .ToListAsync(ct);

        return rows.GroupBy(r => r.ProductId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<BreakdownUnitDto>)g
                .Select(r => new BreakdownUnitDto(r.Id, r.UnitCode, r.Label, r.ConversionFactorToBase, r.IsBaseUnit)).ToList());
    }
}
