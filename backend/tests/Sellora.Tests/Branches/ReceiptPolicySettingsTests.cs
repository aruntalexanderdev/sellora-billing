using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Branches;

/// <summary>Phase 06 (FI-D39): the receipt policy is read and written through the existing settings endpoint; a change is audited, an omitted field leaves it unchanged.</summary>
public class ReceiptPolicySettingsTests
{
    [Fact]
    public async Task SettingsPut_ChangesTheReceiptPolicy_Audits_AndOmittedFieldsStayUnchanged()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        db.Companies.Add(new Company { Id = Sellora.Domain.Companies.DefaultTenant.CompanyId, Code = "DEF", Name = "Sellora Mart" });
        await db.SaveChangesAsync();
        var controller = new SettingsController(db, new BranchService(db, new StockLocationUsageService(db)))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, w.UserId.ToString())], "test")) },
            },
        };

        UpdateBusinessSettingsRequest Request(ReceiptPostingMode? mode, bool? separation) =>
            new("Sellora Mart", null, null, null, null, null, null, "INV", w.CurrencyId, mode, separation);

        var settings = await db.BusinessSettings.SingleAsync();
        var prefix = settings.InvoicePrefix;
        BusinessSettingsDto Dto(IActionResult? r) => Assert.IsType<BusinessSettingsDto>(Assert.IsType<OkObjectResult>(r).Value);
        UpdateBusinessSettingsRequest WithPrefix(UpdateBusinessSettingsRequest r) => r with { InvoicePrefix = prefix };

        Assert.Equal((ReceiptPostingMode.Direct, false), (Dto((await controller.Get(CancellationToken.None)).Result).ReceiptPostingMode, Dto((await controller.Get(CancellationToken.None)).Result).ReceiptSeparationOfDuties));

        var changed = Dto((await controller.Update(WithPrefix(Request(ReceiptPostingMode.SupervisorApproval, true)), CancellationToken.None)).Result);
        Assert.Equal((ReceiptPostingMode.SupervisorApproval, true), (changed.ReceiptPostingMode, changed.ReceiptSeparationOfDuties));
        var audit = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.ReceiptPolicyChanged);
        Assert.Contains("Direct", audit.Before);
        Assert.Contains("SupervisorApproval", audit.After);
        Assert.Equal(w.UserId, audit.ActorUserId);

        var unchanged = Dto((await controller.Update(WithPrefix(Request(null, null)), CancellationToken.None)).Result);
        Assert.Equal((ReceiptPostingMode.SupervisorApproval, true), (unchanged.ReceiptPostingMode, unchanged.ReceiptSeparationOfDuties));
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.ReceiptPolicyChanged)); // no change, no audit

        var invalid = await controller.Update(WithPrefix(Request((ReceiptPostingMode)99, null)), CancellationToken.None);
        Assert.IsType<ObjectResult>(invalid.Result);
        Assert.False(controller.ModelState.IsValid);
    }
}
