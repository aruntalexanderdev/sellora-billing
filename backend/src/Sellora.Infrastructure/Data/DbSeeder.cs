using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Permissions;

namespace Sellora.Infrastructure.Data;

/// <summary>
/// Idempotent dev/seed data: the full permission catalogue, the system Administrator role, and one
/// Administrator user (an Active, company-wide member of the default company) so there's a way to sign in before any
/// other user exists. Seeding always runs with the company context set to <see cref="DefaultTenant.CompanyId"/>: demo data
/// never lands in another tenant. Everything else
/// (additional roles, users, business data) is created live through the app per the docx §13 demo scenario.
/// </summary>
public static class DbSeeder
{
    public const string SeedAdminEmail = "admin@selloramart.in";
    public const string SeedAdminPassword = "Passw0rd!23";

    public static async Task SeedAsync(SelloraDbContext db, IPasswordHasher passwordHasher, CancellationToken ct = default)
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

        var currencies = await SeedCurrenciesAsync(db, ct);
        var settings = await SeedBusinessSettingsAsync(db, currencies["INR"], ct);
        if (company.Name != settings.BusinessName)
        {
            // BusinessSettings is the company's profile (ADR-07); the company name follows it.
            company.Name = settings.BusinessName;
            await db.SaveChangesAsync(ct);
        }
        var units = await SeedUnitsAsync(db, ct);
        var taxes = await SeedTaxesAsync(db, ct);
        await SeedSuppliersAsync(db, currencies, ct);
        await SeedProductsAsync(db, units, taxes, ct);
        await SeedCustomersAsync(db, ct);
        await SeedExpenseCategoriesAsync(db, ct);
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
    }

    // Recording payments used to be covered by an edit permission, so every role holding that permission keeps
    // the ability when the dedicated one is introduced. Only runs while nobody holds the new permission, so a
    // role an administrator later revokes it from is not re-granted.
    private static async Task GrantNewPermissionToExistingHoldersAsync(SelloraDbContext db, string newCode, string existingCode, CancellationToken ct)
    {
        var permission = await db.Permissions.SingleAsync(p => p.Code == newCode, ct);
        if (await db.RolePermissions.AnyAsync(rp => rp.PermissionId == permission.Id, ct)) return;

        var roleIds = await db.RolePermissions
            .Where(rp => rp.Permission.Code == existingCode)
            .Select(rp => rp.RoleId).ToListAsync(ct);
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
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Name == "Administrator", ct);
        if (role is not null) return role;

        role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "Administrator",
            Description = "Full access to every module. System role — cannot be edited or deleted.",
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
        var hasMembership = await db.UserCompanyMemberships.AnyAsync(m => m.UserId == admin.Id && m.CompanyId == DefaultTenant.CompanyId, ct);
        if (!hasMembership)
        {
            db.UserCompanyMemberships.Add(new UserCompanyMembership
            {
                Id = Guid.NewGuid(), UserId = admin.Id, CompanyId = DefaultTenant.CompanyId,
                Status = MembershipStatus.Active, AccessScope = CompanyAccessScope.CompanyWide,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    // Phase 6 D8: the governance catalogue lives in its own table (never in Permissions). Ids are deterministic (md5, as the migration);
    // existing rows are left untouched.
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

    private static async Task<BusinessSettings> SeedBusinessSettingsAsync(SelloraDbContext db, Currency baseCurrency, CancellationToken ct)
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
        db.BusinessSettings.Add(settings);
        await db.SaveChangesAsync(ct);
        return settings;
    }

    private static readonly (string Name, string ShortCode, MeasurementCategory Category, short DecimalPlaces, decimal? StandardFactor)[] UnitSeed =
    [
        ("Kilogram", "kg", MeasurementCategory.Weight, 3, 1m),
        ("Litre", "litre", MeasurementCategory.Volume, 3, 1m),
        ("Piece", "pc", MeasurementCategory.Count, 0, null),
        ("Box", "box", MeasurementCategory.Packaging, 0, null),
        ("Half Carton", "hctn", MeasurementCategory.Packaging, 0, null),
        ("Full Carton", "fctn", MeasurementCategory.Packaging, 0, null),
        ("Gram", "g", MeasurementCategory.Weight, 0, 0.001m),
        ("Millilitre", "ml", MeasurementCategory.Volume, 0, 0.001m),
        ("Bag", "bag", MeasurementCategory.Packaging, 0, null),
    ];

    private static async Task<Dictionary<string, Unit>> SeedUnitsAsync(SelloraDbContext db, CancellationToken ct)
    {
        var existing = await db.Units.ToListAsync(ct);
        // Keyed by the seed short code so product seeds resolve even when an existing unit
        // matched on name (different code) or differs only in case.
        var bySeedCode = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, shortCode, category, decimalPlaces, standardFactor) in UnitSeed)
        {
            // Never update existing rows; skip when the code or name is already taken (case-insensitive).
            var match = existing.FirstOrDefault(u => string.Equals(u.ShortCode, shortCode, StringComparison.OrdinalIgnoreCase))
                        ?? existing.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                match = new Unit
                {
                    Id = Guid.NewGuid(), Name = name, ShortCode = shortCode,
                    MeasurementCategory = category, DecimalPlaces = decimalPlaces, StandardFactor = standardFactor,
                };
                db.Units.Add(match);
                existing.Add(match);
            }
            bySeedCode[shortCode] = match;
        }
        await db.SaveChangesAsync(ct);
        return bySeedCode;
    }

    private static readonly (string Name, decimal Rate)[] TaxSeed =
    [
        ("GST 5%", 5m),
        ("GST 12%", 12m),
        ("GST 18%", 18m),
    ];

    private static async Task<Dictionary<decimal, Tax>> SeedTaxesAsync(SelloraDbContext db, CancellationToken ct)
    {
        var existing = await db.Taxes.ToListAsync(ct);
        foreach (var (name, rate) in TaxSeed)
        {
            if (existing.Any(t => t.RatePercent == rate)) continue;
            var tax = new Tax { Id = Guid.NewGuid(), Name = name, RatePercent = rate };
            db.Taxes.Add(tax);
            existing.Add(tax);
        }
        await db.SaveChangesAsync(ct);
        return existing.ToDictionary(t => t.RatePercent);
    }

    private static readonly (string Name, string City, string? Gstin, string Terms, string CurrencyCode)[] SupplierSeed =
    [
        ("Annapoorna Traders", "Chennai", "33AABCA1234F1Z5", "Net 15", "INR"),
        ("Kaveri Agro Mills", "Tiruchirappalli", "33AAECK5678L1ZQ", "Net 30", "INR"),
        ("Sri Balaji Oils", "Coimbatore", "33AAFCS9012M1ZB", "Advance", "INR"),
        ("Madurai Spice Co.", "Madurai", "33AAGCM3456N1ZC", "Net 15", "INR"),
        ("Global Ingredients Inc.", "Chicago", null, "Net 30", "USD"),
    ];

    private static async Task SeedSuppliersAsync(SelloraDbContext db, Dictionary<string, Currency> currencies, CancellationToken ct)
    {
        var existingNames = await db.Suppliers.Select(s => s.Name).ToListAsync(ct);
        foreach (var (name, city, gstin, terms, currencyCode) in SupplierSeed)
        {
            if (existingNames.Contains(name)) continue;
            db.Suppliers.Add(new Supplier
            {
                Id = Guid.NewGuid(), Name = name, City = city, Gstin = gstin, PaymentTerms = terms,
                CurrencyId = currencies[currencyCode].Id,
            });
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
        new("SNK-MIX", "Namkeen Mixture", "Snacks", 12m, 25m, [new("pc", null, 1m)]),
        new("STN-PEN", "Ball Pen", "Stationery", 12m, 200m,
            [new("pc", null, 1m), new("box", "pc", 10m), new("hctn", "box", 5m), new("fctn", "hctn", 2m)]),
    ];

    private static async Task SeedProductsAsync(SelloraDbContext db, Dictionary<string, Unit> units, Dictionary<decimal, Tax> taxes, CancellationToken ct)
    {
        var existingSkus = await db.Products.Select(p => p.Sku).ToListAsync(ct);
        foreach (var seed in ProductSeedData)
        {
            if (existingSkus.Contains(seed.Sku)) continue;

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
                Category = seed.Category,
                BaseUnitId = units[baseSeed.UnitCode].Id,
                TaxId = taxes[seed.TaxRate].Id,
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
        }
        await db.SaveChangesAsync(ct);
    }

    private static readonly (string Name, string Phone, string Address)[] CustomerSeed =
    [
        ("Meena Kumari", "+91 98765 43210", "12 Gandhi Nagar, Chennai"),
        ("Suresh Enterprises", "+91 98407 11223", "45 Anna Nagar, Chennai"),
    ];

    private static async Task SeedCustomersAsync(SelloraDbContext db, CancellationToken ct)
    {
        if (!await db.Customers.AnyAsync(c => c.IsWalkIn, ct))
        {
            db.Customers.Add(new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true });
        }

        var existingNames = await db.Customers.Select(c => c.Name).ToListAsync(ct);
        foreach (var (name, phone, address) in CustomerSeed)
        {
            if (existingNames.Contains(name)) continue;
            db.Customers.Add(new Customer { Id = Guid.NewGuid(), Name = name, Phone = phone, Address = address });
        }
        await db.SaveChangesAsync(ct);
    }
}
