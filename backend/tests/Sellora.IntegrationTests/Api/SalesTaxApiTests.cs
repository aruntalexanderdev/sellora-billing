using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.Api.BranchApiWorld;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// TP-4 end to end on the real API and PostgreSQL (docs/features/tax-profiles.md, "TP-4 design"): the POS path (POST /sales) for an intra-state customer, an inter-state customer, a walk-in that carries a state, a VAT
/// company, tax-inclusive shelf prices with an exact total, and a whole-bill discount with charges, each followed by payment, a partial return, the refund, the remaining return and a cancellation, with the stock
/// reconciliation and every V__tax-*.sql validation file returning zero rows after each step; <c>expectedGrandTotal</c> (400), the bill discount permission (403), and the race of a sale against a new version of the
/// same profile (the sale holds FOR KEY SHARE on the profile, the version add FOR UPDATE: whoever is second waits, and nothing commits on a superseded version).
/// Shared dedicated world (its own database and host): the reconciliation SQL is global there. Tests of one class run one after the other; each sets the price basis it needs.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SalesTaxApiTests(PostgresFixture postgres)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static BranchApiWorld? world;

    private async Task<BranchApiWorld> WorldAsync()
    {
        await Gate.WaitAsync();
        try { return world ??= await CreateDedicatedAsync(postgres); }
        finally { Gate.Release(); }
    }

    // ------------------------------------------------------------------------------------------------------------ helpers

    private static async Task SetBasisAsync(BranchApiWorld w, bool pricesIncludeTax, decimal? maxBillDiscount = null)
    {
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        var s = await db.BusinessSettings.SingleAsync();
        var response = await w.Admin.PutAsJsonAsync("/api/v1/settings", new UpdateBusinessSettingsRequest(s.BusinessName, s.AddressLine, s.City, s.State, s.Gstin, s.Phone, s.Email, s.InvoicePrefix, s.BaseCurrencyId,
            null, null, s.CountryId, s.StateId, pricesIncludeTax, maxBillDiscount), ApiHost.Json);
        Check(response, "set the price basis");
    }

    /// <summary>The state the company sells from (branch state, else the business settings' state) and any other Indian state.</summary>
    private static async Task<(Guid Own, Guid Other, Guid Country)> StatesAsync(BranchApiWorld w)
    {
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        var settings = await db.BusinessSettings.SingleAsync();
        var branchState = await db.Branches.Where(b => b.Id == w.MainId).Select(b => b.StateId).SingleAsync();
        var own = branchState ?? settings.StateId ?? throw new InvalidOperationException("the world has no own state");
        var country = await db.States.Where(s => s.Id == own).Select(s => s.CountryId).SingleAsync();
        var other = await db.States.Where(s => s.CountryId == country && s.Id != own).OrderBy(s => s.Code).Select(s => s.Id).FirstAsync();
        return (own, other, country);
    }

    private static async Task<Guid> CustomerAsync(BranchApiWorld w, string name, Guid? state)
    {
        var (_, _, country) = await StatesAsync(w);
        var response = await w.Admin.PostAsJsonAsync("/api/v1/customers", new UpsertCustomerRequest(name + " " + Guid.NewGuid().ToString("N")[..6], null, null, state is null ? null : country, state), ApiHost.Json);
        Check(response, "create customer");
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> SaleAsync(BranchApiWorld w, Guid customer, decimal qty, decimal price = 20m, decimal discount = 0m, decimal paid = 0m,
        decimal? billPercent = null, decimal? billAmount = null, decimal charges = 0m, decimal? expected = null, HttpClient? client = null, Guid? product = null, Guid? unit = null) =>
        (client ?? w.Admin).PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(customer, Today, PaymentMethod.Cash, paid,
            [new CreateSaleLineRequest(product ?? w.ProductId, unit ?? w.ProductUnitId, qty, price, discount)], null, null, billPercent, billAmount, charges, expected), ApiHost.Json);

    private static async Task<JsonElement> CreatedAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await JsonAsync(response);
    }

    private static Task<JsonElement> DetailAsync(BranchApiWorld w, JsonElement sale) => w.Admin.GetAsync($"/api/v1/sales/{sale.GetProperty("id").GetGuid()}").ContinueWith(t => CreatedAsync(t.Result)).Unwrap();

    private static async Task<JsonElement> ReturnAsync(BranchApiWorld w, JsonElement sale, decimal qty)
    {
        var response = await w.Admin.PostAsJsonAsync("/api/v1/sales-returns", new CreateSaleReturnRequest(sale.GetProperty("id").GetGuid(), Today, "tp4 test",
            [new SaleReturnLineRequest(sale.GetProperty("lines")[0].GetProperty("id").GetGuid(), null, qty)], null), ApiHost.Json);
        return await CreatedAsync(response);
    }

    private static async Task<JsonElement> PayAsync(BranchApiWorld w, JsonElement sale)
    {
        var due = D(await DetailAsync(w, sale), "dueAmount");
        return await CreatedAsync(await w.Admin.PostAsJsonAsync($"/api/v1/sales/{sale.GetProperty("id").GetGuid()}/payments", new AddSalePaymentRequest(due, PaymentMethod.Cash, due), ApiHost.Json));
    }

    private static async Task<JsonElement> RefundAsync(BranchApiWorld w, JsonElement sale)
    {
        var refundable = D(await DetailAsync(w, sale), "refundableAmount");
        Assert.True(refundable > 0m, "nothing to refund");
        return await CreatedAsync(await w.Admin.PostAsJsonAsync($"/api/v1/sales/{sale.GetProperty("id").GetGuid()}/refunds", new AddSaleRefundRequest(refundable, PaymentMethod.Cash, refundable, null, "tp4 test", null), ApiHost.Json));
    }

    private static decimal D(JsonElement e, string name) => e.GetProperty(name).GetDecimal();
    private static string[] Kinds(JsonElement line) => line.GetProperty("taxes").EnumerateArray().Select(r => r.GetProperty("kind").GetString()!).ToArray();
    private static decimal SumRows(JsonElement line, string field) => line.GetProperty("taxes").EnumerateArray().Sum(r => r.GetProperty(field).GetDecimal());

    /// <summary>Stock reconciliation (QOH = ledger = batches) plus every V__tax-*.sql file: zero rows.</summary>
    private static async Task AssertConsistentAsync(BranchApiWorld w, string when, params string[] skip)
    {
        await using (var db = w.Host.Database.CreateDbContext(w.CompanyId)) await StockInvariants.AssertHoldAsync(db);
        foreach (var file in TaxProfileWorld.ValidationFiles.Except(skip)) await ValidationScripts.AssertZeroRowsAsync(w.Host.Database, file, minimumStatements: 1, when);
    }

    private static async Task<int> InvoiceCountAsync(BranchApiWorld w)
    {
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        return await db.SalesInvoices.CountAsync();
    }

    // ------------------------------------------------------------------------------------------------------------ intra-state GST, the whole life of a sale

    [Fact]
    public async Task Sale_IntraStateExclusive_SplitsCgstSgst_ThenPaymentPartialReturnRefundAndTheRemainingReturn_AllReconcile()
    {
        var w = await WorldAsync();
        await SetBasisAsync(w, pricesIncludeTax: false);
        var (own, _, _) = await StatesAsync(w);
        var customer = await CustomerAsync(w, "Same state", own);

        var sale = await CreatedAsync(await SaleAsync(w, customer, 10m));

        var line = sale.GetProperty("lines")[0];
        Assert.Equal("IntraState", sale.GetProperty("placeOfSupply").GetString());
        Assert.Equal(["Cgst", "Sgst"], Kinds(line));
        Assert.Equal(200m, D(line, "taxableValue"));
        Assert.Equal(36m, D(line, "taxAmount"));
        Assert.All(line.GetProperty("taxes").EnumerateArray(), r => Assert.Equal(18m, r.GetProperty("taxAmount").GetDecimal()));
        Assert.False(line.GetProperty("priceIncludesTax").GetBoolean());
        Assert.Equal(200m, D(sale, "taxableTotal"));
        Assert.Equal(36m, D(sale, "taxTotal"));
        Assert.Equal(236m, D(sale, "grandTotal"));
        Assert.Equal(own, sale.GetProperty("placeOfSupplyStateId").GetGuid());
        Assert.Equal(own, sale.GetProperty("sellerStateId").GetGuid());
        await AssertConsistentAsync(w, "after the intra-state sale");

        var paid = await PayAsync(w, sale);
        Assert.Equal(236m, D(paid, "paidAmount"));
        await AssertConsistentAsync(w, "after the payment");

        var first = await ReturnAsync(w, sale, 4m);
        Assert.Equal(94.4m, D(first, "total"));   // 80 taxable + 14.40 tax
        var returnLine = first.GetProperty("lines")[0];
        Assert.Equal(["Cgst", "Sgst"], Kinds(returnLine));
        await using (var db = w.Host.Database.CreateDbContext(w.CompanyId))   // AC15: the return line carries the original's profile, version and place (the DTO does not expose the ids)
        {
            var returned = await db.SalesReturnLines.SingleAsync(l => l.Id == first.GetProperty("lines")[0].GetProperty("id").GetGuid());
            var original = await db.SalesInvoiceLines.SingleAsync(l => l.Id == line.GetProperty("id").GetGuid());
            Assert.Equal((original.TaxProfileId, original.TaxProfileVersionId, original.PlaceOfSupply), (returned.TaxProfileId, returned.TaxProfileVersionId, returned.PlaceOfSupply));
            Assert.NotNull(original.TaxProfileVersionId);
        }
        await AssertConsistentAsync(w, "after the partial return");

        var refunded = await RefundAsync(w, sale);
        Assert.Equal(0m, D(refunded, "refundableAmount"));
        await AssertConsistentAsync(w, "after the refund");

        var second = await ReturnAsync(w, sale, 6m);   // the rest: the last return takes the exact remainder
        Assert.Equal(141.6m, D(second, "total"));
        Assert.Equal(236m, D(first, "total") + D(second, "total"));
        await AssertConsistentAsync(w, "after the remaining return");
    }

    [Fact]
    public async Task Sale_Cancelled_KeepsItsTaxRows_RestoresStock_AndEverythingReconciles()
    {
        var w = await WorldAsync();
        await SetBasisAsync(w, pricesIncludeTax: false);
        var (own, _, _) = await StatesAsync(w);
        var sale = await CreatedAsync(await SaleAsync(w, await CustomerAsync(w, "To cancel", own), 3m, paid: 70.8m));

        var cancel = await w.Admin.PostAsync($"/api/v1/sales/{sale.GetProperty("id").GetGuid()}/cancel", null);

        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        var cancelled = await DetailAsync(w, sale);
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(2, cancelled.GetProperty("lines")[0].GetProperty("taxes").GetArrayLength());   // cancel leaves the tax rows untouched
        Assert.Equal(70.8m, D(cancelled, "grandTotal"));
        await AssertConsistentAsync(w, "after the cancellation");
    }

    // ------------------------------------------------------------------------------------------------------------ inter-state, walk-in with a state

    [Fact]
    public async Task Sale_InterStateCustomer_IsOneIgstRow_AndItsFullReturnReversesIt()
    {
        var w = await WorldAsync();
        await SetBasisAsync(w, pricesIncludeTax: false);
        var (own, other, _) = await StatesAsync(w);
        var sale = await CreatedAsync(await SaleAsync(w, await CustomerAsync(w, "Other state", other), 5m));

        var line = sale.GetProperty("lines")[0];
        Assert.Equal("InterState", sale.GetProperty("placeOfSupply").GetString());
        Assert.Equal(other, sale.GetProperty("placeOfSupplyStateId").GetGuid());   // the place of supply of a sale is the BUYER's state
        Assert.Equal(own, sale.GetProperty("sellerStateId").GetGuid());
        Assert.Equal(["Igst"], Kinds(line));
        Assert.Equal(18m, D(line, "taxAmount"));
        Assert.Equal(118m, D(sale, "grandTotal"));
        await AssertConsistentAsync(w, "after the inter-state sale");

        var ret = await ReturnAsync(w, sale, 5m);

        Assert.Equal(["Igst"], Kinds(ret.GetProperty("lines")[0]));
        Assert.Equal(118m, D(ret, "total"));
        Assert.Equal("InterState", ret.GetProperty("lines")[0].GetProperty("placeOfSupply").GetString());
        await AssertConsistentAsync(w, "after the full return");
    }

    [Fact]
    public async Task Sale_ToACustomerWithoutAState_IsIntraStateWithoutAWarning()
    {
        var w = await WorldAsync();
        await SetBasisAsync(w, pricesIncludeTax: false);

        var sale = await CreatedAsync(await SaleAsync(w, await CustomerAsync(w, "No state", null), 1m));

        Assert.Equal("IntraState", sale.GetProperty("placeOfSupply").GetString());
        Assert.Empty(sale.GetProperty("warnings").EnumerateArray().Where(x => x.GetProperty("field").GetString() == "customerId"));
        await AssertConsistentAsync(w, "after the sale to a customer without a state");
    }

    [Fact]
    public async Task Sale_ToAWalkInCustomerThatCarriesAState_IsAlwaysIntraState()
    {
        var w = await WorldAsync();
        await SetBasisAsync(w, pricesIncludeTax: false);
        var (_, other, country) = await StatesAsync(w);
        Guid walkIn;
        await using (var db = w.Host.Database.CreateDbContext(w.CompanyId))
        {
            var c = await db.Customers.FirstAsync(x => x.IsWalkIn);
            c.CountryId = country;
            c.StateId = other;   // another state than the seller's
            await db.SaveChangesAsync();
            walkIn = c.Id;
        }

        var sale = await CreatedAsync(await SaleAsync(w, walkIn, 2m));

        Assert.Equal("IntraState", sale.GetProperty("placeOfSupply").GetString());   // TP4-Q3
        Assert.Equal(["Cgst", "Sgst"], Kinds(sale.GetProperty("lines")[0]));
        await AssertConsistentAsync(w, "after the walk-in sale");
    }

    // ------------------------------------------------------------------------------------------------------------ tax-inclusive shelf prices

    [Fact]
    public async Task Sale_InclusivePrices_TheCustomerPaysExactlyShelfTimesQuantityMinusDiscounts_AndReturnsSumExactly()
    {
        var w = await WorldAsync();
        await SetBasisAsync(w, pricesIncludeTax: true);
        var (own, _, _) = await StatesAsync(w);
        var customer = await CustomerAsync(w, "Inclusive", own);

        var plain = await CreatedAsync(await SaleAsync(w, customer, 10m));
        Assert.True(plain.GetProperty("lines")[0].GetProperty("priceIncludesTax").GetBoolean());
        Assert.Equal(200m, D(plain, "grandTotal"));                                      // 20 x 10, tax inside the price
        Assert.Equal(D(plain, "taxableTotal") + D(plain, "taxTotal"), D(plain, "grandTotal"));
        Assert.Equal(D(plain.GetProperty("lines")[0], "taxAmount"), SumRows(plain.GetProperty("lines")[0], "taxAmount"));
        await AssertConsistentAsync(w, "after the inclusive sale");

        // line discount 5 and a whole-bill discount of 10: 7 x 20 - 5 - 10 = 125 exactly
        var discounted = await CreatedAsync(await SaleAsync(w, customer, 7m, discount: 5m, billAmount: 10m));
        Assert.Equal(125m, D(discounted, "grandTotal"));
        Assert.Equal(10m, D(discounted, "billDiscountAmount"));
        Assert.Equal(15m, D(discounted, "discountTotal"));
        await AssertConsistentAsync(w, "after the inclusive sale with discounts");

        // odd quantities: the exact promise holds when 20 x 3 does not divide by 1.18
        var odd = await CreatedAsync(await SaleAsync(w, customer, 3m));
        Assert.Equal(60m, D(odd, "grandTotal"));
        await AssertConsistentAsync(w, "after the inclusive sale of 3");

        var first = await ReturnAsync(w, plain, 3m);
        var rest = await ReturnAsync(w, plain, 7m);
        Assert.Equal(200m, D(first, "total") + D(rest, "total"));   // the last return takes the exact remainder
        await AssertConsistentAsync(w, "after the two returns of the inclusive sale");
    }

    // ------------------------------------------------------------------------------------------------------------ bill discount and charges

    [Fact]
    public async Task Sale_BillDiscountPercentAndCharges_AreInTheTaxableValue_AndTheHeaderEqualsTheSumOfTheLines()
    {
        var w = await WorldAsync();
        await SetBasisAsync(w, pricesIncludeTax: false);
        var (own, _, _) = await StatesAsync(w);

        // 10 x 20 = 200, bill discount 10 percent = 20, bill charges 15: taxable 195, tax 35.10, total 230.10
        var sale = await CreatedAsync(await SaleAsync(w, await CustomerAsync(w, "Bill discount", own), 10m, billPercent: 10m, charges: 15m));

        var line = sale.GetProperty("lines")[0];
        Assert.Equal(20m, D(line, "billDiscountShare"));
        Assert.Equal(15m, D(line, "billChargeShare"));
        Assert.Equal(195m, D(line, "taxableValue"));
        Assert.Equal(35.1m, D(line, "taxAmount"));
        Assert.Equal(20m, D(sale, "billDiscountAmount"));
        Assert.Equal(10m, D(sale, "billDiscountPercent"));
        Assert.Equal(15m, D(sale, "billCharges"));
        Assert.Equal(195m, D(sale, "taxableTotal"));
        Assert.Equal(230.1m, D(sale, "grandTotal"));
        await AssertConsistentAsync(w, "after the sale with bill discount and charges");

        var ret = await ReturnAsync(w, sale, 5m);

        // half of the landed taxable value (97.50) plus the tax per component, each rounded to the currency: 2 x round(8.775) = 17.56 (not 17.55)
        Assert.Equal(115.06m, D(ret, "total"));
        await AssertConsistentAsync(w, "after the return");
        var rest = await ReturnAsync(w, sale, 5m);
        Assert.Equal(230.1m, D(ret, "total") + D(rest, "total"));   // the last return takes the exact remainder
        await AssertConsistentAsync(w, "after the remaining return");
    }

    // ------------------------------------------------------------------------------------------------------------ expectedGrandTotal, permission

    [Fact]
    public async Task Sale_ExpectedGrandTotalThatDiffers_Is400OnTheField_SavesNothing_AndTheRightOneSucceeds()
    {
        var w = await WorldAsync();
        await SetBasisAsync(w, pricesIncludeTax: false);
        var (own, _, _) = await StatesAsync(w);
        var customer = await CustomerAsync(w, "Expected", own);
        var before = await InvoiceCountAsync(w);

        var stale = await SaleAsync(w, customer, 10m, expected: 235m);

        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Contains("expectedgrandtotal", await ErrorFieldsAsync(stale));
        Assert.Equal(before, await InvoiceCountAsync(w));
        var ok = await CreatedAsync(await SaleAsync(w, customer, 10m, expected: 236m));
        Assert.Equal(236m, D(ok, "grandTotal"));
        await AssertConsistentAsync(w, "after the stale total");
    }

    [Fact]
    public async Task Sale_WholeBillDiscount_Is403WithoutTheBillDiscountPermission_AndTheSameUserCanSellWithoutIt()
    {
        var w = await WorldAsync();
        await SetBasisAsync(w, pricesIncludeTax: false);
        var (own, _, _) = await StatesAsync(w);
        var customer = await CustomerAsync(w, "Cashier", own);
        var r = await RestrictedUserWorld.CreateDedicatedAsync(w);
        var role = await w.Admin.PostAsJsonAsync("/api/v1/roles", new CreateRoleRequest("Cashier " + Guid.NewGuid().ToString("N")[..6], null, ["Sales.View", "Sales.Create", "Masters.View", "Company.Branches.View"]), ApiHost.Json);
        Check(role, "create cashier role");
        var cashier = await r.CreateAsync("cash", (await JsonAsync(role)).GetProperty("id").GetGuid());
        var before = await InvoiceCountAsync(w);

        var denied = await SaleAsync(w, customer, 2m, billPercent: 5m, client: cashier.Client);
        var deniedAmount = await SaleAsync(w, customer, 2m, billAmount: 1m, client: cashier.Client);
        var plain = await SaleAsync(w, customer, 2m, client: cashier.Client);
        var admin = await SaleAsync(w, customer, 2m, billPercent: 5m);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deniedAmount.StatusCode);
        Assert.True(plain.IsSuccessStatusCode, await plain.Content.ReadAsStringAsync());
        Assert.True(admin.IsSuccessStatusCode, await admin.Content.ReadAsStringAsync());   // the administrator holds it
        Assert.Equal(before + 2, await InvoiceCountAsync(w));
        await AssertConsistentAsync(w, "after the refused bill discounts");
    }

    [Fact]
    public async Task Sale_BillDiscountAboveTheCompanyCap_Is400OnTheFieldSent()
    {
        var w = await WorldAsync();
        await SetBasisAsync(w, pricesIncludeTax: false, maxBillDiscount: 5m);
        try
        {
            var (own, _, _) = await StatesAsync(w);
            var customer = await CustomerAsync(w, "Cap", own);

            var percent = await SaleAsync(w, customer, 2m, billPercent: 6m);
            var amount = await SaleAsync(w, customer, 2m, billAmount: 3m);   // 3 of 40 = 7.5 percent

            Assert.Equal(HttpStatusCode.BadRequest, percent.StatusCode);
            Assert.Contains("billdiscountpercent", await ErrorFieldsAsync(percent));
            Assert.Equal(HttpStatusCode.BadRequest, amount.StatusCode);
            Assert.Contains("billdiscountamount", await ErrorFieldsAsync(amount));
            Assert.True((await SaleAsync(w, customer, 2m, billPercent: 5m)).IsSuccessStatusCode);
        }
        finally { await SetBasisAsync(w, pricesIncludeTax: false, maxBillDiscount: null); }
        await AssertConsistentAsync(w, "after the capped discounts");
    }

    // ------------------------------------------------------------------------------------------------------------ VAT company

    /// <summary>The world was built in India and switched to the UAE by the test: its products still carry the Indian mirror TaxId, which is exactly what this file reports (an artifact of the switch, not of the sale).</summary>
    private const string SkipForTheSwitchedCountry = "tax-products-resolve.sql";

    [Fact]
    public async Task Sale_InAUaeCompany_IsVatWithPlaceOfSupplyNotApplicable_AndItsReturnFollows()
    {
        var w = await CreateDedicatedAsync(postgres);
        await using var disposeHost = w.Host;
        await using (var db = w.Host.Database.CreateDbContext(w.CompanyId))
        {
            var uae = await db.Countries.SingleAsync(c => c.Code == "AE");
            var settings = await db.BusinessSettings.SingleAsync();
            settings.CountryId = uae.Id;
            settings.StateId = null;
            settings.SellingPricesIncludeTax = false;
            await db.SaveChangesAsync();
            await TaxSeeder.SeedCompanyAsync(db, w.CompanyId, uae.Id);
            await db.SaveChangesAsync();
        }

        var sale = await CreatedAsync(await SaleAsync(w, w.CustomerId, 10m));

        var line = sale.GetProperty("lines")[0];
        Assert.Equal("NotApplicable", sale.GetProperty("placeOfSupply").GetString());
        Assert.Equal("Vat", sale.GetProperty("regime").GetString());
        Assert.Equal(["Vat"], Kinds(line));
        Assert.Equal(200m, D(line, "taxableValue"));
        Assert.Equal(10m, D(line, "taxAmount"));   // 5 percent
        Assert.Equal(210m, D(sale, "grandTotal"));
        await AssertConsistentAsync(w, "after the VAT sale", SkipForTheSwitchedCountry);

        var ret = await ReturnAsync(w, sale, 4m);

        Assert.Equal(["Vat"], Kinds(ret.GetProperty("lines")[0]));
        Assert.Equal(84m, D(ret, "total"));
        await AssertConsistentAsync(w, "after the VAT return", SkipForTheSwitchedCountry);
    }

    // ------------------------------------------------------------------------------------------------------------ race with a new version of the profile

    private sealed record RaceProduct(Guid ProductId, Guid ProductUnitId, Guid ProfileId, Guid OpenVersionId, Guid Store, Guid[] ComponentIds);

    /// <summary>A product on its own 12 percent profile (version from 2000-01-01) that no document uses yet, priced at 50 with 3 units in stock at the main store.</summary>
    private static async Task<RaceProduct> NewRaceProductAsync(BranchApiWorld w)
    {
        RaceProduct result;
        await using (var db = w.Host.Database.CreateDbContext(w.CompanyId))
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var tax = new Tax { Id = Guid.NewGuid(), Name = "SRace " + suffix, RatePercent = 12m, IsActive = true };
            var profile = await TaxFixtures.UseAsProfileAsync(db, tax, companyDefault: false, code: "S" + suffix[..6].ToUpperInvariant());
            var baseUnit = await db.ProductUnits.Where(u => u.Id == w.ProductUnitId).Select(u => u.UnitId).SingleAsync();
            var product = new Product { Id = Guid.NewGuid(), Sku = "SRACE-" + suffix, Name = "Sale race " + suffix, BaseUnitId = baseUnit, TaxId = tax.Id };
            var unit = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = baseUnit, IsBaseUnit = true, InnerQuantity = 1m, ConversionFactorToBase = 1m, IsSellable = true, IsPurchasable = true, IsActive = true };
            product.Units.Add(unit);
            db.Products.Add(product);
            await db.SaveChangesAsync();
            var country = await db.Countries.SingleAsync(c => c.Code == "IN");
            db.TaxAssignments.Add(new TaxAssignment { Id = Guid.NewGuid(), Scope = TaxScope.Product, ProductId = product.Id, CountryId = country.Id, SalesTaxProfileId = profile.Id, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var store = await StockSeed.StoreOfAsync(db, w.MainId);
            await StockSeed.AddAsync(db, store, product.Id, 3m, 5m, w.AdminId);
            var version = await db.TaxProfileVersions.SingleAsync(v => v.TaxProfileId == profile.Id);
            var components = await db.TaxComponents.Where(c => c.CountryId == country.Id && (c.Code == "CGST" || c.Code == "SGST" || c.Code == "IGST")).OrderBy(c => c.Code).Select(c => c.Id).ToArrayAsync();
            result = new RaceProduct(product.Id, unit.Id, profile.Id, version.Id, store, components);   // components: CGST, IGST, SGST
        }
        Check(await w.Admin.PutAsJsonAsync($"/api/v1/pricing/{result.ProductUnitId}", new SetPriceRequest(50m), ApiHost.Json), "price the race product");
        return result;
    }

    private static AddTaxVersionRequest NextVersion(RaceProduct p) => new(Today,
    [
        new TaxVersionComponentRequest(p.ComponentIds[0], TaxApplicability.IntraState, 9m, null),
        new TaxVersionComponentRequest(p.ComponentIds[2], TaxApplicability.IntraState, 9m, null),
        new TaxVersionComponentRequest(p.ComponentIds[1], TaxApplicability.InterState, 18m, null),
    ]);

    private static async Task<bool> WaiterAsync(string connectionString, string likeA, string likeB, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        await using var monitor = new NpgsqlConnection(connectionString);
        await monitor.OpenAsync();
        while (DateTime.UtcNow < deadline)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND wait_event_type = 'Lock' AND query ILIKE @a AND query ILIKE @b
                """, monitor);
            cmd.Parameters.AddWithValue("a", likeA);
            cmd.Parameters.AddWithValue("b", likeB);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0) return true;
            await Task.Delay(25);
        }
        return false;
    }

    [Fact]
    public async Task Race_VersionAddHoldingTheProfile_TheSaleWaitsForItAndThenFailsCleanlyOn_Lines_NeverCommittingOnTheSupersededVersion()
    {
        var w = await WorldAsync();
        await SetBasisAsync(w, pricesIncludeTax: false);
        var (own, _, _) = await StatesAsync(w);
        var customer = await CustomerAsync(w, "Race A", own);
        var p = await NewRaceProductAsync(w);
        await using var gate = new NpgsqlConnection(w.Host.Database.ConnectionString);
        await gate.OpenAsync();
        await using var tx = await gate.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"""SELECT "Id" FROM "TaxProfileVersions" WHERE "Id" = '{p.OpenVersionId}' FOR UPDATE""", gate, tx)) await hold.ExecuteNonQueryAsync();

        // 1. the version add takes the profile FOR UPDATE and then waits for the gate in its UPDATE of the open version
        var versionTask = w.Admin.PostAsJsonAsync($"/api/v1/tax-profiles/{p.ProfileId}/versions", NextVersion(p), ApiHost.Json);
        Assert.True(await WaiterAsync(w.Host.Database.ConnectionString, "%UPDATE%TaxProfileVersions%", "%", TimeSpan.FromSeconds(30)), "the version add did not block on the open version");
        // 2. the sale resolves the old version, then waits for the profile lock (FOR KEY SHARE conflicts with FOR UPDATE)
        var saleTask = SaleAsync(w, customer, 1m, price: 50m, product: p.ProductId, unit: p.ProductUnitId);
        Assert.True(await WaiterAsync(w.Host.Database.ConnectionString, "%TaxProfiles%", "%FOR KEY SHARE%", TimeSpan.FromSeconds(30)), "the sale did not wait for the profile lock");
        Assert.False(saleTask.IsCompleted);
        await tx.RollbackAsync();

        var version = await versionTask;
        var sale = await saleTask;

        Assert.True(version.IsSuccessStatusCode, await version.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, sale.StatusCode);
        Assert.Contains("lines", await ErrorFieldsAsync(sale));
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        Assert.Equal(0, await db.SalesInvoiceLines.CountAsync(l => l.ProductId == p.ProductId));
        Assert.Equal(3m, await db.StockItems.Where(s => s.ProductId == p.ProductId).SumAsync(s => s.QuantityOnHand));   // only the seeded units
        await AssertConsistentAsync(w, "after the refused sale");
    }

    [Fact]
    public async Task Race_SaleHoldingTheProfile_TheVersionAddWaitsForItsCommit_AndIsThenRefusedBecauseTheVersionIsUsedToday()
    {
        var w = await WorldAsync();
        await SetBasisAsync(w, pricesIncludeTax: false);
        var (own, _, _) = await StatesAsync(w);
        var customer = await CustomerAsync(w, "Race B", own);
        var p = await NewRaceProductAsync(w);
        await using var gate = new NpgsqlConnection(w.Host.Database.ConnectionString);
        await gate.OpenAsync();
        await using var tx = await gate.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"""SELECT 1 FROM "StockItems" WHERE "ProductId" = '{p.ProductId}' AND "StockLocationId" = '{p.Store}' FOR UPDATE""", gate, tx)) await hold.ExecuteNonQueryAsync();

        // 1. the sale takes the profile FOR KEY SHARE (before the stock rows) and then waits for the gate on the StockItems row
        var saleTask = SaleAsync(w, customer, 1m, price: 50m, product: p.ProductId, unit: p.ProductUnitId);
        Assert.True(await WaiterAsync(w.Host.Database.ConnectionString, "%StockItems%", "%", TimeSpan.FromSeconds(30)), "the sale did not wait on the stock row");
        // 2. the version add needs the profile FOR UPDATE: it must wait for the sale, not overtake it
        var versionTask = w.Admin.PostAsJsonAsync($"/api/v1/tax-profiles/{p.ProfileId}/versions", NextVersion(p), ApiHost.Json);
        Assert.True(await WaiterAsync(w.Host.Database.ConnectionString, "%TaxProfiles%", "%FOR UPDATE%", TimeSpan.FromSeconds(30)), "the version add did not wait for the sale's profile lock");
        await Task.Delay(300);
        Assert.False(versionTask.IsCompleted, "the version add finished although a sale on the profile was still open");
        await tx.RollbackAsync();

        var sale = await saleTask;
        var version = await versionTask;

        var created = await CreatedAsync(sale);
        var line = created.GetProperty("lines")[0];
        Assert.Equal(p.OpenVersionId, line.GetProperty("taxProfileVersionId").GetGuid());   // committed on the version that was open when it started
        Assert.Equal(12m, D(line, "taxRatePercent"));
        Assert.Equal(HttpStatusCode.BadRequest, version.StatusCode);                        // a version cannot supersede one that a document of today already uses
        Assert.Contains("effectivefrom", await ErrorFieldsAsync(version));
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        Assert.Equal(1, await db.TaxProfileVersions.CountAsync(v => v.TaxProfileId == p.ProfileId));
        await AssertConsistentAsync(w, "after the sale that won the race");
    }
}
