using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.Api.BranchApiWorld;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// TP-3 end to end on the real API and PostgreSQL (docs/features/tax-profiles.md, "TP-3 design"): purchase, return and cancel through HTTP for intra-state GST, inter-state IGST, a foreign currency supplier
/// and a bill discount with charges (landed cost), each followed by the stock reconciliation and the zero-row validation files; then the race of a purchase save against a new version of the same profile
/// (the purchase holds FOR KEY SHARE on the profile, the version add FOR UPDATE: whoever is second waits, and nothing commits on a superseded version).
/// All tests share one dedicated world (its own database and host): the reconciliation SQL is global there.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PurchaseTaxApiTests(PostgresFixture postgres)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static BranchApiWorld? world;

    private async Task<BranchApiWorld> WorldAsync()
    {
        await Gate.WaitAsync();
        try { return world ??= await CreateDedicatedAsync(postgres); }
        finally { Gate.Release(); }
    }

    private static readonly string[] ValidationFiles = ["tax-purchase-snapshot.sql", "tax-line-rows-consistency.sql", "tax-line-rows-missing-purchases.sql", "tax-profiles-integrity.sql", "tax-gst-rules.sql"];

    // ------------------------------------------------------------------------------------------------------------ helpers

    private static async Task<Guid> SupplierAsync(BranchApiWorld w, string name, string? state, Guid? currency = null)
    {
        await using var db = w.Host.Database.CreateDbContext();
        var india = await db.Countries.SingleAsync(c => c.Code == "IN");
        Guid? stateId = state is null ? null : await db.States.Where(s => s.CountryId == india.Id && s.Code == state).Select(s => (Guid?)s.Id).SingleAsync();
        var response = await w.Admin.PostAsJsonAsync("/api/v1/suppliers", new UpsertSupplierRequest(name + " " + Guid.NewGuid().ToString("N")[..6], null, null, null, currency ?? w.CurrencyId, india.Id, stateId), ApiHost.Json);
        Check(response, "create supplier");
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static CreatePurchaseLineRequest Line(Guid product, Guid unit, decimal qty, decimal rate) => new(product, unit, qty, rate, 0m, 0m);

    private static async Task<HttpResponseMessage> PurchaseAsync(BranchApiWorld w, Guid supplier, CreatePurchaseLineRequest[] lines, bool includeTax = false, decimal? discountPercent = null, decimal? discountAmount = null, decimal charges = 0m) =>
        await w.Admin.PostAsJsonAsync("/api/v1/purchases", new CreatePurchaseRequest(supplier, Today, null, null, PaymentMethod.Cash, 0m, lines, null, null, includeTax, discountPercent, discountAmount, charges), ApiHost.Json);

    private static async Task<JsonElement> CreatedAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await JsonAsync(response);
    }

    private static async Task<JsonElement> ReturnAsync(BranchApiWorld w, JsonElement purchase, decimal qty)
    {
        var response = await w.Admin.PostAsJsonAsync("/api/v1/purchase-returns", new CreatePurchaseReturnRequest(purchase.GetProperty("id").GetGuid(), Today, "tp3 test",
            [new PurchaseReturnLineRequest(purchase.GetProperty("lines")[0].GetProperty("id").GetGuid(), null, qty)]), ApiHost.Json);
        return await CreatedAsync(response);
    }

    private static decimal D(JsonElement e, string name) => e.GetProperty(name).GetDecimal();
    private static decimal SumRows(JsonElement line, string field) => line.GetProperty("taxes").EnumerateArray().Sum(r => r.GetProperty(field).GetDecimal());
    private static string[] Kinds(JsonElement line) => line.GetProperty("taxes").EnumerateArray().Select(r => r.GetProperty("kind").GetString()!).ToArray();

    /// <summary>Stock reconciliation (QOH = ledger = batches) plus every TP-3 validation file: zero rows.</summary>
    private static async Task AssertConsistentAsync(BranchApiWorld w, string when)
    {
        await using (var db = w.Host.Database.CreateDbContext(w.CompanyId)) await StockInvariants.AssertHoldAsync(db);
        foreach (var file in ValidationFiles) await ValidationScripts.AssertZeroRowsAsync(w.Host.Database, file, minimumStatements: 1, when);
    }

    // ------------------------------------------------------------------------------------------------------------ intra-state GST

    [Fact]
    public async Task Purchase_IntraStateGst_SplitsIntoCgstAndSgst_ReturnReversesBothRows_CancelRestoresStock_AndEverythingReconciles()
    {
        var w = await WorldAsync();
        var supplier = await SupplierAsync(w, "Same state", "TN");
        var purchase = await CreatedAsync(await PurchaseAsync(w, supplier, [Line(w.ProductId, w.ProductUnitId, 10m, 100m)]));

        var line = purchase.GetProperty("lines")[0];
        Assert.Equal("IntraState", purchase.GetProperty("placeOfSupply").GetString());
        Assert.Equal(["Cgst", "Sgst"], Kinds(line));
        Assert.Equal(1000m, D(line, "taxableValue"));
        Assert.Equal(180m, D(line, "taxAmount"));
        Assert.All(line.GetProperty("taxes").EnumerateArray(), r => Assert.Equal(90m, r.GetProperty("taxAmount").GetDecimal()));
        Assert.Equal(1180m, D(purchase, "grandTotal"));
        Assert.Equal(1000m, D(purchase, "taxableTotal"));
        Assert.Equal(180m, D(purchase, "taxTotal"));
        Assert.Equal(100m, D(line, "unitCost"));   // landed cost excludes the tax
        await AssertConsistentAsync(w, "after the intra-state purchase");

        var ret = await ReturnAsync(w, purchase, 4m);

        Assert.Equal(400m, D(ret, "taxableTotal"));
        Assert.Equal(72m, D(ret, "taxTotal"));
        Assert.Equal(472m, D(ret, "total"));
        var returnLine = ret.GetProperty("lines")[0];
        Assert.Equal(["Cgst", "Sgst"], Kinds(returnLine));
        Assert.All(returnLine.GetProperty("taxes").EnumerateArray(), r => Assert.Equal(36m, r.GetProperty("taxAmount").GetDecimal()));
        Assert.Equal(line.GetProperty("taxProfileVersionId").GetGuid(), returnLine.GetProperty("taxProfileVersionId").GetGuid());   // AC15: the original's version
        await AssertConsistentAsync(w, "after the return");

        var second = await CreatedAsync(await PurchaseAsync(w, supplier, [Line(w.ProductId, w.ProductUnitId, 5m, 40m)]));
        var cancel = await w.Admin.PostAsync($"/api/v1/purchases/{second.GetProperty("id").GetGuid()}/cancel", null);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        var cancelled = await JsonAsync(await w.Admin.GetAsync($"/api/v1/purchases/{second.GetProperty("id").GetGuid()}"));
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(2, cancelled.GetProperty("lines")[0].GetProperty("taxes").GetArrayLength());   // cancel leaves the tax rows untouched
        await AssertConsistentAsync(w, "after the cancellation");
    }

    // ------------------------------------------------------------------------------------------------------------ inter-state IGST

    [Fact]
    public async Task Purchase_InterStateGst_IsOneIgstRow_AndItsReturnReversesIt()
    {
        var w = await WorldAsync();
        var supplier = await SupplierAsync(w, "Other state", "KA");
        var purchase = await CreatedAsync(await PurchaseAsync(w, supplier, [Line(w.ProductId, w.ProductUnitId, 10m, 100m)]));

        var line = purchase.GetProperty("lines")[0];
        Assert.Equal("InterState", purchase.GetProperty("placeOfSupply").GetString());
        Assert.Equal(["Igst"], Kinds(line));
        Assert.Equal(180m, D(line, "taxAmount"));
        Assert.Equal(1180m, D(purchase, "grandTotal"));
        await AssertConsistentAsync(w, "after the inter-state purchase");

        var ret = await ReturnAsync(w, purchase, 10m);   // everything: the last return takes the exact remainder

        Assert.Equal(["Igst"], Kinds(ret.GetProperty("lines")[0]));
        Assert.Equal(180m, D(ret, "taxTotal"));
        Assert.Equal(1180m, D(ret, "total"));
        Assert.Equal("InterState", ret.GetProperty("lines")[0].GetProperty("placeOfSupply").GetString());
        await AssertConsistentAsync(w, "after the full return");
    }

    // ------------------------------------------------------------------------------------------------------------ foreign currency

    [Fact]
    public async Task Purchase_InForeignCurrency_ConvertsEveryRowToBase_KeepsTheOriginals_AndTheReturnFollows()
    {
        var w = await WorldAsync();
        Guid usd;
        await using (var db = w.Host.Database.CreateDbContext()) usd = await db.Currencies.Where(c => c.Code == "USD").Select(c => c.Id).SingleAsync();
        await w.Admin.PostAsJsonAsync("/api/v1/exchange-rates", new UpsertExchangeRateRequest(usd, 83.37m, Today), ApiHost.Json);   // may already exist for today: the resolved rate is what counts
        var rate = D(await JsonAsync(await w.Admin.GetAsync($"/api/v1/exchange-rates/resolve?currencyId={usd}&date={Today:yyyy-MM-dd}")), "rateToBase");
        var supplier = await SupplierAsync(w, "Dollar supplier", "TN", usd);

        var purchase = await CreatedAsync(await PurchaseAsync(w, supplier, [Line(w.ProductId, w.ProductUnitId, 7m, 3.45m)]));

        var line = purchase.GetProperty("lines")[0];
        Assert.Equal("USD", purchase.GetProperty("currencyCode").GetString());
        Assert.Equal(rate, D(purchase, "exchangeRateToBase"));
        Assert.Equal(["Cgst", "Sgst"], Kinds(line));
        Assert.Equal(D(line, "taxAmount"), SumRows(line, "taxAmount"));                      // line tax = sum of the converted rows
        Assert.Equal(D(line, "originalTaxAmount"), SumRows(line, "originalTaxAmount"));      // and the document-currency twins add up too
        Assert.Equal(D(line, "taxableValue") + D(line, "taxAmount"), D(line, "lineTotal"));
        Assert.Equal(D(line, "originalTaxableValue") + D(line, "originalTaxAmount"), D(line, "originalLineTotal"));
        Assert.Equal(7m * 3.45m, D(line, "originalTaxableValue"));                           // 24.15 USD before tax
        Assert.Equal(2 * Math.Round(24.15m * 0.09m, 2), D(line, "originalTaxAmount"));       // 4.34 USD: each half (2.1735) rounds to the document precision, the line is the sum of its rows
        Assert.InRange(Math.Abs(D(purchase, "originalGrandTotal") * rate - D(purchase, "grandTotal")), 0m, 0.05m);
        await AssertConsistentAsync(w, "after the foreign currency purchase");

        var ret = await ReturnAsync(w, purchase, 3m);

        Assert.Equal(SumRowsOf(ret, "taxAmount"), D(ret, "taxTotal"));
        Assert.Equal(D(ret, "taxableTotal") + D(ret, "taxTotal"), D(ret, "total"));
        Assert.NotNull(ret.GetProperty("lines")[0].GetProperty("originalTaxAmount").GetDecimal() as decimal?);
        await AssertConsistentAsync(w, "after the foreign currency return");
    }

    private static decimal SumRowsOf(JsonElement document, string field) => document.GetProperty("lines").EnumerateArray().Sum(l => SumRows(l, field));

    // ------------------------------------------------------------------------------------------------------------ bill discount and charges into the landed cost

    [Fact]
    public async Task Purchase_BillDiscountAndCharges_AreInTheTaxableValue_AndReachTheBatchAndAverageCost()
    {
        var w = await WorldAsync();
        var supplier = await SupplierAsync(w, "Landed", "TN");
        Guid productId = w.ProductId;
        decimal qohBefore, costBefore;
        await using (var db = w.Host.Database.CreateDbContext(w.CompanyId))
        {
            var item = await db.StockItems.SingleAsync(s => s.ProductId == productId && s.StockLocationId == db.PurchaseOrders.OrderBy(p => p.CreatedAt).Select(p => p.StockLocationId).First());
            (qohBefore, costBefore) = (item.QuantityOnHand, item.AverageCost);
        }

        // 10 x 100 = 1000, bill discount 100, bill charges 50: taxable 950, tax 171 at 18 percent, unit cost 95
        var purchase = await CreatedAsync(await PurchaseAsync(w, supplier, [Line(w.ProductId, w.ProductUnitId, 10m, 100m)], discountAmount: 100m, charges: 50m));

        var line = purchase.GetProperty("lines")[0];
        Assert.Equal(100m, D(line, "billDiscountShare"));
        Assert.Equal(50m, D(line, "billChargeShare"));
        Assert.Equal(950m, D(line, "taxableValue"));
        Assert.Equal(171m, D(line, "taxAmount"));
        Assert.Equal(95m, D(line, "unitCost"));
        Assert.Equal(950m, D(purchase, "taxableTotal"));
        Assert.Equal(1121m, D(purchase, "grandTotal"));
        Assert.Equal(100m, D(purchase, "billDiscountAmount"));
        Assert.Equal(50m, D(purchase, "billCharges"));
        await using (var db = w.Host.Database.CreateDbContext(w.CompanyId))
        {
            var batch = await db.PurchaseBatches.SingleAsync(b => b.PurchaseOrderLineId == line.GetProperty("id").GetGuid());
            Assert.Equal(95m, batch.UnitCost);
            Assert.Equal(10m, batch.PurchasedBaseQty);
            var item = await db.StockItems.SingleAsync(s => s.ProductId == productId && s.StockLocationId == batch.OriginStockLocationId);
            Assert.Equal(qohBefore + 10m, item.QuantityOnHand);
            var expectedAverage = (qohBefore * costBefore + 10m * 95m) / (qohBefore + 10m);
            Assert.InRange(Math.Abs(item.AverageCost - expectedAverage), 0m, 0.000001m);
        }
        await AssertConsistentAsync(w, "after the bill discount and charges purchase");

        var ret = await ReturnAsync(w, purchase, 5m);

        Assert.Equal(475m, D(ret, "taxableTotal"));       // half of the landed taxable value
        Assert.Equal(85.5m, D(ret, "taxTotal"));
        await AssertConsistentAsync(w, "after the return of a bill-discounted purchase");
    }

    // ------------------------------------------------------------------------------------------------------------ race with a new version of the profile

    private sealed record RaceProduct(Guid ProductId, Guid ProductUnitId, Guid ProfileId, Guid OpenVersionId, Guid Store, Guid[] ComponentIds);

    /// <summary>A product on its own 12 percent profile (version from 2000-01-01) that no document uses yet, with 1 unit in stock at the main store.</summary>
    private static async Task<RaceProduct> NewRaceProductAsync(BranchApiWorld w)
    {
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tax = new Tax { Id = Guid.NewGuid(), Name = "Race " + suffix, RatePercent = 12m, IsActive = true };
        var profile = await TaxFixtures.UseAsProfileAsync(db, tax, companyDefault: false, code: "R" + suffix[..6].ToUpperInvariant());
        var baseUnit = await db.ProductUnits.Where(u => u.Id == w.ProductUnitId).Select(u => u.UnitId).SingleAsync();
        var product = new Product { Id = Guid.NewGuid(), Sku = "RACE-" + suffix, Name = "Race " + suffix, BaseUnitId = baseUnit, TaxId = tax.Id };
        var unit = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = baseUnit, IsBaseUnit = true, InnerQuantity = 1m, ConversionFactorToBase = 1m };
        product.Units.Add(unit);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        var country = await db.Countries.SingleAsync(c => c.Code == "IN");
        db.TaxAssignments.Add(new TaxAssignment { Id = Guid.NewGuid(), Scope = TaxScope.Product, ProductId = product.Id, CountryId = country.Id, SalesTaxProfileId = profile.Id, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var store = await StockSeed.StoreOfAsync(db, w.MainId);
        await StockSeed.AddAsync(db, store, product.Id, 1m, 5m, w.AdminId);
        var version = await db.TaxProfileVersions.SingleAsync(v => v.TaxProfileId == profile.Id);
        var components = await db.TaxComponents.Where(c => c.CountryId == country.Id && (c.Code == "CGST" || c.Code == "SGST" || c.Code == "IGST")).OrderBy(c => c.Code).Select(c => c.Id).ToArrayAsync();
        return new RaceProduct(product.Id, unit.Id, profile.Id, version.Id, store, components);   // components: CGST, IGST, SGST
    }

    private static AddTaxVersionRequest NextVersion(RaceProduct p) => new(Today,
    [
        new TaxVersionComponentRequest(p.ComponentIds[0], Sellora.Domain.Entities.TaxApplicability.IntraState, 9m, null),   // CGST
        new TaxVersionComponentRequest(p.ComponentIds[2], Sellora.Domain.Entities.TaxApplicability.IntraState, 9m, null),   // SGST
        new TaxVersionComponentRequest(p.ComponentIds[1], Sellora.Domain.Entities.TaxApplicability.InterState, 18m, null),  // IGST
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
    public async Task Race_VersionAddHoldingTheProfile_ThePurchaseWaitsForItAndThenFailsCleanlyOn_Lines_NeverCommittingOnTheSupersededVersion()
    {
        var w = await WorldAsync();
        var p = await NewRaceProductAsync(w);
        var supplier = await SupplierAsync(w, "Race A", "TN");
        await using var gate = new NpgsqlConnection(w.Host.Database.ConnectionString);
        await gate.OpenAsync();
        await using var tx = await gate.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"""SELECT "Id" FROM "TaxProfileVersions" WHERE "Id" = '{p.OpenVersionId}' FOR UPDATE""", gate, tx)) await hold.ExecuteNonQueryAsync();

        // 1. the version add takes the profile FOR UPDATE and then waits for the gate in its UPDATE of the open version
        var versionTask = w.Admin.PostAsJsonAsync($"/api/v1/tax-profiles/{p.ProfileId}/versions", NextVersion(p), ApiHost.Json);
        Assert.True(await WaiterAsync(w.Host.Database.ConnectionString, "%UPDATE%TaxProfileVersions%", "%", TimeSpan.FromSeconds(30)), "the version add did not block on the open version");
        // 2. the purchase resolves the old version, then waits for the profile lock (FOR KEY SHARE conflicts with FOR UPDATE)
        var purchaseTask = PurchaseAsync(w, supplier, [Line(p.ProductId, p.ProductUnitId, 2m, 50m)]);
        Assert.True(await WaiterAsync(w.Host.Database.ConnectionString, "%TaxProfiles%", "%FOR KEY SHARE%", TimeSpan.FromSeconds(30)), "the purchase did not wait for the profile lock");
        Assert.False(purchaseTask.IsCompleted);
        await tx.RollbackAsync();

        var version = await versionTask;
        var purchase = await purchaseTask;

        Assert.True(version.IsSuccessStatusCode, await version.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, purchase.StatusCode);
        Assert.Contains("lines", await ErrorFieldsAsync(purchase));
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        Assert.Equal(0, await db.PurchaseOrderLines.CountAsync(l => l.ProductId == p.ProductId));
        Assert.Equal(1m, await db.StockItems.Where(s => s.ProductId == p.ProductId).SumAsync(s => s.QuantityOnHand));   // only the seeded unit
        await AssertConsistentAsync(w, "after the refused purchase");
    }

    [Fact]
    public async Task Race_PurchaseHoldingTheProfile_TheVersionAddWaitsForItsCommit_AndIsThenRefusedBecauseTheVersionIsUsedToday()
    {
        var w = await WorldAsync();
        var p = await NewRaceProductAsync(w);
        var supplier = await SupplierAsync(w, "Race B", "TN");
        await using var gate = new NpgsqlConnection(w.Host.Database.ConnectionString);
        await gate.OpenAsync();
        await using var tx = await gate.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"""SELECT 1 FROM "StockItems" WHERE "ProductId" = '{p.ProductId}' AND "StockLocationId" = '{p.Store}' FOR UPDATE""", gate, tx)) await hold.ExecuteNonQueryAsync();

        // 1. the purchase takes the profile FOR KEY SHARE (before the stock rows) and then waits for the gate on the StockItems row
        var purchaseTask = PurchaseAsync(w, supplier, [Line(p.ProductId, p.ProductUnitId, 2m, 50m)]);
        Assert.True(await WaiterAsync(w.Host.Database.ConnectionString, "%StockItems%", "%", TimeSpan.FromSeconds(30)), "the purchase did not wait on the stock row");
        // 2. the version add needs the profile FOR UPDATE: it must wait for the purchase, not overtake it
        var versionTask = w.Admin.PostAsJsonAsync($"/api/v1/tax-profiles/{p.ProfileId}/versions", NextVersion(p), ApiHost.Json);
        Assert.True(await WaiterAsync(w.Host.Database.ConnectionString, "%TaxProfiles%", "%FOR UPDATE%", TimeSpan.FromSeconds(30)), "the version add did not wait for the purchase's profile lock");
        await Task.Delay(300);
        Assert.False(versionTask.IsCompleted, "the version add finished although a purchase on the profile was still open");
        await tx.RollbackAsync();

        var purchase = await purchaseTask;
        var purchaseDone = DateTime.UtcNow;
        var version = await versionTask;

        var created = await CreatedAsync(purchase);
        var line = created.GetProperty("lines")[0];
        Assert.Equal(p.OpenVersionId, line.GetProperty("taxProfileVersionId").GetGuid());   // committed on the version that was open when it started
        Assert.Equal(12m, D(line, "taxRatePercent"));
        Assert.Equal(HttpStatusCode.BadRequest, version.StatusCode);                      // a version cannot supersede one that a document of today already uses
        Assert.Contains("effectivefrom", await ErrorFieldsAsync(version));
        await using var db = w.Host.Database.CreateDbContext(w.CompanyId);
        Assert.Equal(1, await db.TaxProfileVersions.CountAsync(v => v.TaxProfileId == p.ProfileId));
        _ = purchaseDone;
        await AssertConsistentAsync(w, "after the purchase that won the race");
    }
}
