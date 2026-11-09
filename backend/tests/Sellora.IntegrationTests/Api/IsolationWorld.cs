using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Expenses;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Security;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>Ids of one company's data used to address the API: masters, one document of every kind, and a user to log in as.</summary>
public sealed record Ids(
    Guid Company, string Token, Guid UserId, Guid ProductId, Guid ProductUnitId, Guid BaseUnitId, Guid SupplierId, Guid CustomerId,
    Guid TaxId, Guid CategoryId, Guid AccountId, Guid PurchaseId, Guid PurchaseLineId, Guid PurchaseReturnId, Guid SaleId,
    Guid SaleLineId, Guid SalesReturnId, Guid AdjustmentId, Guid ExpenseId, Guid ExchangeRateId, Guid BatchId, Guid CurrencyId, Guid BranchId);

public static class IdsExtensions
{
    public static Guid BaseUnitProductUnit(this Ids ids) => ids.ProductUnitId;
}

/// <summary>
/// Company A (the seeded default company) and company B populated with the SAME business keys: B's masters are copies of A's
/// (same SKU, barcode, tax / category / account / supplier / customer names), and every document kind is created in each company
/// through the real services so both carry identical document numbers (PUR-0001, INV-0001, …).
/// </summary>
public sealed class IsolationWorld
{
    public const string BOwnerEmail = "bowner@selloramart.in";

    public required Ids A { get; init; }
    public required Ids B { get; init; }
    public required HashSet<string> RowIdsOfA { get; init; }
    public required HashSet<string> RowIdsOfB { get; init; }
    public required ApiHost Host { get; init; }

    public static async Task<IsolationWorld> BuildAsync(PostgresFixture postgres)
    {
        var host = await ApiHost.StartAsync(postgres);
        var company = Guid.Parse(OtherCompany);
        Guid adminId, roleId;
        await using (var db = host.Database.CreateDbContext())
        {
            adminId = await db.Users.Where(u => u.Email == ApiHost.AdminEmail).Select(u => u.Id).SingleAsync();
            roleId = await db.Roles.Where(r => r.IsSystem).Select(r => r.Id).SingleAsync();
            db.Companies.Add(new Company { Id = company, Code = "B", Name = "Company B" });
            var bowner = new User
            {
                Id = Guid.NewGuid(), FirstName = "B", LastName = "Owner", Username = "bowner", Email = BOwnerEmail,
                PasswordHash = new PasswordHasher().Hash(ApiHost.AdminPassword), RoleId = roleId, Status = UserStatus.Active,
            };
            db.Users.Add(bowner);
            db.UserCompanyMemberships.Add(new UserCompanyMembership { Id = Guid.NewGuid(), UserId = bowner.Id, CompanyId = company });
            await db.SaveChangesAsync();
        }

        await using (var dbB = host.Database.CreateDbContext(company))
        {
            // Every company owns a default branch (B1 gives migrated companies theirs); added under B's own context.
            dbB.Branches.Add(Sellora.IntegrationTests.TestData.BranchBuilder.Main(company));
            await dbB.SaveChangesAsync();
        }

        await CloneMastersAsync(host, company, adminId);
        var bUser = await host.Database.CreateDbContext().Users.Where(u => u.Email == BOwnerEmail).Select(u => u.Id).SingleAsync();
        var a = await PopulateAsync(host, Guid.Parse(DefaultCompany), adminId, (await host.LoginAsync()).AccessToken);
        var b = await PopulateAsync(host, company, bUser, (await host.LoginAsync(BOwnerEmail)).AccessToken);
        // The same barcode on both companies' pen base unit (scoped unique index).
        await ExecuteAsync(host.Database, $"""UPDATE "ProductUnits" SET "Barcode" = '{IsolationCases.SharedBarcode}' WHERE "Id" IN ('{a.BaseUnitProductUnit()}', '{b.BaseUnitProductUnit()}')""");
        return new IsolationWorld { A = a, B = b, Host = host, RowIdsOfA = await RowIdsAsync(host, a.Company), RowIdsOfB = await RowIdsAsync(host, b.Company) };
    }

    private static async Task CloneMastersAsync(ApiHost host, Guid company, Guid userId)
    {
        await using var source = host.Database.CreateDbContext();
        await using var target = host.Database.CreateDbContext(company);
        var taxes = await source.Taxes.AsNoTracking().ToListAsync();
        var taxMap = taxes.ToDictionary(t => t.Id, _ => Guid.NewGuid());
        target.Taxes.AddRange(taxes.Select(t => new Tax { Id = taxMap[t.Id], Name = t.Name, RatePercent = t.RatePercent, IsActive = t.IsActive }));
        target.Suppliers.AddRange((await source.Suppliers.AsNoTracking().ToListAsync()).Select(s => new Supplier
            { Id = Guid.NewGuid(), Name = s.Name, City = s.City, Gstin = s.Gstin, PaymentTerms = s.PaymentTerms, IsActive = s.IsActive, CurrencyId = s.CurrencyId }));
        target.Customers.AddRange((await source.Customers.AsNoTracking().ToListAsync()).Select(c => new Customer
            { Id = Guid.NewGuid(), Name = c.Name, Phone = c.Phone, Address = c.Address, IsActive = c.IsActive, IsWalkIn = c.IsWalkIn }));
        target.ExpenseCategories.AddRange((await source.ExpenseCategories.AsNoTracking().ToListAsync()).Select(c => new ExpenseCategory { Id = Guid.NewGuid(), Name = c.Name, IsActive = c.IsActive }));
        target.FinancialAccounts.AddRange((await source.FinancialAccounts.AsNoTracking().ToListAsync()).Select(a => new FinancialAccount
            { Id = Guid.NewGuid(), Name = a.Name, Kind = a.Kind, Reference = a.Reference, IsActive = a.IsActive, CreatedByUserId = userId }));
        var settings = await source.BusinessSettings.AsNoTracking().SingleAsync();
        target.BusinessSettings.Add(new BusinessSettings { Id = Guid.NewGuid(), BusinessName = settings.BusinessName, InvoicePrefix = settings.InvoicePrefix, BaseCurrencyId = settings.BaseCurrencyId });
        foreach (var rate in await source.ExchangeRates.AsNoTracking().ToListAsync())
        {
            target.ExchangeRates.Add(new ExchangeRate { Id = Guid.NewGuid(), CurrencyId = rate.CurrencyId, RateToBase = rate.RateToBase, EffectiveDate = rate.EffectiveDate, CreatedByUserId = userId });
        }

        foreach (var p in await source.Products.AsNoTracking().Include(p => p.Units).ToListAsync())
        {
            var unitMap = p.Units.ToDictionary(u => u.Id, _ => Guid.NewGuid());
            var copy = new Product { Id = Guid.NewGuid(), Sku = p.Sku, Name = p.Name, Category = p.Category, BaseUnitId = p.BaseUnitId, TaxId = taxMap[p.TaxId], ReorderLevel = p.ReorderLevel, IsActive = p.IsActive };
            foreach (var u in p.Units)
            {
                copy.Units.Add(new ProductUnit
                {
                    Id = unitMap[u.Id], ProductId = copy.Id, UnitId = u.UnitId, InnerProductUnitId = u.InnerProductUnitId is { } i ? unitMap[i] : null,
                    InnerQuantity = u.InnerQuantity, ConversionFactorToBase = u.ConversionFactorToBase, IsBaseUnit = u.IsBaseUnit, Label = u.Label,
                    Barcode = u.Barcode, IsPurchasable = u.IsPurchasable, IsSellable = u.IsSellable, IsDefaultPurchaseUnit = u.IsDefaultPurchaseUnit,
                    IsDefaultSalesUnit = u.IsDefaultSalesUnit, SortOrder = u.SortOrder, IsActive = u.IsActive,
                });
            }
            target.Products.Add(copy);
        }
        await target.SaveChangesAsync();
    }

    private static async Task<Ids> PopulateAsync(ApiHost host, Guid company, Guid userId, string token)
    {
        await using var db = host.Database.CreateDbContext(company);
        var product = await db.Products.Include(p => p.Units).SingleAsync(p => p.Sku == "STN-PEN");
        var baseUnit = product.Units.Single(u => u.IsBaseUnit);
        var supplier = await db.Suppliers.Where(s => s.Currency.Code == "INR").OrderBy(s => s.Name).FirstAsync();
        var customer = await db.Customers.OrderBy(c => c.Name).FirstAsync();
        var category = await db.ExpenseCategories.OrderBy(c => c.Name).FirstAsync();
        var account = await db.FinancialAccounts.OrderBy(a => a.Name).FirstAsync();
        var currencyId = await db.BusinessSettings.Select(s => s.BaseCurrencyId).SingleAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await new PricingService(db).SetPriceAsync(baseUnit.Id, 10m, userId);
        var purchases = new PurchaseService(db, new ExchangeRateService(db));
        var purchase = await purchases.CreatePurchaseAsync(new CreatePurchaseInput(supplier.Id, today, null, null, PaymentMethod.Cash, 100m,
            [new PurchaseLineInput(product.Id, baseUnit.Id, 100m, 5m, 0, 0)], account.Id), userId);
        var sales = new SalesService(db);
        var sale = await sales.CreateSaleAsync(new CreateSaleInput(customer.Id, today, PaymentMethod.Cash, 0m,
            [new SaleLineInput(product.Id, baseUnit.Id, 10m, 10m, 0)]), userId);
        var salesReturn = await sales.CreateReturnAsync(new CreateSaleReturnInput(sale.Id, today, null, [new SaleReturnLineInput(sale.Lines.Single().Id, null, 2m)]), userId);
        var purchaseReturn = await purchases.CreateReturnAsync(new CreatePurchaseReturnInput(purchase.Id, today, null, [new PurchaseReturnLineInput(purchase.Lines.Single().Id, null, 5m)]), userId);
        var adjustment = await new StockAdjustmentService(db).CreateAdjustmentAsync(
            new CreateAdjustmentInput(product.Id, baseUnit.Id, AdjustmentType.Correction, AdjustmentDirection.Increase, 3m, 5m, "isolation"), userId);
        var expense = await new ExpenseService(db, new ExchangeRateService(db)).CreateAsync(new CreateExpenseInput(category.Id, today, currencyId, 100m, PaymentMethod.Cash, null, null), userId);
        var rate = await db.ExchangeRates.OrderBy(r => r.EffectiveDate).FirstAsync();
        var batch = await db.PurchaseBatches.FirstAsync();
        return new Ids(company, token, userId, product.Id, baseUnit.Id, baseUnit.UnitId, supplier.Id, customer.Id, product.TaxId, category.Id, account.Id,
            purchase.Id, purchase.Lines.Single().Id, purchaseReturn.Id, sale.Id, sale.Lines.Single().Id, salesReturn.Id, adjustment.Id, expense.Id, rate.Id, batch.Id, currencyId,
            await db.Branches.Where(b => b.IsDefault).Select(b => b.Id).SingleAsync());
    }

    /// <summary>Every row id (as lower-case text) owned by a company: company tables plus the child tables owned through their parent.</summary>
    private static async Task<HashSet<string>> RowIdsAsync(ApiHost host, Guid company)
    {
        var tables = CompanyTables.Where(t => t != "StockItems").Append("Branches");
        var parts = tables.Select(t => $"""SELECT "Id"::text FROM "{t}" WHERE "CompanyId" = '{company}'""").Concat(
        [
            $"""SELECT l."Id"::text FROM "PurchaseOrderLines" l JOIN "PurchaseOrders" p ON p."Id" = l."PurchaseOrderId" WHERE p."CompanyId" = '{company}'""",
            $"""SELECT x."Id"::text FROM "PurchasePayments" x JOIN "PurchaseOrders" p ON p."Id" = x."PurchaseOrderId" WHERE p."CompanyId" = '{company}'""",
            $"""SELECT l."Id"::text FROM "PurchaseReturnLines" l JOIN "PurchaseReturns" p ON p."Id" = l."PurchaseReturnId" WHERE p."CompanyId" = '{company}'""",
            $"""SELECT l."Id"::text FROM "SalesInvoiceLines" l JOIN "SalesInvoices" p ON p."Id" = l."SalesInvoiceId" WHERE p."CompanyId" = '{company}'""",
            $"""SELECT x."Id"::text FROM "SalesPayments" x JOIN "SalesInvoices" p ON p."Id" = x."SalesInvoiceId" WHERE p."CompanyId" = '{company}'""",
            $"""SELECT l."Id"::text FROM "SalesReturnLines" l JOIN "SalesReturns" p ON p."Id" = l."SalesReturnId" WHERE p."CompanyId" = '{company}'""",
            $"""SELECT c."Id"::text FROM "SalesInvoiceLineBatchConsumptions" c JOIN "PurchaseBatches" b ON b."Id" = c."PurchaseBatchId" WHERE b."CompanyId" = '{company}'""",
            $"""SELECT s."Id"::text FROM "SellingPrices" s JOIN "ProductUnits" u ON u."Id" = s."ProductUnitId" WHERE u."CompanyId" = '{company}'""",
        ]);
        var ids = await QueryAsync(host.Database, string.Join(" UNION ALL ", parts), r => r.GetString(0).ToLowerInvariant());
        return ids.ToHashSet();
    }
}
