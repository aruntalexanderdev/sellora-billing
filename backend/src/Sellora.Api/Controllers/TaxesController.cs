using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>
/// Deprecated (TP-1, design deviation g): the legacy tax rows are read-only mirrors of the tax profiles. Create and edit taxes through
/// <c>/api/v1/tax-profiles</c>; this list stays for one release for clients that have not moved yet and is removed with the destructive release M3.
/// </summary>
[ApiController]
[Route("api/v1/taxes")]
[Authorize(Policy = "Masters.View")]
public class TaxesController(SelloraDbContext db) : ControllerBase
{
    [HttpGet]
    [Obsolete("Read-only mirror of the tax profiles; use /api/v1/tax-profiles.")]
    public async Task<ActionResult<IReadOnlyList<TaxDto>>> List(CancellationToken ct)
    {
        Response.Headers["Deprecation"] = "true";
        var taxes = await db.Taxes.OrderBy(t => t.RatePercent)
            .Select(t => new TaxDto(t.Id, t.Name, t.RatePercent, t.IsActive))
            .ToListAsync(ct);
        return Ok(taxes);
    }
}
