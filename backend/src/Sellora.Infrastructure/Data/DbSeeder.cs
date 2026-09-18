using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;

namespace Sellora.Infrastructure.Data;

/// <summary>
/// Idempotent dev/seed data: the full permission catalogue, the system Administrator role, and one
/// Administrator user so there's a way to sign in before any other user exists. Everything else
/// (additional roles, users, business data) is created live through the app per the docx §13 demo scenario.
/// </summary>
public static class DbSeeder
{
    public const string SeedAdminEmail = "admin@selloramart.in";
    public const string SeedAdminPassword = "Passw0rd!23";

    public static async Task SeedAsync(SelloraDbContext db, IPasswordHasher passwordHasher, CancellationToken ct = default)
    {
        await SeedPermissionsAsync(db, ct);
        var adminRole = await SeedAdministratorRoleAsync(db, ct);
        await SeedAdminUserAsync(db, adminRole, passwordHasher, ct);

        await SeedBusinessSettingsAsync(db, ct);
        var units = await SeedUnitsAsync(db, ct);
        var taxes = await SeedTaxesAsync(db, ct);
        await SeedSuppliersAsync(db, ct);
        await SeedProductsAsync(db, units, taxes, ct);
        await SeedCustomersAsync(db, ct);
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
        var exists = await db.Users.AnyAsync(u => u.Email == SeedAdminEmail, ct);
        if (exists) return;

        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            FirstName = "Ravi",
            LastName = "Krishnan",
            Username = "admin",
            Email = SeedAdminEmail,
            PasswordHash = passwordHasher.Hash(SeedAdminPassword),
            RoleId = adminRole.Id,
            Status = UserStatus.Active,
        });

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedBusinessSettingsAsync(SelloraDbContext db, CancellationToken ct)
    {
        if (await db.BusinessSettings.AnyAsync(ct)) return;

        db.BusinessSettings.Add(new BusinessSettings
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
        });
        await db.SaveChangesAsync(ct);
    }

    private static readonly (string Name, string ShortCode, UnitType Type)[] UnitSeed =
    [
        ("Kilogram", "kg", UnitType.Weight),
        ("Litre", "litre", UnitType.Volume),
        ("Box", "box", UnitType.Count),
        ("Piece", "pc", UnitType.Count),
    ];

    private static async Task<Dictionary<string, Unit>> SeedUnitsAsync(SelloraDbContext db, CancellationToken ct)
    {
        var existing = await db.Units.ToListAsync(ct);
        foreach (var (name, shortCode, type) in UnitSeed)
        {
            if (existing.Any(u => u.ShortCode == shortCode)) continue;
            var unit = new Unit { Id = Guid.NewGuid(), Name = name, ShortCode = shortCode, Type = type };
            db.Units.Add(unit);
            existing.Add(unit);
        }
        await db.SaveChangesAsync(ct);
        return existing.ToDictionary(u => u.ShortCode);
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

    private static readonly (string Name, string City, string Gstin, string Terms)[] SupplierSeed =
    [
        ("Annapoorna Traders", "Chennai", "33AABCA1234F1Z5", "Net 15"),
        ("Kaveri Agro Mills", "Tiruchirappalli", "33AAECK5678L1ZQ", "Net 30"),
        ("Sri Balaji Oils", "Coimbatore", "33AAFCS9012M1ZB", "Advance"),
        ("Madurai Spice Co.", "Madurai", "33AAGCM3456N1ZC", "Net 15"),
    ];

    private static async Task SeedSuppliersAsync(SelloraDbContext db, CancellationToken ct)
    {
        var existingNames = await db.Suppliers.Select(s => s.Name).ToListAsync(ct);
        foreach (var (name, city, gstin, terms) in SupplierSeed)
        {
            if (existingNames.Contains(name)) continue;
            db.Suppliers.Add(new Supplier { Id = Guid.NewGuid(), Name = name, City = city, Gstin = gstin, PaymentTerms = terms });
        }
        await db.SaveChangesAsync(ct);
    }

    private record ProductSeedMode(string Label, decimal Factor);
    private record ProductSeed(string Sku, string Name, string Category, string UnitCode, decimal TaxRate, decimal ReorderLevel, ProductSeedMode[] Modes);

    private static readonly ProductSeed[] ProductSeedData =
    [
        new("RICE-BP10", "Ponni Rice", "Grains", "kg", 5m, 100m, [new("10 kg bag", 10m), new("Loose kg", 1m)]),
        new("ATTA-CH05", "Chakki Atta", "Grains", "kg", 5m, 60m, [new("5 kg bag", 5m), new("Loose kg", 1m)]),
        new("DAL-TUR", "Toor Dal", "Pulses", "kg", 5m, 50m, [new("1 kg pack", 1m)]),
        new("OIL-SUN", "Sunflower Oil", "Oils", "litre", 5m, 40m, [new("1 L bottle", 1m)]),
        new("SUG-01", "Sugar", "Staples", "kg", 5m, 60m, [new("1 kg pack", 1m)]),
        new("SALT-IOD", "Iodised Salt", "Staples", "kg", 5m, 30m, [new("1 kg pack", 1m)]),
        new("SPC-TUR", "Turmeric Powder", "Spices", "kg", 5m, 10m, [new("200 g box", 0.2m), new("Loose kg", 1m)]),
        new("SNK-MIX", "Namkeen Mixture", "Snacks", "box", 12m, 25m, [new("1 box", 1m)]),
    ];

    private static async Task SeedProductsAsync(SelloraDbContext db, Dictionary<string, Unit> units, Dictionary<decimal, Tax> taxes, CancellationToken ct)
    {
        var existingSkus = await db.Products.Select(p => p.Sku).ToListAsync(ct);
        foreach (var seed in ProductSeedData)
        {
            if (existingSkus.Contains(seed.Sku)) continue;

            var product = new Product
            {
                Id = Guid.NewGuid(),
                Sku = seed.Sku,
                Name = seed.Name,
                Category = seed.Category,
                BaseUnitId = units[seed.UnitCode].Id,
                TaxId = taxes[seed.TaxRate].Id,
                ReorderLevel = seed.ReorderLevel,
            };
            foreach (var mode in seed.Modes)
            {
                product.SellingModes.Add(new ProductSellingMode
                {
                    Id = Guid.NewGuid(),
                    ProductId = product.Id,
                    Label = mode.Label,
                    ConversionFactorToBase = mode.Factor,
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
