using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Accounts;
using Sellora.Domain.Entities;
using Sellora.Domain.Expenses;
using Sellora.Domain.Inventory;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Infrastructure;

/// <summary>
/// T3 — service isolation (design §4). Companies A (default) and B live in ONE InMemory database and are populated with identical
/// SKUs, barcodes, names and document numbers. Every service call made under A must ignore B, and every attempt to reference B's
/// supplier / customer / product / unit / tax / category / account from A must fail validation (or be "not found").
/// </summary>
public class CompanyIsolationServiceTests
{
    private static readonly Guid A = TestCompany.DefaultId;
    private static readonly Guid B = TestCompany.OtherId;

    private sealed record Tenant(
        Guid Company, Guid UserId, Guid TaxId, Guid SupplierId, Guid CustomerId, Guid ProductId, Guid LooseKgId, Guid BagId,
        Guid CategoryId, Guid AccountId, Guid ProfileId);

    private sealed record World(string Name, Guid UserId, Guid InrId, Guid EurId, Guid KgId, Guid BagUnitId, Tenant TenantA, Tenant TenantB)
    {
        public SelloraDbContext Db(Guid company) =>
            new(new DbContextOptionsBuilder<SelloraDbContext>().UseInMemoryDatabase(Name).Options, TestCompany.For(company), TestCompany.Branch);
    }

    /// <summary>Platform rows (currencies, units, a user) once; then each company gets the same master data under the same names.</summary>
    private static async Task<World> SeedAsync(bool seedA = true)
    {
        var name = Guid.NewGuid().ToString();
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var eur = new Currency { Id = Guid.NewGuid(), Code = "EUR", Name = "Euro", Symbol = "€" };
        var kg = TestUnits.Kilogram();
        var bag = TestUnits.Bag();
        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ravi", LastName = "Krishnan", Username = "admin", Email = "admin@selloramart.in",
            PasswordHash = "x", RoleId = role.Id, Role = role,
        };
        var world = new World(name, user.Id, inr.Id, eur.Id, kg.Id, bag.Id, null!, null!);
        await using (var platform = world.Db(A))
        {
            platform.AddRange(inr, eur, kg, bag, role, user);
            platform.Companies.Add(new Company { Id = A, Code = "DEFAULT", Name = "Company A" });
            platform.Companies.Add(new Company { Id = B, Code = "B", Name = "Company B" });
            await platform.SaveChangesAsync();
        }

        var tenantA = seedA ? await SeedTenantAsync(world, A) : null!;
        var tenantB = await SeedTenantAsync(world, B);
        return world with { TenantA = tenantA, TenantB = tenantB };
    }

    private static async Task<Tenant> SeedTenantAsync(World w, Guid company)
    {
        await using var db = w.Db(company);
        // Each company owns a default branch (documents resolve to it): A's is the reserved Main, B's a derived id.
        var mainBranch = BranchBuilder.MainBranch(company == A ? null : company, company == A ? null : Guid.NewGuid());
        db.Branches.Add(mainBranch);
        db.StockLocations.Add(BranchBuilder.Store(mainBranch));
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 5%", RatePercent = 5m };
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = w.InrId };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = w.InrId };
        var product = new Product
        {
            Id = Guid.NewGuid(), Sku = "RICE-BP10", Name = "Ponni Rice", BaseUnitId = w.KgId, TaxId = tax.Id, ReorderLevel = 100,
        };
        var kgUnit = await db.Units.SingleAsync(u => u.Id == w.KgId);
        var bagUnit = await db.Units.SingleAsync(u => u.Id == w.BagUnitId);
        product.BaseUnit = kgUnit;
        var loose = TestProductUnits.Base(product, "Loose kg");
        loose.Barcode = "8901234567890"; // the same barcode in both companies
        var bag = TestProductUnits.Pack(product, bagUnit, loose, 10m, "10 kg bag");
        var category = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Rent" };
        var account = new FinancialAccount { Id = Guid.NewGuid(), Name = "Till", Kind = FinancialAccountKind.Cash, CreatedByUserId = w.UserId };
        db.AddRange(tax, supplier, customer, settings, product, category, account);
        await db.SaveChangesAsync();
        db.SellingPrices.Add(new SellingPrice { Id = Guid.NewGuid(), ProductUnitId = loose.Id, Price = 65m, CreatedByUserId = w.UserId });
        await db.SaveChangesAsync();
        var profile = await TaxFixtures.UseAsCompanyDefaultAsync(db, tax); // each company: country India, GST 5% profile as the company default
        await TaxFixtures.EnablePurchasesAsync(db); // TP-3: ... and an own state, so purchases can be placed
        return new Tenant(company, w.UserId, tax.Id, supplier.Id, customer.Id, product.Id, loose.Id, bag.Id, category.Id, account.Id, profile.Id);
    }

    private static PurchaseService Purchases(SelloraDbContext db) => new(db, new ExchangeRateService(db));

    private static Task<PurchaseOrder> BuyBagsAsync(SelloraDbContext db, World w, Tenant t, decimal bags = 3m) =>
        Purchases(db).CreatePurchaseAsync(new CreatePurchaseInput(t.SupplierId, new DateOnly(2026, 9, 1), null, null, PaymentMethod.Cash, 0,
            [new PurchaseLineInput(t.ProductId, t.BagId, bags, 580m, 0, 0)]), w.UserId);

    private static Task<SalesInvoice> SellKgAsync(SelloraDbContext db, World w, Tenant t, decimal kg) =>
        ListPricedSales.For(db).CreateSaleAsync(new CreateSaleInput(t.CustomerId, new DateOnly(2026, 9, 18), PaymentMethod.Cash, 0,
            [new SaleLineInput(t.ProductId, t.LooseKgId, kg, 65m, 0)]), w.UserId);

    private static async Task<decimal> StockAsync(World w, Guid company, Guid productId)
    {
        await using var db = w.Db(company);
        return await db.StockItems.Where(s => s.ProductId == productId).Select(s => s.QuantityOnHand).SingleAsync();
    }

    // ---- products -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateProduct_SameSkuAndBarcodeExistOnlyInOtherCompany_Succeeds_ButDuplicateWithinCompanyIsRejected()
    {
        var w = await SeedAsync(seedA: false);
        await using var db = w.Db(A);
        var taxA = new Tax { Id = Guid.NewGuid(), Name = "GST 5%", RatePercent = 5m };
        db.Taxes.Add(taxA);
        await db.SaveChangesAsync();
        await TaxFixtures.UseAsCompanyDefaultAsync(db, taxA);
        var input = new UpsertProductInput(w.TenantB is null ? "x" : "RICE-BP10", "Ponni Rice", null, null, w.KgId, 0m,
            [new ProductUnitInputItem(null, w.KgId, null, 1m, "Loose kg", "8901234567890", true, true, true, true, true)]);
        var service = new ProductService(db);

        var created = await service.CreateProductAsync(input);

        Assert.Equal(A, created.CompanyId);
        Assert.Equal(1, await db.Products.CountAsync());
        Assert.Equal(2, await w.Db(B).Products.IgnoreQueryFilters([CompanyQueryFilter.Name]).CountAsync(p => p.Sku == "RICE-BP10"));
        var duplicate = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateProductAsync(input));
        Assert.Equal(nameof(UpsertProductInput.Sku), duplicate.Field);
    }

    [Fact]
    public async Task ProductService_OtherCompanyProductAndTax_AreNotFoundFromA()
    {
        var w = await SeedAsync();
        await using var db = w.Db(A);
        var service = new ProductService(db);
        // The other company's tax profile is not visible from A: it is refused like an unknown profile.
        var input = new UpsertProductInput("NEW", "New", null, null, w.KgId, 0m,
            [new ProductUnitInputItem(null, w.KgId, null, 1m, "Each", null, true, true, false, false, true)], SalesTaxProfileId: w.TenantB.ProfileId);

        var tax = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateProductAsync(input));
        var update = await service.UpdateProductAsync(w.TenantB.ProductId, input with { SalesTaxProfileId = w.TenantA.ProfileId });
        var preview = await service.PreviewUnitsAsync(w.TenantB.ProductId, w.KgId, input.Units);

        Assert.Equal("salesTaxProfileId", tax.Field);
        Assert.Null(update);
        Assert.Null(preview);
        Assert.Equal("RICE-BP10", (await w.Db(B).Products.SingleAsync()).Sku); // B untouched
    }

    // ---- purchases ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Purchase_UnderA_NumbersAndStockAreIndependentOfB()
    {
        var w = await SeedAsync();
        await using var dbA = w.Db(A);
        await using var dbB = w.Db(B);

        var a = await BuyBagsAsync(dbA, w, w.TenantA, 3m);
        var b = await BuyBagsAsync(dbB, w, w.TenantB, 1m);

        Assert.Equal(a.PurchaseNo, b.PurchaseNo); // both companies start their own series
        Assert.Equal((A, B), (a.CompanyId, b.CompanyId));
        Assert.Equal(30m, await StockAsync(w, A, w.TenantA.ProductId));
        Assert.Equal(10m, await StockAsync(w, B, w.TenantB.ProductId));
        Assert.Equal(1, await dbA.PurchaseOrders.CountAsync());
        Assert.Equal(1, await dbA.PurchaseBatches.CountAsync());
        Assert.Equal(1, await dbA.StockMovements.CountAsync());
    }

    [Fact]
    public async Task Purchase_ReferencingOtherCompanySupplierProductOrUnit_IsRejected()
    {
        var w = await SeedAsync();
        await using var db = w.Db(A);
        var service = Purchases(db);
        CreatePurchaseInput Build(Guid supplier, Guid product, Guid unit, Guid? account = null) =>
            new(supplier, new DateOnly(2026, 9, 1), null, null, PaymentMethod.Cash, account is null ? 0 : 100m, [new PurchaseLineInput(product, unit, 1m, 580m, 0, 0)], account);

        var supplier = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreatePurchaseAsync(Build(w.TenantB.SupplierId, w.TenantA.ProductId, w.TenantA.BagId), w.UserId));
        var product = await Assert.ThrowsAnyAsync<DomainValidationException>(() => service.CreatePurchaseAsync(Build(w.TenantA.SupplierId, w.TenantB.ProductId, w.TenantB.BagId), w.UserId));
        var unit = await Assert.ThrowsAnyAsync<DomainValidationException>(() => service.CreatePurchaseAsync(Build(w.TenantA.SupplierId, w.TenantA.ProductId, w.TenantB.BagId), w.UserId));
        var account = await Assert.ThrowsAnyAsync<DomainValidationException>(() => service.CreatePurchaseAsync(Build(w.TenantA.SupplierId, w.TenantA.ProductId, w.TenantA.BagId, w.TenantB.AccountId), w.UserId));

        Assert.Equal("SupplierId", supplier.Field);
        Assert.NotNull(product);
        Assert.NotNull(unit);
        Assert.NotNull(account);
        Assert.Equal(0, await db.PurchaseOrders.CountAsync());
    }

    [Fact]
    public async Task PurchaseDocuments_OfOtherCompany_AreNotFoundForPaymentCancellationAndReturn()
    {
        var w = await SeedAsync();
        await using var dbB = w.Db(B);
        var purchaseB = await BuyBagsAsync(dbB, w, w.TenantB, 2m);
        await using var dbA = w.Db(A);
        var service = Purchases(dbA);

        var header = await service.UpdateHeaderAsync(purchaseB.Id, new UpdatePurchaseHeaderInput("X", "Y"));
        var payment = await service.AddPaymentAsync(purchaseB.Id, new AddPurchasePaymentInput(10m, PaymentMethod.Cash, 0m), w.UserId);
        await Assert.ThrowsAsync<DomainValidationException>(() => service.CancelPurchaseAsync(purchaseB.Id, w.UserId));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateReturnAsync(
            new CreatePurchaseReturnInput(purchaseB.Id, new DateOnly(2026, 9, 2), null, [new PurchaseReturnLineInput(purchaseB.Lines.Single().Id, null, 1m)]), w.UserId));

        Assert.Null(header);
        Assert.Null(payment);
        await using var check = w.Db(B);
        Assert.Equal(PurchaseStatus.Completed, (await check.PurchaseOrders.SingleAsync()).Status);
        Assert.Equal(20m, await StockAsync(w, B, w.TenantB.ProductId));
    }

    // ---- sales -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sale_UnderA_ConsumesOnlyAsBatchesAndNumbersIndependentlyOfB()
    {
        var w = await SeedAsync();
        await using var dbA = w.Db(A);
        await using var dbB = w.Db(B);
        await BuyBagsAsync(dbA, w, w.TenantA, 3m);
        await BuyBagsAsync(dbB, w, w.TenantB, 3m);

        var a = await SellKgAsync(dbA, w, w.TenantA, 5m);
        var b = await SellKgAsync(dbB, w, w.TenantB, 1m);

        Assert.Equal(a.InvoiceNo, b.InvoiceNo);
        Assert.Equal(25m, await StockAsync(w, A, w.TenantA.ProductId));
        Assert.Equal(29m, await StockAsync(w, B, w.TenantB.ProductId));
        Assert.Equal(5m, await dbA.SalesInvoiceLineBatchConsumptions.SumAsync(c => c.QtyBaseUnits));
        Assert.Equal(1, await dbA.SalesInvoices.CountAsync());
    }

    [Fact]
    public async Task Sale_ReferencingOtherCompanyCustomerProductOrUnit_IsRejectedAndOtherStockUntouched()
    {
        var w = await SeedAsync();
        await using var dbB = w.Db(B);
        await BuyBagsAsync(dbB, w, w.TenantB, 3m);
        await using var dbA = w.Db(A);
        await BuyBagsAsync(dbA, w, w.TenantA, 3m);
        var service = ListPricedSales.For(dbA);
        CreateSaleInput Build(Guid customer, Guid product, Guid unit) =>
            new(customer, new DateOnly(2026, 9, 18), PaymentMethod.Cash, 0, [new SaleLineInput(product, unit, 1m, 65m, 0)]);

        var customer = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateSaleAsync(Build(w.TenantB.CustomerId, w.TenantA.ProductId, w.TenantA.LooseKgId), w.UserId));
        await Assert.ThrowsAnyAsync<DomainValidationException>(() => service.CreateSaleAsync(Build(w.TenantA.CustomerId, w.TenantB.ProductId, w.TenantB.LooseKgId), w.UserId));

        Assert.Equal("customerId", customer.Field);
        Assert.Equal(30m, await StockAsync(w, B, w.TenantB.ProductId));
        Assert.Equal(0, await dbA.SalesInvoices.CountAsync());
    }

    [Fact]
    public async Task SalesDocuments_OfOtherCompany_AreNotFoundForPaymentCancellationAndReturn()
    {
        var w = await SeedAsync();
        await using var dbB = w.Db(B);
        await BuyBagsAsync(dbB, w, w.TenantB, 3m);
        var saleB = await SellKgAsync(dbB, w, w.TenantB, 5m);
        await using var dbA = w.Db(A);
        var service = new SalesService(dbA);

        var payment = await service.AddPaymentAsync(saleB.Id, new AddSalePaymentInput(10m, PaymentMethod.Cash, 0m), w.UserId);
        await Assert.ThrowsAsync<DomainValidationException>(() => service.CancelSaleAsync(saleB.Id, w.UserId));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateReturnAsync(
            new CreateSaleReturnInput(saleB.Id, new DateOnly(2026, 9, 19), null, [new SaleReturnLineInput(saleB.Lines.Single().Id, null, 1m)]), w.UserId));

        Assert.Null(payment);
        Assert.Equal(25m, await StockAsync(w, B, w.TenantB.ProductId));
        await using var check = w.Db(B);
        Assert.Equal(SalesStatus.Completed, (await check.SalesInvoices.SingleAsync()).Status);
    }

    [Fact]
    public async Task Returns_UnderEachCompany_RestoreOnlyThatCompanysBatches()
    {
        var w = await SeedAsync();
        await using var dbA = w.Db(A);
        await using var dbB = w.Db(B);
        await BuyBagsAsync(dbA, w, w.TenantA, 3m);
        await BuyBagsAsync(dbB, w, w.TenantB, 3m);
        var saleA = await SellKgAsync(dbA, w, w.TenantA, 5m);
        await SellKgAsync(dbB, w, w.TenantB, 5m);

        var returned = await new SalesService(dbA).CreateReturnAsync(
            new CreateSaleReturnInput(saleA.Id, new DateOnly(2026, 9, 19), null, [new SaleReturnLineInput(saleA.Lines.Single().Id, null, 2m)]), w.UserId);

        Assert.Equal(A, returned.CompanyId);
        Assert.Equal(27m, await StockAsync(w, A, w.TenantA.ProductId));
        Assert.Equal(25m, await StockAsync(w, B, w.TenantB.ProductId));
    }

    // ---- adjustments & pricing --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Adjustment_UnderA_NumbersIndependentlyAndRejectsOtherCompanyProduct()
    {
        var w = await SeedAsync();
        await using var dbA = w.Db(A);
        await using var dbB = w.Db(B);
        var serviceA = new StockAdjustmentService(dbA);
        CreateAdjustmentInput Input(Tenant t) => new(t.ProductId, t.LooseKgId, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 5m, 10m, "Opening");

        var a = await serviceA.CreateAdjustmentAsync(Input(w.TenantA), w.UserId);
        var b = await new StockAdjustmentService(dbB).CreateAdjustmentAsync(Input(w.TenantB), w.UserId);
        await Assert.ThrowsAnyAsync<DomainValidationException>(() => serviceA.CreateAdjustmentAsync(Input(w.TenantB), w.UserId));

        Assert.Equal(a.AdjustmentNo, b.AdjustmentNo);
        Assert.Equal(5m, await StockAsync(w, A, w.TenantA.ProductId));
        Assert.Equal(5m, await StockAsync(w, B, w.TenantB.ProductId));
        Assert.Equal(1, await dbA.StockAdjustments.CountAsync());
    }

    [Fact]
    public async Task Pricing_SetPriceUnderA_OnlyTouchesOwnUnitAndRejectsOtherCompanyUnit()
    {
        var w = await SeedAsync();
        await using var dbA = w.Db(A);
        var service = new PricingService(dbA);

        var price = await service.SetPriceAsync(w.TenantA.LooseKgId, 70m, w.UserId);
        await Assert.ThrowsAnyAsync<Exception>(() => service.SetPriceAsync(w.TenantB.LooseKgId, 99m, w.UserId));

        Assert.Equal(70m, price.Price);
        await using var dbB = w.Db(B);
        var open = await dbB.SellingPrices.Where(p => p.EffectiveTo == null).ToListAsync();
        Assert.Equal([65m], open.Select(p => p.Price)); // B's price is untouched and still open
    }

    // ---- expenses, accounts, exchange rates -------------------------------------------------------------------------------

    [Fact]
    public async Task Expense_UnderA_RejectsOtherCompanyCategoryAndAccount_AndIsNotFoundForUpdateCancel()
    {
        var w = await SeedAsync();
        await using var dbA = w.Db(A);
        await using var dbB = w.Db(B);
        var serviceA = new ExpenseService(dbA, new ExchangeRateService(dbA));
        var expenseB = await new ExpenseService(dbB, new ExchangeRateService(dbB))
            .CreateAsync(new CreateExpenseInput(w.TenantB.CategoryId, new DateOnly(2026, 11, 1), w.InrId, 100m, PaymentMethod.Cash, null, null), w.UserId);
        var created = await serviceA.CreateAsync(new CreateExpenseInput(w.TenantA.CategoryId, new DateOnly(2026, 11, 1), w.InrId, 50m, PaymentMethod.Cash, null, null), w.UserId);

        var category = await Assert.ThrowsAsync<DomainValidationException>(() => serviceA.CreateAsync(
            new CreateExpenseInput(w.TenantB.CategoryId, new DateOnly(2026, 11, 1), w.InrId, 50m, PaymentMethod.Cash, null, null), w.UserId));
        await Assert.ThrowsAnyAsync<DomainValidationException>(() => serviceA.CreateAsync(
            new CreateExpenseInput(w.TenantA.CategoryId, new DateOnly(2026, 11, 1), w.InrId, 50m, PaymentMethod.Cash, null, null, w.TenantB.AccountId), w.UserId));
        var update = await serviceA.UpdateAsync(expenseB.Id, new UpdateExpenseInput(w.TenantA.CategoryId, "x", "y"));
        var cancel = await serviceA.CancelAsync(expenseB.Id, "no", w.UserId);

        Assert.Equal(created.ExpenseNo, expenseB.ExpenseNo);
        Assert.Equal("CategoryId", category.Field);
        Assert.Null(update);
        Assert.Null(cancel);
        Assert.Equal(ExpenseStatus.Active, (await dbB.Expenses.SingleAsync()).Status);
        Assert.Equal(1, await dbA.Expenses.CountAsync());
    }

    [Fact]
    public async Task FinancialAccounts_SameNameInBothCompanies_AndTransferUpdateAcrossCompaniesAreRejected()
    {
        var w = await SeedAsync();
        await using var dbA = w.Db(A);
        var service = new FinancialAccountService(dbA);

        var second = await service.CreateAsync(new CreateAccountInput("Bank", FinancialAccountKind.Bank, null, 0m, new DateOnly(2026, 11, 1)), w.UserId);
        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(new CreateAccountInput("Till", FinancialAccountKind.Cash, null, 0m, new DateOnly(2026, 11, 1)), w.UserId));
        await Assert.ThrowsAnyAsync<DomainValidationException>(() => service.TransferAsync(
            new TransferInput(w.TenantA.AccountId, w.TenantB.AccountId, 10m, new DateOnly(2026, 11, 1), null), w.UserId));
        var update = await service.UpdateAsync(w.TenantB.AccountId, new UpdateAccountInput("Hijacked", null));
        var reconcile = await service.ReconcileAsync(w.TenantB.AccountId, [Guid.NewGuid()], w.UserId);

        Assert.Equal(A, second.CompanyId);
        Assert.Null(update);
        Assert.Null(reconcile);
        Assert.Equal("Till", (await w.Db(B).FinancialAccounts.SingleAsync()).Name);
        Assert.Equal(2, await dbA.FinancialAccounts.CountAsync());
    }

    [Fact]
    public async Task ExchangeRates_ResolveUsesOnlyTheCurrentCompanysRates()
    {
        var w = await SeedAsync();
        await using (var dbB = w.Db(B))
        {
            dbB.ExchangeRates.Add(new ExchangeRate { Id = Guid.NewGuid(), CurrencyId = w.EurId, EffectiveDate = new DateOnly(2026, 1, 1), RateToBase = 90m, CreatedByUserId = w.UserId });
            await dbB.SaveChangesAsync();
        }
        await using var dbA = w.Db(A);
        await using var dbB2 = w.Db(B);

        var rateB = await new ExchangeRateService(dbB2).ResolveRateToBaseAsync(w.EurId, new DateOnly(2026, 6, 1));
        var rateA = await Assert.ThrowsAsync<DomainValidationException>(() => new ExchangeRateService(dbA).ResolveRateToBaseAsync(w.EurId, new DateOnly(2026, 6, 1)));

        Assert.Equal(90m, rateB);
        Assert.Equal("CurrencyId", rateA.Field);
    }
}
