using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Expenses;
using Sellora.Domain.Inventory;
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
    Guid SaleLineId, Guid SalesReturnId, Guid AdjustmentId, Guid ExpenseId, Guid ExchangeRateId, Guid BatchId, Guid CurrencyId, Guid BranchId, Guid LocationId, Guid WarehouseId, Guid VehicleId, Guid GovernanceMemberId,
    Guid RoleId, Guid StaffUserId, Guid AssignmentId,
    Guid CountryId, Guid TaxProfileId, Guid TaxComponentId, Guid TaxAssignmentId);

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
            var mainB = Sellora.IntegrationTests.TestData.BranchBuilder.MainBranch(company);
            dbB.Branches.Add(mainB);
            // Phase 3: B's Main store and Transit (the production migration / company creation add them; A's come from the seeder).
            dbB.StockLocations.Add(Sellora.IntegrationTests.TestData.BranchBuilder.Store(mainB));
            dbB.StockLocations.Add(Sellora.IntegrationTests.TestData.BranchBuilder.Transit(company));
            await dbB.SaveChangesAsync();

            // Phase 6: B's owner is an Owner governance member holding all five codes (A's comes from the seeder), so governance reads/writes are exercised in both companies.
            var membershipB = await dbB.UserCompanyMemberships.SingleAsync(m => m.CompanyId == company && m.User.Email == BOwnerEmail);
            var memberB = Sellora.Domain.Permissions.GovernancePermissionCatalog.MemberIdFor(membershipB.Id);
            dbB.CompanyGovernanceMembers.Add(new CompanyGovernanceMember { Id = memberB, CompanyId = company, UserCompanyMembershipId = membershipB.Id, Title = GovernanceTitle.Owner });
            foreach (var def in Sellora.Domain.Permissions.GovernancePermissionCatalog.All)
            {
                dbB.GovernanceMemberPermissions.Add(new GovernanceMemberPermission
                {
                    Id = Sellora.Domain.Permissions.GovernancePermissionCatalog.GrantIdFor(memberB, def.Code), CompanyId = company, GovernanceMemberId = memberB,
                    GovernancePermissionId = Sellora.Domain.Permissions.GovernancePermissionCatalog.IdFor(def.Code),
                });
            }
            await dbB.SaveChangesAsync();

            // Phase 7: roles are company-owned, so B has its own Administrator and its owner holds it through an assignment.
            var adminB = Sellora.IntegrationTests.TestData.RbacSeed.AddAdministratorRole(dbB);
            Sellora.IntegrationTests.TestData.RbacSeed.Assign(dbB, membershipB, adminB.Id);
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
        // TP-1: the company gets the country of A and its own standard tax profiles (TaxSeeder), and every source tax maps onto the same-named mirror tax of the new company
        // (a source tax with no seeded twin is copied as a plain tax). Product-level assignments of A are copied below.
        var sourceSettings = await source.BusinessSettings.AsNoTracking().SingleAsync();
        await TaxSeeder.SeedCompanyAsync(target, company, sourceSettings.CountryId);
        await target.SaveChangesAsync();
        var taxes = await source.Taxes.AsNoTracking().ToListAsync();
        var targetTaxes = await target.Taxes.AsNoTracking().ToListAsync();
        var taxMap = taxes.ToDictionary(t => t.Id, t => targetTaxes.FirstOrDefault(x => x.Name == t.Name)?.Id ?? Guid.NewGuid());
        target.Taxes.AddRange(taxes.Where(t => targetTaxes.All(x => x.Id != taxMap[t.Id])).Select(t => new Tax { Id = taxMap[t.Id], Name = t.Name, RatePercent = t.RatePercent, IsActive = t.IsActive }));
        target.Suppliers.AddRange((await source.Suppliers.AsNoTracking().ToListAsync()).Select(s => new Supplier
            { Id = Guid.NewGuid(), Name = s.Name, City = s.City, Gstin = s.Gstin, PaymentTerms = s.PaymentTerms, IsActive = s.IsActive, CurrencyId = s.CurrencyId }));
        target.Customers.AddRange((await source.Customers.AsNoTracking().ToListAsync()).Select(c => new Customer
            { Id = Guid.NewGuid(), Name = c.Name, Phone = c.Phone, Address = c.Address, IsActive = c.IsActive, IsWalkIn = c.IsWalkIn }));
        target.ExpenseCategories.AddRange((await source.ExpenseCategories.AsNoTracking().ToListAsync()).Select(c => new ExpenseCategory { Id = Guid.NewGuid(), Name = c.Name, IsActive = c.IsActive }));
        target.FinancialAccounts.AddRange((await source.FinancialAccounts.AsNoTracking().ToListAsync()).Select(a => new FinancialAccount
            { Id = Guid.NewGuid(), Name = a.Name, Kind = a.Kind, Reference = a.Reference, IsActive = a.IsActive, CreatedByUserId = userId }));
        var settings = await source.BusinessSettings.AsNoTracking().SingleAsync();
        target.BusinessSettings.Add(new BusinessSettings { Id = Guid.NewGuid(), BusinessName = settings.BusinessName, InvoicePrefix = settings.InvoicePrefix, BaseCurrencyId = settings.BaseCurrencyId, CountryId = settings.CountryId, StateId = settings.StateId, SellingPricesIncludeTax = settings.SellingPricesIncludeTax });
        foreach (var rate in await source.ExchangeRates.AsNoTracking().ToListAsync())
        {
            target.ExchangeRates.Add(new ExchangeRate { Id = Guid.NewGuid(), CurrencyId = rate.CurrencyId, RateToBase = rate.RateToBase, EffectiveDate = rate.EffectiveDate, CreatedByUserId = userId });
        }

        var productMap = new Dictionary<Guid, Guid>();
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
            productMap[p.Id] = copy.Id;
            target.Products.Add(copy);
        }

        // product-level tax assignments of A (a product whose rate is not the company default), mapped onto the new company's profiles by code
        var sourceProfiles = await source.TaxProfiles.AsNoTracking().ToDictionaryAsync(x => x.Id);
        var targetProfiles = await target.TaxProfiles.AsNoTracking().ToListAsync();
        foreach (var a in await source.TaxAssignments.AsNoTracking().Where(x => x.Scope == TaxScope.Product).ToListAsync())
        {
            Guid Map(Guid id) => targetProfiles.First(x => x.CountryId == a.CountryId && x.Code == sourceProfiles[id].Code).Id;
            target.TaxAssignments.Add(new TaxAssignment
            {
                Id = Guid.NewGuid(), Scope = TaxScope.Product, CountryId = a.CountryId, ProductId = productMap[a.ProductId!.Value],
                SalesTaxProfileId = Map(a.SalesTaxProfileId), PurchaseTaxProfileId = a.PurchaseTaxProfileId is { } pp ? Map(pp) : null, CreatedAt = DateTime.UtcNow,
            });
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
        // Phase 4: one company warehouse and one vehicle per company, with the SAME code / number / name in both (scoped uniques).
        var usage = new StockLocationUsageService(db);
        var warehouse = await new WarehouseService(db, usage).CreateAsync(new CreateWarehouseInput("ISO-WH", "Isolation warehouse", null), userId);
        var vehicle = await new VehicleService(db, usage).CreateAsync(new CreateVehicleInput("ISO-1", "Isolation van", VehicleType.Van, null), userId);
        // Phase 7: the company's Administrator role and assignment (roles are company-owned), and a plain member to address in role-assignment writes.
        var administratorRole = await db.Roles.Where(r => r.IsSystem).Select(r => r.Id).SingleAsync();
        var assignment = await db.UserRoleAssignments.Where(a => a.RoleId == administratorRole).Select(a => a.Id).SingleAsync();
        var countryId = await db.BusinessSettings.Select(s => s.CountryId!.Value).SingleAsync();
        var taxProfileId = await db.TaxAssignments.Where(a => a.Scope == TaxScope.Company).Select(a => a.SalesTaxProfileId).SingleAsync();
        var taxComponentId = await db.TaxComponents.Where(c => c.Code == "CGST").Select(c => c.Id).SingleAsync();
        var taxAssignmentId = await db.TaxAssignments.Where(a => a.Scope == TaxScope.Product).OrderBy(a => a.Id).Select(a => a.Id).FirstAsync();
        var staff = new User
        {
            Id = Guid.NewGuid(), FirstName = "Staff", LastName = "Member", Username = "staff" + company.ToString("N")[^4..], Email = $"staff{company.ToString("N")[^4..]}@selloramart.in",
            PasswordHash = new PasswordHasher().Hash(ApiHost.AdminPassword), Status = UserStatus.Active,
        };
        db.Users.Add(staff);
        db.UserCompanyMemberships.Add(new UserCompanyMembership { Id = Guid.NewGuid(), UserId = staff.Id, CompanyId = company });
        await db.SaveChangesAsync();
        return new Ids(company, token, userId, product.Id, baseUnit.Id, baseUnit.UnitId, supplier.Id, customer.Id, product.TaxId, category.Id, account.Id,
            purchase.Id, purchase.Lines.Single().Id, purchaseReturn.Id, sale.Id, sale.Lines.Single().Id, salesReturn.Id, adjustment.Id, expense.Id, rate.Id, batch.Id, currencyId,
            await db.Branches.Where(b => b.IsDefault).Select(b => b.Id).SingleAsync(),
            await db.StockLocations.Where(l => l.LocationType == Sellora.Domain.Inventory.StockLocationType.BranchStore).Select(l => l.Id).SingleAsync(),
            warehouse.Id, vehicle.Id, await db.CompanyGovernanceMembers.Select(m => m.Id).SingleAsync(),
            administratorRole, staff.Id, assignment, countryId, taxProfileId, taxComponentId, taxAssignmentId);
    }

    /// <summary>Every row id (as lower-case text) owned by a company: company tables plus the child tables owned through their parent.</summary>
    private static async Task<HashSet<string>> RowIdsAsync(ApiHost host, Guid company)
    {
        var tables = CompanyTables.Where(t => t != "StockItems").Append("Branches").Append("StockLocations").Append("Warehouses").Append("Vehicles")
            .Append("CompanyGovernanceMembers").Append("GovernanceMemberPermissions").Append("Roles").Append("UserRoleAssignments")
            .Append("TaxComponents").Append("TaxProfiles").Append("TaxProfileVersions").Append("TaxProfileVersionComponents").Append("TaxAssignments");
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
