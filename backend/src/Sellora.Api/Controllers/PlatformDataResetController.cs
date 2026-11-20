using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sellora.Api.Authorization;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Platform;

namespace Sellora.Api.Controllers;

/// <summary>
/// Platform Console "Data reset": returns the business data of the whole database (every company) to a fresh-setup state by running
/// the embedded scripts/sql/dev-reset-fresh-setup.sql. A development and delivery-check tool, off unless
/// <c>Platform:DataResetEnabled</c> is true; a client environment never enables it. Platform session + Platform.Admin only; never
/// returns business data (table names and row counts only). Every reset appends one platform audit row with the actor.
/// </summary>
[ApiController]
[Route("api/v1/platform/data-reset")]
[PlatformEndpoint]
[Authorize(Policy = PlatformPolicies.Admin)]
public class PlatformDataResetController(IDataResetService dataReset, ILogger<PlatformDataResetController> logger) : ControllerBase
{
    /// <summary>Whether the switch is on, so the console can show or hide the page.</summary>
    [HttpGet("status")]
    public ActionResult<DataResetStatusDto> Status() => Ok(new DataResetStatusDto(dataReset.IsEnabled));

    /// <summary>The tables a reset with these options would empty, with their current row counts. Changes nothing.</summary>
    [HttpGet("preview")]
    public async Task<IActionResult> Preview([FromQuery] DataResetOptionsRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(ToDto(await dataReset.PreviewAsync(ToOptions(request), ct)));
        }
        catch (DataResetDisabledException ex)
        {
            return Disabled(ex);
        }
        catch (DataResetRefusedException ex)
        {
            return Refused(ex);
        }
    }

    /// <summary>Runs the reset (all or nothing). The typed database name must be the connected database.</summary>
    [HttpPost]
    public async Task<IActionResult> Reset(DataResetRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var actor)) return Forbid();

        var options = new DataResetOptions(request.WipeCustomers, request.WipeSuppliers, request.KeepTaxes, request.KeepCategories,
            request.KeepExpenseCategories, request.KeepAccounts);
        try
        {
            var report = await dataReset.ResetAsync(options, request.ConfirmDatabase, actor, HttpContext.TraceIdentifier, ct);
            logger.LogWarning("Platform data reset of database {Database} by platform administrator {Actor}; masters kept: {Kept}.",
                report.Database, actor, string.Join(", ", report.KeptMasters));
            return Ok(ToDto(report));
        }
        catch (DomainValidationException ex)
        {
            foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
            return ValidationProblem(ModelState);
        }
        catch (DataResetDisabledException ex)
        {
            return Disabled(ex);
        }
        catch (DataResetRefusedException ex)
        {
            return Refused(ex);
        }
    }

    private static DataResetOptions ToOptions(DataResetOptionsRequest r) =>
        new(r.WipeCustomers, r.WipeSuppliers, r.KeepTaxes, r.KeepCategories, r.KeepExpenseCategories, r.KeepAccounts);

    private static DataResetReportDto ToDto(DataResetReport r) => new(r.Database, r.Applied, r.Message, r.KeptMasters,
        r.Tables.Select(t => new DataResetTableDto(t.Area, t.Table, t.RowsBefore, t.RowsAfter, t.Action)).ToList());

    private ObjectResult Disabled(DataResetDisabledException ex) =>
        Problem(statusCode: 403, title: "Data reset is disabled on this platform.", extensions: new Dictionary<string, object?> { ["code"] = ex.Code });

    private ObjectResult Refused(DataResetRefusedException ex) =>
        Problem(statusCode: 409, title: ex.Message, extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
}
