using Sellora.Domain.Entities;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.TaxProfiles;

/// <summary>
/// TP-5a (T25, BR4): a version is "used" when a sales or purchase line of the PROFILE is dated inside its range. A line belongs to the profile by its
/// <c>TaxProfileId</c> (new lines, and history after the backfill), or while history is not backfilled yet (profile NULL) by the profile's mirror <c>TaxId</c>.
/// </summary>
public class TaxVersionUsageTests
{
    private static readonly DateOnly D = TaxWorld.Today;

    private sealed record Fx(TaxWorld W, TaxProfile Profile, TaxProfile Other, Product Product);

    private static async Task<Fx> SetupAsync()
    {
        var w = await TaxWorld.CreateAsync();
        var profile = await w.ProfileAsync("GST-18");
        var other = await w.ProfileAsync("GST-5");
        return new Fx(w, profile, other, await w.AddProductAsync("U1"));
    }

    private static async Task AddSaleLineAsync(Fx f, DateOnly date, Guid? profileId, Guid taxId, Guid? versionId = null)
    {
        var invoice = new SalesInvoice { Id = Guid.NewGuid(), InvoiceNo = "H-" + Guid.NewGuid().ToString("N")[..6], InvoiceDate = date };
        f.W.Db.SalesInvoiceLines.Add(new SalesInvoiceLine
        {
            Id = Guid.NewGuid(), SalesInvoiceId = invoice.Id, SalesInvoice = invoice, ProductId = f.Product.Id, TaxId = taxId, TaxProfileId = profileId, TaxProfileVersionId = versionId,
        });
        await f.W.Db.SaveChangesAsync();
    }

    private static async Task AddPurchaseLineAsync(Fx f, DateOnly date, Guid? profileId, Guid taxId, Guid? versionId = null)
    {
        var order = new PurchaseOrder { Id = Guid.NewGuid(), PurchaseNo = "P-" + Guid.NewGuid().ToString("N")[..6], PurchaseDate = date };
        f.W.Db.PurchaseOrderLines.Add(new PurchaseOrderLine
        {
            Id = Guid.NewGuid(), PurchaseOrderId = order.Id, PurchaseOrder = order, ProductId = f.Product.Id, TaxId = taxId, TaxProfileId = profileId, TaxProfileVersionId = versionId,
        });
        await f.W.Db.SaveChangesAsync();
    }

    private static Task<bool> UsedAsync(Fx f, TaxProfile p, DateOnly from, DateOnly? to) => TaxVersionUsage.IsUsedAsync(f.W.Db, p.Id, p.LegacyTaxId, from, to, default);

    [Fact]
    public async Task UnbackfilledHistory_IsMatchedByTheMirrorTaxId_WithinTheDateRange()
    {
        var f = await SetupAsync();
        await AddSaleLineAsync(f, D, profileId: null, f.Profile.LegacyTaxId);

        Assert.True(await UsedAsync(f, f.Profile, D.AddDays(-5), D.AddDays(5)));
        Assert.True(await UsedAsync(f, f.Profile, D, null));
        Assert.False(await UsedAsync(f, f.Profile, D.AddDays(1), null)); // dated before the range
        Assert.False(await UsedAsync(f, f.Profile, D.AddDays(-30), D.AddDays(-1)));
        Assert.False(await UsedAsync(f, f.Other, D.AddDays(-5), D.AddDays(5))); // another profile's mirror
    }

    [Fact]
    public async Task BackfilledHistory_IsMatchedByTheProfileId_NotByTheMirrorTaxId()
    {
        var f = await SetupAsync();
        // The TP-5a backfill: profile set, version still NULL. The TaxId here is deliberately NOT the profile's mirror: only TaxProfileId may count.
        await AddSaleLineAsync(f, D, f.Profile.Id, taxId: Guid.NewGuid());

        Assert.True(await UsedAsync(f, f.Profile, D.AddDays(-5), D.AddDays(5)));
        Assert.False(await UsedAsync(f, f.Profile, D.AddDays(1), null));
        Assert.False(await UsedAsync(f, f.Other, D.AddDays(-5), D.AddDays(5)));
    }

    [Fact]
    public async Task ABackfilledLine_OfAnotherProfile_IsNotCountedForTheProfilesMirror()
    {
        var f = await SetupAsync();
        // Profile B was backfilled, but the line's TaxId is profile A's mirror: the profile id wins, the fallback only applies while the profile is NULL.
        await AddSaleLineAsync(f, D, f.Other.Id, f.Profile.LegacyTaxId);

        Assert.False(await UsedAsync(f, f.Profile, D.AddDays(-5), D.AddDays(5)));
        Assert.True(await UsedAsync(f, f.Other, D.AddDays(-5), D.AddDays(5)));
    }

    [Fact]
    public async Task NewLines_AreMatchedByTheirProfile_WhateverTheirMirrorTaxId()
    {
        var f = await SetupAsync();
        await AddSaleLineAsync(f, D, f.Profile.Id, f.Other.LegacyTaxId, versionId: Guid.NewGuid());
        await AddPurchaseLineAsync(f, D.AddDays(40), f.Profile.Id, f.Other.LegacyTaxId, versionId: Guid.NewGuid());

        Assert.True(await UsedAsync(f, f.Profile, D, D));
        Assert.True(await UsedAsync(f, f.Profile, D.AddDays(40), null)); // the purchase line
        Assert.False(await UsedAsync(f, f.Profile, D.AddDays(1), D.AddDays(39)));
        Assert.False(await UsedAsync(f, f.Other, D.AddDays(-5), D.AddDays(60)));
    }

    [Fact]
    public async Task PurchaseLines_BackfilledOrNot_CountLikeSalesLines()
    {
        var f = await SetupAsync();
        await AddPurchaseLineAsync(f, D, profileId: null, f.Profile.LegacyTaxId);
        await AddPurchaseLineAsync(f, D.AddDays(100), f.Other.Id, Guid.NewGuid());

        Assert.True(await UsedAsync(f, f.Profile, D, D));
        Assert.False(await UsedAsync(f, f.Profile, D.AddDays(1), null));
        Assert.True(await UsedAsync(f, f.Other, D.AddDays(100), null));
        Assert.False(await UsedAsync(f, f.Other, D, D.AddDays(99)));
    }

    [Fact]
    public async Task LatestDocumentDate_FollowsTheSameMembership_AcrossSalesAndPurchases()
    {
        var f = await SetupAsync();
        Assert.Null(await TaxVersionUsage.LatestDocumentDateAsync(f.W.Db, f.Profile.Id, f.Profile.LegacyTaxId, default));

        await AddSaleLineAsync(f, D, null, f.Profile.LegacyTaxId);               // unbackfilled sale
        await AddPurchaseLineAsync(f, D.AddDays(9), f.Profile.Id, Guid.NewGuid()); // backfilled / new purchase
        await AddSaleLineAsync(f, D.AddDays(50), f.Other.Id, f.Profile.LegacyTaxId); // belongs to the other profile

        Assert.Equal(D.AddDays(9), await TaxVersionUsage.LatestDocumentDateAsync(f.W.Db, f.Profile.Id, f.Profile.LegacyTaxId, default));
        Assert.Equal(D.AddDays(50), await TaxVersionUsage.LatestDocumentDateAsync(f.W.Db, f.Other.Id, f.Other.LegacyTaxId, default));
    }

    [Fact]
    public async Task AddingAVersionOverBackfilledHistory_IsRefused_ByTheProfileService()
    {
        var f = await SetupAsync();
        await AddSaleLineAsync(f, D, f.Profile.Id, Guid.NewGuid()); // history: profile only, no version, TaxId not the mirror

        var versions = await f.W.GstSetAsync(20m);
        var field = await TaxWorld.FieldOfAsync(() => f.W.Profiles.AddVersionAsync(f.Profile.Id, TaxWorld.Version(D, versions), null));

        Assert.Equal("effectiveFrom", field);
    }
}
