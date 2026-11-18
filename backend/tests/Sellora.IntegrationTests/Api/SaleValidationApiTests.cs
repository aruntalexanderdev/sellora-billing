using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.BranchApiWorld;

namespace Sellora.IntegrationTests.Api;

/// <summary>FI-D24 over real HTTP: invalid sale lines are 400 with the field mapped, and the invoice totals equal the sum of the rounded line totals.</summary>
[Collection(PostgresCollection.Name)]
public class SaleValidationApiTests(PostgresFixture postgres)
{
    private static Task<HttpResponseMessage> PostSaleAsync(BranchApiWorld w, Guid productId, Guid unitId, decimal qty, decimal price, decimal discount, bool overridePrice = false) =>
        w.SendAsync(HttpMethod.Post, "/api/v1/sales", new CreateSaleRequest(w.CustomerId, Today, Sellora.Domain.Entities.PaymentMethod.Cash, 0m,
            [new CreateSaleLineRequest(productId, unitId, qty, price, discount, overridePrice)]));

    [Theory]
    [InlineData(0, 20, 0, "lines")]       // quantity 0 (range check on the request)
    [InlineData(1, -1, 0, "lines")]       // negative price
    [InlineData(1, 20, 25, "lines")]      // discount greater than the line amount (20 x 1)
    [InlineData(1, 20, -1, "lines")]      // negative discount
    public async Task CreateSale_InvalidLine_Is400WithTheFieldMapped(int qty, int price, int discount, string field)
    {
        var w = await GetAsync(postgres);
        var response = await PostSaleAsync(w, w.ProductId, w.ProductUnitId, qty, price, discount, overridePrice: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await ErrorFieldsAsync(response), new FieldContains());
    }

    private sealed class FieldContains : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => x is not null && y is not null && x.Contains(y, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(string obj) => 0;
    }

    [Fact]
    public async Task CreateSale_ThreeAtThreePointThreeThreeFiveWith18PercentTax_TotalsEqualTheSumOfRoundedLineTotals()
    {
        var w = await GetAsync(postgres);
        Guid productId, unitId;
        await using (var db = w.Host.Database.CreateDbContext(w.CompanyId))
        {
            var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 18% " + Guid.NewGuid().ToString("N")[..6], RatePercent = 18m };
            db.Taxes.Add(tax);
            await db.SaveChangesAsync();
            var baseUnit = await db.Units.SingleAsync(u => u.Id == db.ProductUnits.Single(p => p.ProductId == w.ProductId && p.IsBaseUnit).UnitId);
            var created = await new ProductService(db).CreateProductAsync(new UpsertProductInput("RND-" + Guid.NewGuid().ToString("N")[..6], "Rounding Pen", null, null, baseUnit.Id, tax.Id, 0m,
                [new ProductUnitInputItem(null, baseUnit.Id, null, 1m, null, null, true, true, false, false, true)]));
            productId = created.Id;
            unitId = await db.ProductUnits.Where(p => p.ProductId == productId && p.IsBaseUnit).Select(p => p.Id).SingleAsync();
        }
        Check(await w.Admin.PutAsJsonAsync($"/api/v1/pricing/{unitId}", new SetPriceRequest(3.335m), ApiHost.Json), "set price");
        Check(await w.SendAsync(HttpMethod.Post, "/api/v1/purchases", new CreatePurchaseRequest(w.SupplierId, Today, null, null, Sellora.Domain.Entities.PaymentMethod.Cash, 0m,
            [new CreatePurchaseLineRequest(productId, unitId, 10m, 1m, 0, 0)], null)), "stock");

        var response = await PostSaleAsync(w, productId, unitId, 3m, 3.335m, 0m);
        Check(response, "sale");
        var sale = await JsonAsync(response);

        // 3 x 3.335 = 10.005 -> 10.01 (away from zero); tax 18% of 10.01 = 1.8018 -> 1.80; line total 11.81.
        var line = sale.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal((10.01m, 1.80m, 11.81m), (line.GetProperty("taxableValue").GetDecimal(), line.GetProperty("taxAmount").GetDecimal(), line.GetProperty("lineTotal").GetDecimal()));
        Assert.Equal(11.81m, sale.GetProperty("grandTotal").GetDecimal());
        Assert.Equal(sale.GetProperty("lines").EnumerateArray().Sum(l => l.GetProperty("lineTotal").GetDecimal()), sale.GetProperty("grandTotal").GetDecimal());
        Assert.Equal(sale.GetProperty("subtotal").GetDecimal() - sale.GetProperty("discountTotal").GetDecimal() + sale.GetProperty("taxTotal").GetDecimal(), sale.GetProperty("grandTotal").GetDecimal());
    }
}
