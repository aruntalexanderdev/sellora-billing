using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Branches;

/// <summary>T3 — branch writes: create/edit/status, uniqueness per company, code immutability, default-branch rules, invoice-prefix rules.</summary>
public class BranchServiceTests
{
    private static CreateBranchInput New(string code, string? name = null, string? prefix = null) =>
        new(code, name ?? code + " branch", null, null, null, null, null, null, prefix);

    private static UpdateBranchInput Edit(string name, string? prefix = null, string? gstin = null) =>
        new(name, null, null, null, null, null, gstin, prefix);

    private static async Task<string> FieldOfAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(action);
        return ex.Field;
    }

    [Fact]
    public async Task CreateAsync_ValidInput_NormalisesCodeStampsCompanyAndStartsActiveNonDefault()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();

        var branch = await new BranchService(db).CreateAsync(New(" kar ", "  Karur  ") with { Gstin = " 33AAAAA0000A1Z5 ", City = "Karur" }, w.UserId);

        Assert.Equal(("KAR", "Karur", "33AAAAA0000A1Z5"), (branch.Code, branch.Name, branch.Gstin));
        Assert.Equal((TestCompany.DefaultId, false, true, w.UserId), (branch.CompanyId, branch.IsDefault, branch.IsActive, branch.CreatedByUserId));
        Assert.Null(branch.InvoicePrefix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TOOLONG")]
    [InlineData("A-B")]
    [InlineData("a b")]
    public async Task CreateAsync_InvalidCode_RejectsOnCode(string code)
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();

        Assert.Equal("Code", await FieldOfAsync(() => new BranchService(db).CreateAsync(New(code), w.UserId)));
    }

    [Fact]
    public async Task CreateAsync_DuplicateCodeOrNameInTheSameCompany_IsRejectedCaseInsensitively()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        var service = new BranchService(db);

        Assert.Equal("Code", await FieldOfAsync(() => service.CreateAsync(New("blr", "Another"), w.UserId)));
        Assert.Equal("Code", await FieldOfAsync(() => service.CreateAsync(New("main", "Another"), w.UserId)));
        Assert.Equal("Name", await FieldOfAsync(() => service.CreateAsync(New("KAR", "BENGALURU"), w.UserId)));
        Assert.Equal(2, await db.Branches.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_SameCodeAndNameInAnotherCompany_Succeeds()
    {
        var w = await BranchWorld.CreateAsync();
        await using (var other = new Sellora.Infrastructure.Data.SelloraDbContext(w.Options, TestCompany.For(TestCompany.OtherId), Sellora.Infrastructure.Data.BranchContext.Unrestricted()))
        {
            other.BusinessSettings.Add(new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "B", InvoicePrefix = "INV", BaseCurrencyId = w.CurrencyId });
            await other.SaveChangesAsync();
            var created = await new BranchService(other).CreateAsync(New("BLR", "Bengaluru"), w.UserId);
            Assert.Equal(TestCompany.OtherId, created.CompanyId);
        }

        await using var a = w.Db();
        Assert.Equal(1, await a.Branches.CountAsync(b => b.Code == "BLR"));
    }

    [Fact]
    public async Task UpdateAsync_ChangesDetailsButNeverTheCodeOrDefaultFlag()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();

        var updated = await new BranchService(db).UpdateAsync(w.Blr, Edit("Bangalore East", gstin: "29BBBBB1111B1Z5"), w.UserId);

        Assert.NotNull(updated);
        Assert.Equal(("BLR", "Bangalore East", false, "29BBBBB1111B1Z5"), (updated!.Code, updated.Name, updated.IsDefault, updated.Gstin));
        Assert.Equal(w.UserId, updated.UpdatedByUserId);
        Assert.False(typeof(UpdateBranchInput).GetProperties().Any(p => p.Name is "Code" or "IsDefault"), "The update contract must not carry the code or the default flag.");
    }

    [Fact]
    public async Task UpdateAsync_UnknownBranchOrDuplicateName_NullOrRejected()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        var service = new BranchService(db);

        Assert.Null(await service.UpdateAsync(Guid.NewGuid(), Edit("X"), w.UserId));
        Assert.Equal("Name", await FieldOfAsync(() => service.UpdateAsync(w.Blr, Edit("main"), w.UserId)));
    }

    [Fact]
    public async Task SetStatusAsync_DefaultBranchCannotBeDeactivated_OtherBranchesToggle()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        var service = new BranchService(db);

        Assert.Equal("IsActive", await FieldOfAsync(() => service.SetStatusAsync(w.Main, false, w.UserId)));
        var off = await service.SetStatusAsync(w.Blr, false, w.UserId);
        Assert.False(off!.IsActive);
        var on = await service.SetStatusAsync(w.Blr, true, w.UserId);
        Assert.True(on!.IsActive);
        Assert.Null(await service.SetStatusAsync(Guid.NewGuid(), false, w.UserId));
        Assert.True((await db.Branches.SingleAsync(b => b.Id == w.Main)).IsActive);
    }

    [Fact]
    public async Task UpdateAsync_DefaultBranchWithItsOwnInvoicePrefix_IsRejected()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();

        Assert.Equal("InvoicePrefix", await FieldOfAsync(() => new BranchService(db).UpdateAsync(w.Main, Edit("Main", "HQ"), w.UserId)));
    }

    [Theory]
    [InlineData("INV", "company invoice prefix")]
    [InlineData("inv", "company invoice prefix")]
    [InlineData("-AB", "pattern")]
    [InlineData("AB-", "pattern")]
    [InlineData("A B", "pattern")]
    [InlineData("ELEVENCHARS", "pattern")]
    public async Task UpdateAsync_BadOrCompanyEqualPrefix_IsRejected(string prefix, string why)
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();

        Assert.True("InvoicePrefix" == await FieldOfAsync(() => new BranchService(db).UpdateAsync(w.Blr, Edit("Bengaluru", prefix), w.UserId)), why);
    }

    [Fact]
    public async Task UpdateAsync_PrefixOfAnotherBranchOrItsDerivedShape_IsRejectedAndUnusedPrefixAccepted()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        var service = new BranchService(db);
        await service.CreateAsync(New("KAR", "Karur", "KRR"), w.UserId);
        await service.CreateAsync(New("HYD", "Hyderabad"), w.UserId); // derives INV-HYD

        Assert.Equal("InvoicePrefix", await FieldOfAsync(() => service.UpdateAsync(w.Blr, Edit("Bengaluru", "KRR"), w.UserId)));
        Assert.Equal("InvoicePrefix", await FieldOfAsync(() => service.UpdateAsync(w.Blr, Edit("Bengaluru", "INV-HYD"), w.UserId)));
        var ok = await service.UpdateAsync(w.Blr, Edit("Bengaluru", "bgl"), w.UserId);
        Assert.Equal("BGL", ok!.InvoicePrefix);
    }

    [Fact]
    public async Task CreateAsync_DerivedPrefixEqualToAnotherBranchOwnPrefix_IsRejectedOnCode()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        var service = new BranchService(db);
        await service.UpdateAsync(w.Blr, Edit("Bengaluru", "INV-KAR"), w.UserId);

        Assert.Equal("Code", await FieldOfAsync(() => service.CreateAsync(New("KAR"), w.UserId)));
    }

    [Fact]
    public async Task UpdateAsync_PrefixAlreadyOnInvoicesOfAnotherBranch_IsRejected()
    {
        var w = await BranchWorld.CreateAsync();
        await using (var seed = w.Db())
        {
            await new BranchService(seed).CreateAsync(New("KAR", "Karur"), w.UserId);
            var sale = await w.SaleAsync(null);
            sale = await seed.SalesInvoices.SingleAsync(s => s.Id == sale.Id);
            sale.InvoiceNo = "XYZ-0001"; // a legacy number at Main that looks like prefix XYZ
            await seed.SaveChangesAsync();
        }
        await using var db = w.Db();

        Assert.Equal("InvoicePrefix", await FieldOfAsync(() => new BranchService(db).UpdateAsync(w.Blr, Edit("Bengaluru", "XYZ"), w.UserId)));
    }

    [Fact]
    public async Task CreateAsync_WorstCaseInvoiceNumberBeyondSixteenCharacters_IsRejectedUnlessItHasItsOwnPrefix()
    {
        var w = await BranchWorld.CreateAsync(companyPrefix: "ABCDEFGHIJ"); // ABCDEFGHIJ-BLR + "-" + 5 digits = 20
        await using var db = w.Db();
        var service = new BranchService(db);

        Assert.Equal("Code", await FieldOfAsync(() => service.CreateAsync(New("KAR"), w.UserId)));
        var withPrefix = await service.CreateAsync(New("KAR", "Karur", "KRR"), w.UserId);
        Assert.Equal("KRR", withPrefix.InvoicePrefix);
    }

    [Fact]
    public async Task EnsureCompanyInvoicePrefixAllowedAsync_CollidingOrTooLongForABranch_IsRejected()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        var service = new BranchService(db);
        await service.UpdateAsync(w.Blr, Edit("Bengaluru", "BGL"), w.UserId);
        await service.CreateAsync(New("KAR", "Karur"), w.UserId);

        await service.EnsureCompanyInvoicePrefixAllowedAsync("SAL");
        Assert.Equal("InvoicePrefix", await FieldOfAsync(() => service.EnsureCompanyInvoicePrefixAllowedAsync("bgl")));
        Assert.Equal("InvoicePrefix", await FieldOfAsync(() => service.EnsureCompanyInvoicePrefixAllowedAsync("ABCDEFGHIJ"))); // KAR derives ABCDEFGHIJ-KAR
    }

    [Fact]
    public void EffectiveInvoicePrefix_DefaultUsesCompanyPrefix_OthersOwnPrefixElseCompanyPlusCode()
    {
        var main = BranchBuilder.Main();
        var own = BranchBuilder.Extra("KAR");
        own.InvoicePrefix = "KRR";
        var derived = BranchBuilder.Extra("HYD");

        Assert.Equal(("INV", "KRR", "INV-HYD"), (DocumentNumberFormat.InvoicePrefix(main, "INV"), DocumentNumberFormat.InvoicePrefix(own, "INV"), DocumentNumberFormat.InvoicePrefix(derived, "INV")));
        Assert.Equal("PUR-0007", DocumentNumberFormat.Format("PUR", main, "INV", 7));
        Assert.Equal("PUR-HYD-0007", DocumentNumberFormat.Format("PUR", derived, "INV", 7));
        Assert.Equal("EXP-0007", DocumentNumberFormat.Format("EXP", derived, "INV", 7));
        Assert.Equal("KRR-0007", DocumentNumberFormat.Format("INV", own, "INV", 7));
    }
}
