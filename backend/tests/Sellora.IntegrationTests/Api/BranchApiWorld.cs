using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// One real API host with the seeded company (admin logged in), the seeded Pen product, a stocked and priced base unit, and helpers that talk
/// to the API the way the Angular app does (optional <c>X-Branch-Id</c> header). Each test makes its own branches (unique codes) so counters
/// start at 1 for them; the default branch is shared, so tests only assert its number shape.
/// </summary>
public sealed class BranchApiWorld
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static BranchApiWorld? _instance;
    private static int _codeSeed = 100;

    public required ApiHost Host { get; init; }
    public required HttpClient Admin { get; init; }
    public required Guid AdminId { get; init; }
    public required Guid CompanyId { get; init; }
    public required Guid MainId { get; init; }
    public required Guid ProductId { get; init; }
    public required Guid ProductUnitId { get; init; }
    public required Guid SupplierId { get; init; }
    public required Guid CustomerId { get; init; }
    public required Guid CategoryId { get; init; }
    public required Guid CurrencyId { get; init; }
    public required string CompanyPrefix { get; init; }

    public static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    public const string CompanyGstin = "29AAAAA0000A1Z5";

    public static async Task<BranchApiWorld> GetAsync(PostgresFixture postgres)
    {
        await Gate.WaitAsync();
        try { return _instance ??= await BuildAsync(postgres); }
        finally { Gate.Release(); }
    }

    /// <summary>A world of its own (a fresh database + host), for tests whose data must not touch the shared world's global reconciliation checks or company settings.</summary>
    public static Task<BranchApiWorld> CreateDedicatedAsync(PostgresFixture postgres) => BuildAsync(postgres);

    private static async Task<BranchApiWorld> BuildAsync(PostgresFixture postgres)
    {
        var host = await ApiHost.StartAsync(postgres);
        var admin = await host.LoginClientAsync();
        await using var db = host.Database.CreateDbContext();
        var product = await db.Products.Include(p => p.Units).SingleAsync(p => p.Sku == "STN-PEN");
        var unit = product.Units.Single(u => u.IsBaseUnit);
        var settings = await db.BusinessSettings.SingleAsync();
        var world = new BranchApiWorld
        {
            Host = host, Admin = admin, AdminId = await db.Users.Where(u => u.Email == ApiHost.AdminEmail).Select(u => u.Id).SingleAsync(),
            CompanyId = settings.CompanyId, MainId = await db.Branches.Where(b => b.IsDefault).Select(b => b.Id).SingleAsync(),
            ProductId = product.Id, ProductUnitId = unit.Id,
            SupplierId = await db.Suppliers.Where(s => s.Currency.Code == "INR").OrderBy(s => s.Name).Select(s => s.Id).FirstAsync(),
            CustomerId = await db.Customers.OrderBy(c => c.Name).Select(c => c.Id).FirstAsync(),
            CategoryId = await db.ExpenseCategories.OrderBy(c => c.Name).Select(c => c.Id).FirstAsync(),
            CurrencyId = settings.BaseCurrencyId, CompanyPrefix = settings.InvoicePrefix,
        };
        Check(await admin.PutAsJsonAsync($"/api/v1/pricing/{unit.Id}", new SetPriceRequest(20m), ApiHost.Json), "set price");
        Check(await world.SendAsync(HttpMethod.Put, "/api/v1/settings", new UpdateBusinessSettingsRequest(settings.BusinessName, settings.AddressLine, settings.City, settings.State, CompanyGstin, settings.Phone, settings.Email, settings.InvoicePrefix, settings.BaseCurrencyId,
            SellingPricesIncludeTax: false)), "set GSTIN and the price basis");   // TP-4 (T18): a company created after TP-1 defaults to tax-inclusive shelf prices; these fixtures sell tax-exclusive (the pre-TP-4 behaviour)
        Check(await world.PurchaseAsync(null, qty: 1000m), "seed stock");
        return world;
    }

    public static void Check(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{what}: {(int)response.StatusCode} {response.Content.ReadAsStringAsync().Result}");
    }

    // ---- requests -----------------------------------------------------------------------------------------------------------

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, object? body = null, Guid? branch = null, HttpClient? client = null, string? rawBranchHeader = null)
    {
        using var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: ApiHost.Json);
        if (rawBranchHeader is not null) request.Headers.TryAddWithoutValidation("X-Branch-Id", rawBranchHeader);
        else if (branch is { } id) request.Headers.Add("X-Branch-Id", id.ToString());
        return await (client ?? Admin).SendAsync(request);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    /// <summary>Creates an active branch with a fresh code (and optionally an own invoice prefix / GSTIN); returns its id and code.</summary>
    public async Task<(Guid Id, string Code)> NewBranchAsync(string? invoicePrefix = null, string? gstin = null)
    {
        var code = "T" + Interlocked.Increment(ref _codeSeed);
        var response = await SendAsync(HttpMethod.Post, "/api/v1/branches", new CreateBranchRequest(code, "Test " + code, null, null, null, null, null, gstin, invoicePrefix));
        Check(response, "create branch " + code);
        return ((await JsonAsync(response)).GetProperty("id").GetGuid(), code);
    }

    public Task<HttpResponseMessage> PurchaseAsync(Guid? branch, decimal qty = 1m, decimal paid = 0m, Guid? account = null) =>
        SendAsync(HttpMethod.Post, "/api/v1/purchases", new CreatePurchaseRequest(SupplierId, Today, null, null, PaymentMethod.Cash, paid,
            [new CreatePurchaseLineRequest(ProductId, ProductUnitId, qty, 5m, 0, 0)], account), branch);

    /// <summary>8b: a branch sells from its OWN store. Hand-seeds exactly <paramref name="qty"/> there when it holds less (no document, so numbering is unchanged; the store is empty again after the sale).</summary>
    public async Task EnsureBranchStockAsync(Guid? branch, decimal qty)
    {
        if (branch is not { } id || id == MainId) return;
        await using var db = Host.Database.CreateDbContext(CompanyId);
        var store = await StockSeed.StoreOfAsync(db, id);
        var have = await db.StockItems.Where(s => s.StockLocationId == store && s.ProductId == ProductId).Select(s => s.QuantityOnHand).SingleOrDefaultAsync();
        if (have < qty) await StockSeed.AddAsync(db, store, ProductId, qty - have, 5m, AdminId);
    }

    public async Task<HttpResponseMessage> SaleAsync(Guid? branch, decimal qty = 1m, decimal paid = 0m, Guid? account = null)
    {
        await EnsureBranchStockAsync(branch, qty);
        return await SendAsync(HttpMethod.Post, "/api/v1/sales", new CreateSaleRequest(CustomerId, Today, PaymentMethod.Cash, paid,
            [new CreateSaleLineRequest(ProductId, ProductUnitId, qty, 20m, 0)], account), branch);
    }

    public Task<HttpResponseMessage> AdjustmentAsync(Guid? branch) =>
        SendAsync(HttpMethod.Post, "/api/v1/stock-adjustments", new CreateStockAdjustmentRequest(ProductId, ProductUnitId, AdjustmentType.Correction, AdjustmentDirection.Increase, 1m, 5m, "branch test"), branch);

    public Task<HttpResponseMessage> ExpenseAsync(Guid? tag = null) =>
        SendAsync(HttpMethod.Post, "/api/v1/expenses", new CreateExpenseRequest(CategoryId, Today, CurrencyId, 10m, PaymentMethod.Cash, null, null, null, tag));

    public async Task<Guid> NewAccountAsync(string name, Guid? branch)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/financial-accounts", new CreateFinancialAccountRequest(name, FinancialAccountKind.Cash, null, 0m, null, branch));
        Check(response, "create account " + name);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    public static async Task<List<string>> ErrorFieldsAsync(HttpResponseMessage response)
    {
        var json = await JsonAsync(response);
        return json.TryGetProperty("errors", out var errors) ? errors.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList() : [];
    }
}
