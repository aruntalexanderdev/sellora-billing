using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Purchasing;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.Branches;

/// <summary>Phase 2 review fixes: derived-shape checks on branch edit, precise history collisions, settings PUT only validates a changed prefix,
/// purchase header edits respect branch access.</summary>
public class BranchNumberingReviewFixTests
{
    private static CreateBranchInput New(string code, string? prefix = null) =>
        new(code, code + " branch", null, null, null, null, null, null, prefix);

    private static UpdateBranchInput Edit(string name, string? prefix = null) =>
        new(name, null, null, null, null, null, null, prefix);

    private static async Task<string> FieldOfAsync(Func<Task> action) =>
        (await Assert.ThrowsAsync<DomainValidationException>(action)).Field;

    private static async Task SetInvoiceNoAsync(BranchWorld w, Guid? branch, string invoiceNo)
    {
        var sale = await w.SaleAsync(branch);
        await using var db = w.Db();
        var row = await db.SalesInvoices.SingleAsync(i => i.Id == sale.Id);
        row.InvoiceNo = invoiceNo;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task UpdateAsync_ClearingPrefixIntoDerivedShapeOfAnotherBranchOwnPrefix_IsRejected()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        var service = new BranchService(db);
        await service.UpdateAsync(w.Blr, Edit("Bengaluru", "INV-KAR"), w.UserId);
        var kar = await service.CreateAsync(New("KAR", "KRR"), w.UserId);

        Assert.Equal("InvoicePrefix", await FieldOfAsync(() => service.UpdateAsync(kar.Id, Edit("KAR branch"), w.UserId)));
    }

    [Fact]
    public async Task UpdateAsync_DerivedShapeTooLong_IsRejectedButOwnPrefixIsFine()
    {
        var w = await BranchWorld.CreateAsync(companyPrefix: "ABCDEFGHIJ");
        await using var db = w.Db();
        var service = new BranchService(db);
        var kar = await service.CreateAsync(New("KAR", "KRR"), w.UserId);

        Assert.Equal("InvoicePrefix", await FieldOfAsync(() => service.UpdateAsync(kar.Id, Edit("KAR branch"), w.UserId)));
        Assert.Equal("KRR", (await service.UpdateAsync(kar.Id, Edit("KAR branch", "KRR"), w.UserId))!.InvoicePrefix);
    }

    [Fact]
    public async Task UpdateAsync_ClearingPrefixIntoDerivedShapeAlreadyUsedByAnotherBranch_IsRejected()
    {
        var w = await BranchWorld.CreateAsync();
        var kar = await new BranchService(w.Db()).CreateAsync(New("KAR", "KRR"), w.UserId);
        await SetInvoiceNoAsync(w, w.Main, "INV-KAR-0001");
        await using var db = w.Db();

        Assert.Equal("InvoicePrefix", await FieldOfAsync(() => new BranchService(db).UpdateAsync(kar.Id, Edit("KAR branch"), w.UserId)));
    }

    [Theory]
    [InlineData("X-0001", true)]      // default-branch shape {prefix}-{n} at another branch
    [InlineData("X-BLR-0001", false)] // Blr's derived shape produced by Main
    [InlineData("X-BLR-0001", true)]  // ... and by Blr itself: not a collision
    [InlineData("X-KAR-0001", true)]  // a code that exists nowhere: no shape of the new prefix
    public async Task EnsureCompanyInvoicePrefixAllowedAsync_HistoricalNumbers_AreMatchedByShape(string invoiceNo, bool atBlr)
    {
        var w = await BranchWorld.CreateAsync();
        await SetInvoiceNoAsync(w, atBlr ? w.Blr : w.Main, invoiceNo);
        await using var db = w.Db();
        var service = new BranchService(db);

        var rejected = invoiceNo == "X-0001" || (invoiceNo == "X-BLR-0001" && !atBlr);
        if (rejected)
        {
            Assert.Equal("InvoicePrefix", await FieldOfAsync(() => service.EnsureCompanyInvoicePrefixAllowedAsync("X")));
        }
        else
        {
            await service.EnsureCompanyInvoicePrefixAllowedAsync("X");
        }
    }

    [Fact]
    public async Task EnsureCompanyInvoicePrefixAllowedAsync_DefaultBranchOwnHistory_IsNotACollision()
    {
        var w = await BranchWorld.CreateAsync();
        await SetInvoiceNoAsync(w, w.Main, "X-0001");
        await using var db = w.Db();

        await new BranchService(db).EnsureCompanyInvoicePrefixAllowedAsync("X");
    }

    [Fact]
    public async Task EnsureCompanyInvoicePrefixAllowedAsync_NumbersOfALongerShape_AreNotACollision()
    {
        var w = await BranchWorld.CreateAsync();
        await SetInvoiceNoAsync(w, w.Blr, "A-B-0001"); // a branch numbering "A-B" is not the default shape of prefix "A"
        await using var db = w.Db();

        await new BranchService(db).EnsureCompanyInvoicePrefixAllowedAsync("A");
    }

    [Fact]
    public async Task UpdateAsync_OwnPrefixCollidesOnlyWithDigitSuffixedNumbers()
    {
        var w = await BranchWorld.CreateAsync();
        await new BranchService(w.Db()).CreateAsync(New("KAR", "KRR"), w.UserId);
        await SetInvoiceNoAsync(w, w.Main, "A-B-0001");
        await using var db = w.Db();
        var service = new BranchService(db);

        var ok = await service.UpdateAsync(w.Blr, Edit("Bengaluru", "A"), w.UserId);
        Assert.Equal("A", ok!.InvoicePrefix);
        await SetInvoiceNoAsync(w, w.Main, "B-0001");
        Assert.Equal("InvoicePrefix", await FieldOfAsync(() => service.UpdateAsync(w.Blr, Edit("Bengaluru", "B"), w.UserId)));
    }

    [Fact]
    public async Task SettingsPut_UnchangedPrefixSucceedsEvenIfADerivedShapeIsNowTooLong_ChangedPrefixIsValidated()
    {
        var w = await BranchWorld.CreateAsync(companyPrefix: "ABCDEFGHIJ"); // Blr derives ABCDEFGHIJ-BLR (too long)
        await using var db = w.Db();
        db.Companies.Add(new Sellora.Domain.Entities.Company { Id = Sellora.Domain.Companies.DefaultTenant.CompanyId, Code = "DEF", Name = "Sellora Mart" });
        await db.SaveChangesAsync();
        var controller = new SettingsController(db, new BranchService(db));

        UpdateBusinessSettingsRequest Request(string prefix) =>
            new("Sellora Mart 2", null, null, null, null, null, null, prefix, w.CurrencyId);

        Assert.IsType<OkObjectResult>((await controller.Update(Request("abcdefghij"), CancellationToken.None)).Result);
        Assert.IsType<ObjectResult>((await controller.Update(Request("ABCDEFGHIK"), CancellationToken.None)).Result);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task UpdateHeaderAsync_DocumentOfAnInaccessibleBranch_IsNotFound()
    {
        var w = await BranchWorld.CreateAsync();
        var purchase = await w.PurchaseAsync(w.Blr);
        await using var denied = w.Db(allowed: new HashSet<Guid> { w.Main });
        await using var allowed = w.Db(allowed: new HashSet<Guid> { w.Main, w.Blr });
        var input = new UpdatePurchaseHeaderInput("SUP-1", "note");

        // Phase 5 CHANGED ASSERTION: the Branch query filter now hides the other branch's document, so it is "not found" (null -> 404)
        // instead of the former DomainValidationException("branchId").
        Assert.Null(await new PurchaseService(denied, new ExchangeRateService(denied)).UpdateHeaderAsync(purchase.Id, input));
        var updated = await new PurchaseService(allowed, new ExchangeRateService(allowed)).UpdateHeaderAsync(purchase.Id, input);
        Assert.Equal("SUP-1", updated!.SupplierInvoiceNumber);
    }
}
