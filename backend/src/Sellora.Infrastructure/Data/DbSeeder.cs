using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Permissions;
using Sellora.Infrastructure.Services;

namespace Sellora.Infrastructure.Data;

/// <summary>
/// Idempotent dev/seed data: the full permission catalogue, the system Administrator role, and one
/// Administrator user (an Active, company-wide member of the default company) so there's a way to sign in before any
/// other user exists. Seeding always runs with the company context set to <see cref="DefaultTenant.CompanyId"/>: demo data
/// never lands in another tenant. Everything else
/// (additional roles, users, business data) is created live through the app per the docx §13 demo scenario.
/// <para>
/// The demo masters (units, taxes, suppliers, product categories, products, named customers, expense categories) are only seeded
/// while <c>seedDemoData</c> is true (configuration <c>Database:SeedDemoData</c>, default true). With false the seeder leaves a
/// database that was emptied on purpose (scripts/sql/dev-reset-fresh-setup.sql) empty; system data, the walk-in customer and the
/// Main Cash account are always seeded.
/// </para>
/// </summary>
public static class DbSeeder
{
    public const string SeedAdminEmail = "admin@selloramart.in";
    public const string SeedAdminPassword = "Passw0rd!23";

    public static async Task SeedAsync(SelloraDbContext db, IPasswordHasher passwordHasher, bool seedDemoData = true, CancellationToken ct = default)
    {
        if (db.CompanyContext.CompanyId != DefaultTenant.CompanyId)
        {
            throw new InvalidOperationException("DbSeeder must run with the company context set to the default company.");
        }

        var company = await SeedDefaultCompanyAsync(db, ct);
        await SeedDefaultBranchAsync(db, ct);
        await SeedDefaultLocationsAsync(db, ct);
        await SeedPermissionsAsync(db, ct);
        var adminRole = await SeedAdministratorRoleAsync(db, ct);
        await SeedAdminUserAsync(db, adminRole, passwordHasher, ct);
        await SeedGovernanceCatalogueAsync(db, ct);
        await SeedDefaultOwnerAsync(db, ct);

        var geo = await SeedGeographyAsync(db, ct);
        var currencies = await SeedCurrenciesAsync(db, ct);
        var settings = await SeedBusinessSettingsAsync(db, currencies["INR"], geo, ct);
        if (company.Name != settings.BusinessName)
        {
            // BusinessSettings is the company's profile (ADR-07); the company name follows it.
            company.Name = settings.BusinessName;
            await db.SaveChangesAsync(ct);
        }
        if (seedDemoData)
        {
            var units = await SeedUnitsAsync(db, ct);
            var taxes = await SeedTaxesAsync(db, settings, ct);
            await SeedSuppliersAsync(db, currencies, geo, ct);
            var categories = await SeedProductCategoriesAsync(db, ct);
            await SeedProductsAsync(db, units, taxes, categories, ct);
        }
        // The walk-in customer is system data (POS defaults to it); the named demo customers follow the switch.
        await SeedCustomersAsync(db, geo, seedDemoData, ct);
        if (seedDemoData) await SeedExpenseCategoriesAsync(db, ct);
        await SeedMainCashAccountAsync(db, ct);
    }

    // The M1 migration inserts the default company; this covers databases created without migrations (tests, InMemory).
    private static async Task<Company> SeedDefaultCompanyAsync(SelloraDbContext db, CancellationToken ct)
    {
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == DefaultTenant.CompanyId, ct);
        if (company is not null) return company;

        company = new Company { Id = DefaultTenant.CompanyId, Code = DefaultTenant.Code, Name = "Default Company" };
        db.Companies.Add(company);
        await db.SaveChangesAsync(ct);
        return company;
    }

    // Migration B1 inserts Main per company; this covers databases created without migrations (tests, InMemory).
    private static async Task SeedDefaultBranchAsync(SelloraDbContext db, CancellationToken ct)
    {
        if (await db.Branches.AnyAsync(b => b.IsDefault, ct)) return;

        db.Branches.Add(new Branch { Id = DefaultTenant.BranchId, Code = DefaultTenant.BranchCode, Name = "Main", IsDefault = true, IsActive = true });
        await db.SaveChangesAsync(ct);
    }

    // Phase 3 migration inserts the store per branch and Transit per company; this covers databases created without migrations.
    private static async Task SeedDefaultLocationsAsync(SelloraDbContext db, CancellationToken ct)
    {
        var branch = await db.Branches.SingleAsync(b => b.Id == DefaultTenant.BranchId, ct);
        if (!await db.StockLocations.AnyAsync(l => l.Id == DefaultTenant.MainStoreLocationId, ct))
        {
            db.StockLocations.Add(new StockLocation
            {
                Id = DefaultTenant.MainStoreLocationId, BranchId = branch.Id, LocationType = StockLocationType.BranchStore,
                Code = branch.Code, Name = branch.Name, IsActive = branch.IsActive
            });
        }
        if (!await db.StockLocations.AnyAsync(l => l.Id == DefaultTenant.TransitLocationId, ct))
        {
            db.StockLocations.Add(new StockLocation
            {
                Id = DefaultTenant.TransitLocationId, LocationType = StockLocationType.Transit,
                Code = DefaultTenant.TransitCode, Name = "In transit", IsActive = true
            });
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedPermissionsAsync(SelloraDbContext db, CancellationToken ct)
    {
        var existingCodes = await db.Permissions.Select(p => p.Code).ToListAsync(ct);
        var missing = PermissionCatalog.All.Where(p => !existingCodes.Contains(p.Code));

        foreach (var def in missing)
        {
            db.Permissions.Add(new Permission
            {
                Id = Guid.NewGuid(),
                Code = def.Code,
                Module = def.Module,
                Action = def.Action,
                Description = def.Description,
            });
        }

        await db.SaveChangesAsync(ct);

        await GrantNewPermissionToExistingHoldersAsync(db, "Purchase.Pay", "Purchase.Edit", ct);
        await GrantNewPermissionToExistingHoldersAsync(db, "Sales.Receive", "Sales.Create", ct);
        // Phase 7: assigning roles was part of Users.Create/Users.Edit; the migration does the same for existing databases (B4).
        await GrantNewPermissionToExistingHoldersAsync(db, PermissionCatalog.CompanyUsersAssignRoles, ["Users.Create", "Users.Edit"], ct);
        // Phase 08: managing categories was part of Inventory.Edit (free-text Category); the migration does the same for existing databases.
        await GrantNewPermissionToExistingHoldersAsync(db, "Inventory.Categories", "Inventory.Edit", ct);
        // Phase 07: funding and reversal are new; existing CashBank.Manage holders keep them (the migration does the same).
        await GrantNewPermissionToExistingHoldersAsync(db, "CashBank.Fund", "CashBank.Manage", ct);
        await GrantNewPermissionToExistingHoldersAsync(db, "CashBank.Reverse", "CashBank.Manage", ct);
        // Phase 04 (FI-D31): viewing shipments goes to dispatchers and approvers; planning and loading to dispatchers. Reservations.Release is granted to nobody (Administrator only).
        await GrantNewPermissionToExistingHoldersAsync(db, "Shipments.View", ["StockTransfers.Dispatch", "StockTransfers.Approve"], ct);
        await GrantNewPermissionToExistingHoldersAsync(db, "Shipments.Plan", "StockTransfers.Dispatch", ct);
        await GrantNewPermissionToExistingHoldersAsync(db, "Shipments.Load", "StockTransfers.Dispatch", ct);
        // Phase 06 (FI-D39): approving receipts goes to existing approvers (supervisors); the default policy is Direct, so nothing changes until a company opts in.
        await GrantNewPermissionToExistingHoldersAsync(db, "StockTransfers.ApproveReceipt", "StockTransfers.Approve", ct);
        // FI-D50: level-1 (requesting branch) approval goes to existing approvers; it only matters once a company sets a threshold.
        await GrantNewPermissionToExistingHoldersAsync(db, "StockTransfers.ApproveRequest", "StockTransfers.Approve", ct);
        // Tax profiles (TP-1): taxes sat under Masters.View/Edit; the migration grants the same way for existing databases.
        await GrantNewPermissionToExistingHoldersAsync(db, "Tax.View", ["Masters.View", "Inventory.Create", "Inventory.Edit"], ct);
        await GrantNewPermissionToExistingHoldersAsync(db, "Tax.Manage", "Masters.Edit", ct);
        await GrantNewPermissionToExistingHoldersAsync(db, "Sales.BillDiscount", "Sales.PriceOverride", ct);
        // TP-5a: the tax reports go to whoever may see the sales report (the migration does the same).
        await GrantNewPermissionToExistingHoldersAsync(db, "Reports.Tax", "Reports.Sales", ct);
    }

    // Recording payments used to be covered by an edit permission, so every role holding that permission keeps
    // the ability when the dedicated one is introduced. Only runs while nobody holds the new permission, so a
    // role an administrator later revokes it from is not re-granted.
    private static Task GrantNewPermissionToExistingHoldersAsync(SelloraDbContext db, string newCode, string existingCode, CancellationToken ct) =>
        GrantNewPermissionToExistingHoldersAsync(db, newCode, [existingCode], ct);

    private static async Task GrantNewPermissionToExistingHoldersAsync(SelloraDbContext db, string newCode, string[] existingCodes, CancellationToken ct)
    {
        var permission = await db.Permissions.SingleAsync(p => p.Code == newCode, ct);
        if (await db.RolePermissions.AnyAsync(rp => rp.PermissionId == permission.Id, ct)) return;

        var roleIds = await db.RolePermissions
            .Where(rp => existingCodes.Contains(rp.Permission.Code))
            .Select(rp => rp.RoleId).Distinct().ToListAsync(ct);
        foreach (var roleId in roleIds)
        {
            db.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionId = permission.Id });
        }
        await db.SaveChangesAsync(ct);
    }

    // A default cash account so payments have somewhere to land from the start. Added once, so an
    // administrator who renames or deactivates it is not overridden.
    private static async Task SeedMainCashAccountAsync(SelloraDbContext db, CancellationToken ct)
    {
        // Under the "Branch" filter a context granting nothing still sees company-level (BranchId NULL) accounts, and Main Cash is one.
        if (await db.FinancialAccounts.AnyAsync(ct)) return;

        var admin = await db.Users.SingleAsync(u => u.Email == SeedAdminEmail, ct);
        db.FinancialAccounts.Add(new FinancialAccount
        {
            Id = Guid.NewGuid(), Name = "Main Cash", Kind = FinancialAccountKind.Cash, CreatedByUserId = admin.Id,
        });
        await db.SaveChangesAsync(ct);
    }

    // Starter categories; added once, so an administrator who renames or deactivates one is not overridden.
    private static async Task SeedExpenseCategoriesAsync(SelloraDbContext db, CancellationToken ct)
    {
        if (await db.ExpenseCategories.AnyAsync(ct)) return;

        foreach (var name in new[] { "Rent", "Electricity", "Internet", "Salary", "Transport", "Maintenance", "Other" })
        {
            db.ExpenseCategories.Add(new ExpenseCategory { Id = Guid.NewGuid(), Name = name });
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task<Role> SeedAdministratorRoleAsync(SelloraDbContext db, CancellationToken ct)
    {
        // Company filter: the default company's own Administrator (exactly one IsSystem role per company, IX_Roles_CompanyId_System).
        var role = await db.Roles.SingleOrDefaultAsync(r => r.IsSystem, ct);
        if (role is not null) return role;

        role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "Administrator",
            Description = "Full access to every module. System role — cannot be edited or deleted.",
            ScopeType = RoleScope.Company,
            IsSystem = true,
            IsActive = true,
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);
        return role;
    }

    private static async Task SeedAdminUserAsync(SelloraDbContext db, Role adminRole, IPasswordHasher passwordHasher, CancellationToken ct)
    {
        var admin = await db.Users.SingleOrDefaultAsync(u => u.Email == SeedAdminEmail, ct);
        if (admin is null)
        {
            admin = new User
            {
                Id = Guid.NewGuid(),
                FirstName = "Ravi",
                LastName = "Krishnan",
                Username = "admin",
                Email = SeedAdminEmail,
                PasswordHash = passwordHasher.Hash(SeedAdminPassword),
                RoleId = adminRole.Id,
                Status = UserStatus.Active,
            };
            db.Users.Add(admin);
        }

        // Explicit membership, never inferred (arch 6.1): the seeded administrator can sign in to the default company.
        var membershipId = await db.UserCompanyMemberships
            .Where(m => m.UserId == admin.Id && m.CompanyId == DefaultTenant.CompanyId)
            .Select(m => (Guid?)m.Id).SingleOrDefaultAsync(ct);
        if (membershipId is null)
        {
            membershipId = Guid.NewGuid();
            db.UserCompanyMemberships.Add(new UserCompanyMembership
            {
                Id = membershipId.Value, UserId = admin.Id, CompanyId = DefaultTenant.CompanyId,
                Status = MembershipStatus.Active, AccessScope = CompanyAccessScope.CompanyWide,
            });
        }

        // Phase 7 (D3/D9): the seeded administrator holds the company's Administrator role through an assignment (Users.RoleId is deprecated).
        // Seeded once: an administrator whose assignment was later ended is not re-assigned. Id = md5('ura:' || membershipId), as the migration.
        if (!await db.UserRoleAssignments.AnyAsync(a => a.UserCompanyMembershipId == membershipId.Value, ct))
        {
            db.UserRoleAssignments.Add(new UserRoleAssignment
            {
                Id = Md5Guid("ura:" + membershipId.Value.ToString("D")), UserCompanyMembershipId = membershipId.Value, RoleId = adminRole.Id,
                ScopeType = RoleScope.Company, Status = AssignmentStatus.Active, AssignmentReason = "Seeded administrator",
            });
        }

        await db.SaveChangesAsync(ct);
    }

    // Phase 6 D8: the governance catalogue lives in its own table (never in Permissions). Ids are deterministic (md5, as the migration);
    // existing rows are left untouched.
    private static Guid Md5Guid(string text) => Guid.Parse(Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(text))));

    private static async Task SeedGovernanceCatalogueAsync(SelloraDbContext db, CancellationToken ct)
    {
        var existingCodes = await db.GovernancePermissions.Select(p => p.Code).ToListAsync(ct);
        foreach (var def in GovernancePermissionCatalog.All.Where(d => !existingCodes.Contains(d.Code)))
        {
            db.GovernancePermissions.Add(new GovernancePermission { Id = GovernancePermissionCatalog.IdFor(def.Code), Code = def.Code, Description = def.Description });
        }
        await db.SaveChangesAsync(ct);
    }

    // Covers databases created without migrations (tests, InMemory): the migration maps the earliest Administrator to Owner (owner decision O2).
    // Only while the company has no governance member at all, so an owner who later changes governance is not overridden. Platform
    // administrators are never seeded (O3: granted only by scripts/sql/grant-platform-admin.sql).
    private static async Task SeedDefaultOwnerAsync(SelloraDbContext db, CancellationToken ct)
    {
        if (await db.CompanyGovernanceMembers.AnyAsync(ct)) return;

        var membershipId = await db.UserCompanyMemberships
            .Where(m => m.CompanyId == DefaultTenant.CompanyId && m.Status == MembershipStatus.Active && m.User.Email == SeedAdminEmail)
            .Select(m => (Guid?)m.Id).SingleOrDefaultAsync(ct);
        if (membershipId is null) return;

        var memberId = GovernancePermissionCatalog.MemberIdFor(membershipId.Value);
        var member = new CompanyGovernanceMember { Id = memberId, CompanyId = DefaultTenant.CompanyId, UserCompanyMembershipId = membershipId.Value, Title = GovernanceTitle.Owner };
        db.CompanyGovernanceMembers.Add(member);
        foreach (var def in GovernancePermissionCatalog.All)
        {
            db.GovernanceMemberPermissions.Add(new GovernanceMemberPermission
            {
                Id = GovernancePermissionCatalog.GrantIdFor(memberId, def.Code), CompanyId = DefaultTenant.CompanyId, GovernanceMemberId = memberId,
                GovernancePermissionId = GovernancePermissionCatalog.IdFor(def.Code),
            });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Resolved geography for demo rows: country and state ids by ISO code.</summary>
    private sealed record GeoLookup(Dictionary<string, Country> Countries, Dictionary<(string Country, string State), State> States)
    {
        public (Guid? CountryId, Guid? StateId) Link(string countryCode, string stateCode)
            => (Countries[countryCode].Id, States[(countryCode, stateCode)].Id);
    }

    // Country/State masters (country-state-masters): migrations seed them in every migrated database with the same deterministic ids;
    // this covers InMemory/dev databases created without migrations. Lookup by code, insert only what is missing (never edits a row).
    private static async Task<GeoLookup> SeedGeographyAsync(SelloraDbContext db, CancellationToken ct)
    {
        var countries = await db.Countries.ToDictionaryAsync(c => c.Code, ct);
        foreach (var seed in GeoSeedData.Countries.Where(c => !countries.ContainsKey(c.Code)))
        {
            var country = new Country
            {
                Id = GeoSeedData.CountryId(seed.Code), Code = seed.Code, Iso3Code = seed.Iso3Code, Name = seed.Name, IsActive = true, IsSystem = true,
            };
            db.Countries.Add(country);
            countries[seed.Code] = country;
        }
        await db.SaveChangesAsync(ct);

        var byId = countries.Values.ToDictionary(c => c.Id, c => c.Code);
        var states = (await db.States.ToListAsync(ct)).ToDictionary(s => (byId[s.CountryId], s.Code));
        foreach (var seed in GeoSeedData.States.Where(s => !states.ContainsKey((s.CountryCode, s.Code))))
        {
            var state = new State
            {
                Id = GeoSeedData.StateId(seed.CountryCode, seed.Code), CountryId = countries[seed.CountryCode].Id, Code = seed.Code,
                Name = seed.Name, Kind = seed.Kind, GstStateCode = seed.GstStateCode, IsActive = true, IsSystem = true,
            };
            db.States.Add(state);
            states[(seed.CountryCode, seed.Code)] = state;
        }
        await db.SaveChangesAsync(ct);
        return new GeoLookup(countries, states);
    }

    // INR is inserted by the AddMultiCurrency migration (so it exists in every environment); this only
    // adds a demo foreign currency and rate so multi-currency purchasing is usable in a fresh dev database.
    private static async Task<Dictionary<string, Currency>> SeedCurrenciesAsync(SelloraDbContext db, CancellationToken ct)
    {
        var existing = await db.Currencies.ToListAsync(ct);
        if (!existing.Any(c => c.Code == "USD"))
        {
            var usd = new Currency { Id = Guid.NewGuid(), Code = "USD", Name = "US Dollar", Symbol = "$", DecimalPrecision = 2 };
            db.Currencies.Add(usd);
            existing.Add(usd);
        }
        await db.SaveChangesAsync(ct);

        var usdCurrency = existing.Single(c => c.Code == "USD");
        if (!await db.ExchangeRates.AnyAsync(r => r.CurrencyId == usdCurrency.Id, ct))
        {
            var admin = await db.Users.SingleAsync(u => u.Email == SeedAdminEmail, ct);
            db.ExchangeRates.Add(new ExchangeRate
            {
                Id = Guid.NewGuid(), CurrencyId = usdCurrency.Id, RateToBase = 83.20m,
                EffectiveDate = new DateOnly(2026, 9, 1), CreatedByUserId = admin.Id,
            });
            await db.SaveChangesAsync(ct);
        }

        return existing.ToDictionary(c => c.Code);
    }

    private static async Task<BusinessSettings> SeedBusinessSettingsAsync(SelloraDbContext db, Currency baseCurrency, GeoLookup geo, CancellationToken ct)
    {
        var existing = await db.BusinessSettings.SingleOrDefaultAsync(ct);
        if (existing is not null) return existing;

        var settings = new BusinessSettings
        {
            Id = Guid.NewGuid(),
            BusinessName = "Sellora Mart",
            AddressLine = "14 Anna Salai",
            City = "Chennai",
            State = "Tamil Nadu",
            Gstin = "33AABCS1429B1ZQ",
            Phone = "+91 44 4567 8900",
            Email = "contact@selloramart.in",
            InvoicePrefix = "INV",
            BaseCurrencyId = baseCurrency.Id,
        };
        // Chennai, Tamil Nadu (GSTIN 33...): link the demo company; the frozen legacy State text stays as the hint.
        (settings.CountryId, settings.StateId) = geo.Link("IN", "TN");
        db.BusinessSettings.Add(settings);
        await db.SaveChangesAsync(ct);
        return settings;
    }

    // Uqc: GST Unit Quantity Code (TP-5a). The same mapping is applied to existing rows by migration PrepareTaxHistoryBackfill.
    private static readonly (string Name, string ShortCode, MeasurementCategory Category, short DecimalPlaces, decimal? StandardFactor, string? Uqc)[] UnitSeed =
    [
        ("Kilogram", "kg", MeasurementCategory.Weight, 3, 1m, "KGS"),
        ("Litre", "litre", MeasurementCategory.Volume, 3, 1m, "LTR"),
        ("Piece", "pc", MeasurementCategory.Count, 0, null, "PCS"),
        ("Box", "box", MeasurementCategory.Packaging, 0, null, "BOX"),
        ("Half Carton", "hctn", MeasurementCategory.Packaging, 0, null, "CTN"),
        ("Full Carton", "fctn", MeasurementCategory.Packaging, 0, null, "CTN"),
        ("Gram", "g", MeasurementCategory.Weight, 0, 0.001m, "GMS"),
        ("Millilitre", "ml", MeasurementCategory.Volume, 0, 0.001m, "MLT"),
        ("Bag", "bag", MeasurementCategory.Packaging, 0, null, "BAG"),
    ];

    private static async Task<Dictionary<string, Unit>> SeedUnitsAsync(SelloraDbContext db, CancellationToken ct)
    {
        var existing = await db.Units.ToListAsync(ct);
        // Keyed by the seed short code so product seeds resolve even when an existing unit
        // matched on name (different code) or differs only in case.
        var bySeedCode = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, shortCode, category, decimalPlaces, standardFactor, uqc) in UnitSeed)
        {
            // Never update existing rows; skip when the code or name is already taken (case-insensitive).
            var match = existing.FirstOrDefault(u => string.Equals(u.ShortCode, shortCode, StringComparison.OrdinalIgnoreCase))
                        ?? existing.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                match = new Unit
                {
                    Id = Guid.NewGuid(), Name = name, ShortCode = shortCode,
                    MeasurementCategory = category, DecimalPlaces = decimalPlaces, StandardFactor = standardFactor, Uqc = uqc,
                };
                db.Units.Add(match);
                existing.Add(match);
            }
            bySeedCode[shortCode] = match;
        }
        await db.SaveChangesAsync(ct);
        return bySeedCode;
    }

    /// <summary>A demo product's tax: the standard profile of that rate and its mirror tax. <see cref="IsCompanyDefault"/>: the product inherits it, otherwise it gets a product-level assignment.</summary>
    private sealed record DemoTax(Guid ProfileId, Guid CountryId, Guid TaxId, bool IsCompanyDefault);

    private static readonly decimal[] DemoTaxRates = [5m, 18m];

    /// <summary>TP-1: the standard tax profiles of the company's country (<see cref="TaxSeeder"/>; nothing without a country), then the demo rates mapped to
    /// their GST profiles and mirror taxes. Idempotent.</summary>
    private static async Task<Dictionary<decimal, DemoTax>> SeedTaxesAsync(SelloraDbContext db, BusinessSettings settings, CancellationToken ct)
    {
        var result = new Dictionary<decimal, DemoTax>();
        if (settings.CountryId is not { } countryId) return result;

        await TaxSeeder.SeedCompanyAsync(db, settings.CompanyId, countryId, null, ct);
        await db.SaveChangesAsync(ct);

        var companyDefault = await db.TaxAssignments.Where(a => a.Scope == TaxScope.Company && a.CountryId == countryId).Select(a => a.SalesTaxProfileId).FirstOrDefaultAsync(ct);
        foreach (var rate in DemoTaxRates)
        {
            var code = "GST-" + rate.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            var profile = await db.TaxProfiles.Where(p => p.CountryId == countryId && p.Code == code).Select(p => new { p.Id, p.LegacyTaxId }).FirstOrDefaultAsync(ct);
            if (profile is not null) result[rate] = new DemoTax(profile.Id, countryId, profile.LegacyTaxId, profile.Id == companyDefault);
        }
        return result;
    }

    private static readonly (string Name, string City, string? Gstin, string Terms, string CurrencyCode)[] SupplierSeed =
    [
        ("Annapoorna Traders", "Chennai", "33AABCA1234F1Z5", "Net 15", "INR"),
        ("Kaveri Agro Mills", "Tiruchirappalli", "33AAECK5678L1ZQ", "Net 30", "INR"),
        ("Sri Balaji Oils", "Coimbatore", "33AAFCS9012M1ZB", "Advance", "INR"),
        ("Madurai Spice Co.", "Madurai", "33AAGCM3456N1ZC", "Net 15", "INR"),
        ("Global Ingredients Inc.", "Chicago", null, "Net 30", "USD"),
    ];

    private static async Task SeedSuppliersAsync(SelloraDbContext db, Dictionary<string, Currency> currencies, GeoLookup geo, CancellationToken ct)
    {
        var existingNames = await db.Suppliers.Select(s => s.Name).ToListAsync(ct);
        foreach (var (name, city, gstin, terms, currencyCode) in SupplierSeed)
        {
            if (existingNames.Contains(name)) continue;
            var supplier = new Supplier
            {
                Id = Guid.NewGuid(), Name = name, City = city, Gstin = gstin, PaymentTerms = terms,
                CurrencyId = currencies[currencyCode].Id,
            };
            // Only rows with a 33-prefixed GSTIN (Tamil Nadu) are linked; the USD supplier (Chicago) stays unlinked.
            if (gstin is not null && gstin.StartsWith("33", StringComparison.Ordinal)) (supplier.CountryId, supplier.StateId) = geo.Link("IN", "TN");
            db.Suppliers.Add(supplier);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>One unit row: <paramref name="InnerCode"/> null for the base row ("1 Bag contains 10 kg" is
    /// <c>("bag", "kg", 10m, ...)</c>).</summary>
    private record ProductSeedUnit(string UnitCode, string? InnerCode, decimal InnerQty, string? Label = null, bool Sellable = true, bool Purchasable = true);
    private record ProductSeed(string Sku, string Name, string Category, decimal TaxRate, decimal ReorderLevel, ProductSeedUnit[] Units);

    private static readonly ProductSeed[] ProductSeedData =
    [
        new("RICE-BP10", "Ponni Rice", "Grains", 5m, 100m, [new("kg", null, 1m, "Loose kg"), new("bag", "kg", 10m, "10 kg bag")]),
        new("ATTA-CH05", "Chakki Atta", "Grains", 5m, 60m, [new("kg", null, 1m, "Loose kg"), new("bag", "kg", 5m, "5 kg bag")]),
        new("DAL-TUR", "Toor Dal", "Pulses", 5m, 50m, [new("kg", null, 1m, "1 kg pack")]),
        new("OIL-SUN", "Sunflower Oil", "Oils", 5m, 40m, [new("litre", null, 1m, "1 L bottle")]),
        new("SUG-01", "Sugar", "Staples", 5m, 60m, [new("kg", null, 1m, "1 kg pack")]),
        new("SALT-IOD", "Iodised Salt", "Staples", 5m, 30m, [new("kg", null, 1m, "1 kg pack")]),
        // Gram only defines the 200 g box; it is not bought or sold on its own.
        new("SPC-TUR", "Turmeric Powder", "Spices", 5m, 10m,
            [new("kg", null, 1m, "Loose kg"), new("g", "kg", 0.001m, Sellable: false, Purchasable: false), new("box", "g", 200m, "200 g box")]),
        new("SNK-MIX", "Namkeen Mixture", "Snacks", 5m, 25m, [new("pc", null, 1m)]),
        new("STN-PEN", "Ball Pen", "Stationery", 18m, 200m,
            [new("pc", null, 1m), new("box", "pc", 10m), new("hctn", "box", 5m), new("fctn", "hctn", 2m)]),
    ];

    /// <summary>Category masters for the demo products (idempotent, per company through the query filter); an existing category is
    /// matched by case-insensitive name and never renamed or reactivated.</summary>
    private static async Task<Dictionary<string, ProductCategory>> SeedProductCategoriesAsync(SelloraDbContext db, CancellationToken ct)
    {
        var existing = await db.ProductCategories.ToListAsync(ct);
        var byName = existing.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var usedCodes = existing.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
        var sortOrder = existing.Count == 0 ? 0 : existing.Max(c => c.SortOrder);
        foreach (var name in ProductSeedData.Select(p => p.Category).Distinct().Order(StringComparer.OrdinalIgnoreCase))
        {
            if (byName.ContainsKey(name)) continue;
            var baseCode = new string(name.ToUpperInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
            if (baseCode.Length > 20) baseCode = baseCode[..20].Trim('-');
            if (baseCode.Length == 0) baseCode = "CAT";
            var code = baseCode;
            for (var n = 2; usedCodes.Contains(code); n++)
            {
                var suffix = "-" + n;
                code = baseCode[..Math.Min(baseCode.Length, 20 - suffix.Length)].Trim('-') + suffix;
            }
            usedCodes.Add(code);
            var category = new ProductCategory { Id = Guid.NewGuid(), Code = code, Name = name, SortOrder = ++sortOrder, CreatedAt = DateTime.UtcNow };
            db.ProductCategories.Add(category);
            byName[name] = category;
        }
        await db.SaveChangesAsync(ct);
        return byName;
    }

    private static async Task SeedProductsAsync(SelloraDbContext db, Dictionary<string, Unit> units, Dictionary<decimal, DemoTax> taxes,
        Dictionary<string, ProductCategory> categories, CancellationToken ct)
    {
        var existingSkus = await db.Products.Select(p => p.Sku).ToListAsync(ct);
        foreach (var seed in ProductSeedData)
        {
            if (existingSkus.Contains(seed.Sku) || !taxes.TryGetValue(seed.TaxRate, out var demoTax)) continue;

            // Never attach a seeded product to an unsuitable unit (base Packaging, any unit inactive or missing), e.g.
            // when an existing database already had a unit with the seed's code or name.
            var baseSeed = seed.Units.Single(u => u.InnerCode is null);
            if (!seed.Units.All(u => units.TryGetValue(u.UnitCode, out var unit) && unit.IsActive)
                || units[baseSeed.UnitCode].MeasurementCategory == MeasurementCategory.Packaging) continue;

            var product = new Product
            {
                Id = Guid.NewGuid(),
                Sku = seed.Sku,
                Name = seed.Name,
                Category = categories[seed.Category].Name,
                CategoryId = categories[seed.Category].Id,
                BaseUnitId = units[baseSeed.UnitCode].Id,
                TaxId = demoTax.TaxId,
                ReorderLevel = seed.ReorderLevel,
            };

            // Factors and sort order come from the same validator/graph code the product service saves with, so seeds
            // cannot drift; a seed that would be invalid against existing units (e.g. a legacy Count "box" under a kg
            // base, or kg with fewer decimals than the gram row needs) is skipped like an unsuitable base unit.
            var idByCode = seed.Units.ToDictionary(u => u.UnitCode, _ => Guid.NewGuid(), StringComparer.OrdinalIgnoreCase);
            var drafts = seed.Units.Select((u, i) => new ProductUnitDraft
            {
                Index = i,
                Id = idByCode[u.UnitCode],
                Unit = UnitInfo.From(units[u.UnitCode]),
                UnitId = units[u.UnitCode].Id,
                InnerUnitId = u.InnerCode is null ? null : units[u.InnerCode].Id,
                InnerRef = u.InnerCode is null ? null : idByCode[u.InnerCode],
                InnerQuantity = u.InnerQty,
                Label = u.Label,
                IsSellable = u.Sellable,
                IsPurchasable = u.Purchasable,
                IsActive = true,
                IsNew = true,
            }).ToList();
            var result = ProductUnitConfigurationValidator.Validate(
                new ProductUnitValidationContext(product.BaseUnitId, UnitInfo.From(units[baseSeed.UnitCode]), null, false, new HashSet<string>()),
                drafts);
            if (!result.IsValid) continue;

            foreach (var draft in drafts)
            {
                var computed = result.Row(draft.Id);
                product.Units.Add(new ProductUnit
                {
                    Id = draft.Id,
                    ProductId = product.Id,
                    UnitId = draft.UnitId,
                    InnerProductUnitId = draft.InnerRef,
                    InnerQuantity = draft.IsBaseCandidate ? 1m : draft.InnerQuantity!.Value,
                    ConversionFactorToBase = computed.Factor!.Value,
                    IsBaseUnit = draft.IsBaseCandidate,
                    Label = draft.Label,
                    IsSellable = draft.IsSellable,
                    IsPurchasable = draft.IsPurchasable,
                    SortOrder = computed.SortOrder,
                });
            }
            db.Products.Add(product);
            if (!demoTax.IsCompanyDefault)
            {
                // A rate other than the company default (GST 18%) is a product-level assignment, as the migration gives migrated products.
                db.TaxAssignments.Add(new TaxAssignment
                {
                    Id = Guid.NewGuid(), Scope = TaxScope.Product, CountryId = demoTax.CountryId, ProductId = product.Id,
                    SalesTaxProfileId = demoTax.ProfileId, PurchaseTaxProfileId = null, CreatedAt = DateTime.UtcNow,
                });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static readonly (string Name, string Phone, string Address)[] CustomerSeed =
    [
        ("Meena Kumari", "+91 98765 43210", "12 Gandhi Nagar, Chennai"),
        ("Suresh Enterprises", "+91 98407 11223", "45 Anna Nagar, Chennai"),
    ];

    private static async Task SeedCustomersAsync(SelloraDbContext db, GeoLookup geo, bool seedDemoData, CancellationToken ct)
    {
        if (!await db.Customers.AnyAsync(c => c.IsWalkIn, ct))
        {
            db.Customers.Add(new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true });
        }

        if (seedDemoData)
        {
            var existingNames = await db.Customers.Select(c => c.Name).ToListAsync(ct);
            foreach (var (name, phone, address) in CustomerSeed)
            {
                if (existingNames.Contains(name)) continue;
                // Demo customers all have Chennai addresses; the walk-in customer stays unlinked.
                var customer = new Customer { Id = Guid.NewGuid(), Name = name, Phone = phone, Address = address };
                (customer.CountryId, customer.StateId) = geo.Link("IN", "TN");
                db.Customers.Add(customer);
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
