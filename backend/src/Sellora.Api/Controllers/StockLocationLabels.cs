using Microsoft.EntityFrameworkCore;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>
/// Per-request id → code/name dictionary of the company's stock locations, for document headers (Phase 8 design §6). Unlike
/// <see cref="StockVisibility"/> it is not narrowed by permission: a document the caller can already read always names its own
/// location. Location FKs are navigation-less, so DTOs resolve the label here instead of through a filtered navigation chain.
/// </summary>
internal sealed class StockLocationLabels
{
    private readonly Dictionary<Guid, (string Code, string Name)> _byId;

    private StockLocationLabels(Dictionary<Guid, (string Code, string Name)> byId) => _byId = byId;

    public static async Task<StockLocationLabels> LoadAsync(SelloraDbContext db, CancellationToken ct)
    {
        var companyId = db.CompanyContext.RequireCompanyId();
        var rows = await db.StockLocations.AsNoTracking()
            .Where(l => l.CompanyId == companyId)
            .Select(l => new { l.Id, l.Code, l.Name })
            .ToListAsync(ct);
        return new StockLocationLabels(rows.ToDictionary(l => l.Id, l => (l.Code, l.Name)));
    }

    public string? Code(Guid id) => _byId.TryGetValue(id, out var l) ? l.Code : null;

    public string? Name(Guid id) => _byId.TryGetValue(id, out var l) ? l.Name : null;
}
