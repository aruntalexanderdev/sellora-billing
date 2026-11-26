using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// TP-2 POST /api/v1/tax/preview end to end on Npgsql: the place-of-supply and rate-set resolver queries must translate and give the
/// documented answers (docs/features/tax-profiles.md, TP-2 design). The seeded company is Indian (Tamil Nadu) and Ball Pen carries GST 18%.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TaxPreviewApiTests(PostgresFixture postgres)
{
    private const string Route = "/api/v1/tax/preview";

    private static TaxDocumentPreviewRequest Request(BranchApiWorld w, TaxDirection direction, Guid? party, DateOnly? date = null, decimal qty = 10m, decimal price = 100m) =>
        new(direction, party, date ?? Today, false, null, null, null, [new TaxPreviewLineRequest(w.ProductId, w.ProductUnitId, qty, price)]);

    private static async Task<(Guid Country, Guid? State)> GeoAsync(BranchApiWorld w, string country, string? state)
    {
        await using var db = w.Host.Database.CreateDbContext();
        var c = await db.Countries.SingleAsync(x => x.Code == country);
        var s = state is null ? null : await db.States.Where(x => x.CountryId == c.Id && x.Code == state).Select(x => (Guid?)x.Id).SingleAsync();
        return (c.Id, s);
    }

    private static async Task<Guid> CustomerAsync(BranchApiWorld w, string name, Guid? country, Guid? state)
    {
        var response = await w.Admin.PostAsJsonAsync("/api/v1/customers", new UpsertCustomerRequest(name, null, null, country, state), ApiHost.Json);
        Check(response, "create customer");
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> SupplierAsync(BranchApiWorld w, string name, Guid? country, Guid? state)
    {
        var response = await w.Admin.PostAsJsonAsync("/api/v1/suppliers", new UpsertSupplierRequest(name, null, null, null, w.CurrencyId, country, state), ApiHost.Json);
        Check(response, "create supplier");
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await JsonAsync(response);
    }

    private static string[] Codes(JsonElement preview) =>
        preview.GetProperty("lines")[0].GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("code").GetString()!).ToArray();

    private static decimal SumOfRows(JsonElement preview) =>
        preview.GetProperty("lines")[0].GetProperty("rows").EnumerateArray().Sum(r => r.GetProperty("taxAmount").GetDecimal());

    // ---- (a)-(c) sales: place of supply --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preview_SaleToCustomerInTheCompanyState_GivesCgstAndSgstHalves()
    {
        var w = await GetAsync(postgres);
        var (india, tn) = await GeoAsync(w, "IN", "TN");
        var customer = await CustomerAsync(w, "Same state " + Guid.NewGuid().ToString("N")[..6], india, tn);

        var preview = await OkAsync(await w.SendAsync(HttpMethod.Post, Route, Request(w, TaxDirection.Sale, customer)));

        Assert.Equal("IntraState", preview.GetProperty("placeOfSupply").GetString());
        Assert.Equal("Gst", preview.GetProperty("regime").GetString());
        Assert.Equal(["CGST", "SGST"], Codes(preview));
        var rows = preview.GetProperty("lines")[0].GetProperty("rows");
        Assert.All(rows.EnumerateArray(), r => Assert.Equal(9m, r.GetProperty("ratePercent").GetDecimal()));
        Assert.Equal(rows[0].GetProperty("taxAmount").GetDecimal(), rows[1].GetProperty("taxAmount").GetDecimal());
        Assert.Equal(SumOfRows(preview), preview.GetProperty("taxTotal").GetDecimal());
        Assert.True(preview.GetProperty("taxTotal").GetDecimal() > 0m);
    }

    [Fact]
    public async Task Preview_SaleToCustomerInAnotherIndianState_GivesOneIgstRowAtTheFullRate()
    {
        var w = await GetAsync(postgres);
        var (india, kerala) = await GeoAsync(w, "IN", "KL");
        var customer = await CustomerAsync(w, "Other state " + Guid.NewGuid().ToString("N")[..6], india, kerala);

        var preview = await OkAsync(await w.SendAsync(HttpMethod.Post, Route, Request(w, TaxDirection.Sale, customer)));

        Assert.Equal("InterState", preview.GetProperty("placeOfSupply").GetString());
        Assert.Equal(["IGST"], Codes(preview));
        Assert.Equal(18m, preview.GetProperty("lines")[0].GetProperty("rows")[0].GetProperty("ratePercent").GetDecimal());
        Assert.Equal(SumOfRows(preview), preview.GetProperty("taxTotal").GetDecimal());
    }

    [Fact]
    public async Task Preview_WalkInSale_IsIntraStateAndMatchesTheSameStateCustomer()
    {
        var w = await GetAsync(postgres);
        var (india, tn) = await GeoAsync(w, "IN", "TN");
        var customer = await CustomerAsync(w, "Twin " + Guid.NewGuid().ToString("N")[..6], india, tn);

        var walkIn = await OkAsync(await w.SendAsync(HttpMethod.Post, Route, Request(w, TaxDirection.Sale, null)));
        var same = await OkAsync(await w.SendAsync(HttpMethod.Post, Route, Request(w, TaxDirection.Sale, customer)));

        Assert.Equal("IntraState", walkIn.GetProperty("placeOfSupply").GetString());
        Assert.Equal(["CGST", "SGST"], Codes(walkIn));
        Assert.Equal(same.GetProperty("grandTotal").GetDecimal(), walkIn.GetProperty("grandTotal").GetDecimal());
        Assert.DoesNotContain(walkIn.GetProperty("warnings").EnumerateArray(), x => x.GetProperty("field").GetString() == "partyId"); // a walk-in is plain intra-state, no party warning
    }

    // ---- (d) purchase from a supplier without a state ------------------------------------------------------------------------------

    [Fact]
    public async Task Preview_PurchaseFromSupplierWithoutState_IsIntraStateWithAPartyWarning()
    {
        var w = await GetAsync(postgres);
        var supplier = await SupplierAsync(w, "No state " + Guid.NewGuid().ToString("N")[..6], null, null);

        var preview = await OkAsync(await w.SendAsync(HttpMethod.Post, Route, Request(w, TaxDirection.Purchase, supplier)));

        Assert.Equal("IntraState", preview.GetProperty("placeOfSupply").GetString());
        Assert.Equal(["CGST", "SGST"], Codes(preview));
        Assert.Contains(preview.GetProperty("warnings").EnumerateArray(), x => x.GetProperty("field").GetString() == "partyId");
    }

    [Fact]
    public async Task Preview_PurchaseFromSupplierInAnotherState_IsInterStateWithoutAPartyWarning()
    {
        var w = await GetAsync(postgres);
        var (india, kerala) = await GeoAsync(w, "IN", "KL");
        var supplier = await SupplierAsync(w, "Kerala supplier " + Guid.NewGuid().ToString("N")[..6], india, kerala);

        var preview = await OkAsync(await w.SendAsync(HttpMethod.Post, Route, Request(w, TaxDirection.Purchase, supplier)));

        Assert.Equal("InterState", preview.GetProperty("placeOfSupply").GetString());
        Assert.Equal(["IGST"], Codes(preview));
        Assert.DoesNotContain(preview.GetProperty("warnings").EnumerateArray(), x => x.GetProperty("field").GetString() == "partyId");
    }

    // ---- (e) UAE company: VAT ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preview_UaeCompany_GivesVatAndPlaceOfSupplyNotApplicable()
    {
        var w = await CreateDedicatedAsync(postgres);
        await using var disposeHost = w.Host;
        await using (var db = w.Host.Database.CreateDbContext(w.CompanyId))
        {
            var uae = await db.Countries.SingleAsync(c => c.Code == "AE");
            var settings = await db.BusinessSettings.SingleAsync();
            settings.CountryId = uae.Id;
            settings.StateId = null;
            await db.SaveChangesAsync();
            await TaxSeeder.SeedCompanyAsync(db, w.CompanyId, uae.Id);
            await db.SaveChangesAsync();
        }

        var preview = await OkAsync(await w.SendAsync(HttpMethod.Post, Route, Request(w, TaxDirection.Sale, null)));

        Assert.Equal("NotApplicable", preview.GetProperty("placeOfSupply").GetString());
        Assert.Equal("Vat", preview.GetProperty("regime").GetString());
        Assert.Equal(["VAT"], Codes(preview));
        Assert.Equal(5m, preview.GetProperty("lines")[0].GetProperty("rows")[0].GetProperty("ratePercent").GetDecimal());
        Assert.Equal(SumOfRows(preview), preview.GetProperty("taxTotal").GetDecimal());
    }

    // ---- (f) document date before the first version ----------------------------------------------------------------------------------

    [Fact]
    public async Task Preview_DocumentDateBeforeTheProfilesFirstVersion_Is400OnTheLine()
    {
        var w = await GetAsync(postgres);

        var response = await w.SendAsync(HttpMethod.Post, Route, Request(w, TaxDirection.Sale, null, date: new DateOnly(1990, 1, 1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var fields = await ErrorFieldsAsync(response);
        Assert.Single(fields);
        Assert.StartsWith("lines[0].", fields[0]);
    }

    // ---- (g) a branch-scoped user ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preview_BranchScopedUserWithSalesCreateAtTheirBranch_Is200AndFollowsTheBranch()
    {
        var users = await RestrictedUserWorld.GetWorldAsync(postgres);
        var w = users.World;
        var (branch, _) = await w.NewBranchAsync();
        var user = await users.CreateAtAsync(branch);

        var sale = await user.Client.PostAsJsonAsync(Route, Request(w, TaxDirection.Sale, null), ApiHost.Json);
        var preview = await OkAsync(sale);
        var purchase = await user.Client.PostAsJsonAsync(Route, Request(w, TaxDirection.Purchase, w.SupplierId), ApiHost.Json);

        Assert.Equal("IntraState", preview.GetProperty("placeOfSupply").GetString()); // the branch has no state: the company's (TN) is used (T14)
        Assert.Equal(["CGST", "SGST"], Codes(preview));
        Assert.Equal(HttpStatusCode.OK, purchase.StatusCode);
    }
}
