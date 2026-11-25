using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.TaxProfiles;

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>An InMemory company with India (and a few GCC countries), business settings in a chosen country and, optionally, the standard tax seed (T12).</summary>
internal sealed class TaxWorld
{
    public static readonly DateOnly Today = new(2026, 11, 24);

    public required SelloraDbContext Db { get; init; }
    public DbContextOptions<SelloraDbContext> Options { get; init; } = null!;
    public required TimeProvider Clock { get; init; }
    public required Country India { get; init; }
    public required Country Uae { get; init; }
    public required Country Oman { get; init; }
    public required Country Qatar { get; init; }
    public required BusinessSettings Settings { get; init; }
    public required Unit Piece { get; init; }

    public Guid CompanyId => TestCompany.DefaultId;
    public TaxComponentService Components => new(Db, Clock);
    public TaxProfileService Profiles => new(Db, Clock);
    public TaxAssignmentService Assignments => new(Db, Clock);
    public TaxResolver Resolver => new(Db);

    public static async Task<TaxWorld> CreateAsync(string companyCountry = "IN", bool seed = true, DateOnly? today = null)
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = TestCompany.NewDb(options);
        var clock = new FixedTimeProvider(new DateTimeOffset((today ?? Today).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero));
        Country C(string code, string name) => new() { Id = Guid.NewGuid(), Code = code, Name = name, IsSystem = true };
        var countries = new[] { C("IN", "India"), C("AE", "United Arab Emirates"), C("OM", "Oman"), C("QA", "Qatar") };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "₹" };
        var piece = new Unit { Id = Guid.NewGuid(), Name = "Piece", ShortCode = "pc", MeasurementCategory = MeasurementCategory.Count, DecimalPlaces = 0 };
        var settings = new BusinessSettings
        {
            Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id,
            CountryId = countries.Single(c => c.Code == companyCountry).Id,
        };
        db.AddRange(countries);
        db.AddRange(inr, piece, settings);
        await db.SaveChangesAsync();

        var world = new TaxWorld { Db = db, Options = options, Clock = clock, India = countries[0], Uae = countries[1], Oman = countries[2], Qatar = countries[3], Settings = settings, Piece = piece };
        if (seed)
        {
            await TaxSeeder.SeedCompanyAsync(db, world.CompanyId, settings.CountryId, clock);
            await db.SaveChangesAsync();
        }
        return world;
    }

    public Task<TaxProfile> ProfileAsync(string code, Country? country = null) =>
        Db.TaxProfiles.Include(p => p.Versions).ThenInclude(v => v.Components).SingleAsync(p => p.Code == code && (country == null || p.CountryId == country.Id));

    public Task<TaxComponent> ComponentAsync(string code, Country? country = null) =>
        Db.TaxComponents.SingleAsync(c => c.Code == code && c.CountryId == (country ?? India).Id);

    public async Task<Tax> MirrorAsync(TaxProfile profile) => await Db.Taxes.SingleAsync(t => t.Id == profile.LegacyTaxId);

    public async Task<ProductCategory> AddCategoryAsync(string code, string name)
    {
        var category = new ProductCategory { Id = Guid.NewGuid(), Code = code, Name = name, CreatedAt = DateTime.UtcNow };
        Db.ProductCategories.Add(category);
        await Db.SaveChangesAsync();
        return category;
    }

    public async Task<ProductSubcategory> AddSubcategoryAsync(ProductCategory category, string code, string name, string? hsn = null)
    {
        var sub = new ProductSubcategory { Id = Guid.NewGuid(), CategoryId = category.Id, Code = code, Name = name, HsnCode = hsn, CreatedAt = DateTime.UtcNow };
        Db.ProductSubcategories.Add(sub);
        await Db.SaveChangesAsync();
        return sub;
    }

    /// <summary>A product row as it would be after a save: the mirror tax is the default's.</summary>
    public async Task<Product> AddProductAsync(string sku, Guid? categoryId = null, Guid? subcategoryId = null, string? hsn = null, Guid? taxId = null)
    {
        var product = new Product
        {
            Id = Guid.NewGuid(), Sku = sku, Name = "Product " + sku, BaseUnitId = Piece.Id, CategoryId = categoryId, SubcategoryId = subcategoryId, HsnCode = hsn,
            TaxId = taxId ?? (await MirrorAsync(await ProfileAsync("GST-18"))).Id,
        };
        Db.Products.Add(product);
        await Db.SaveChangesAsync();
        return product;
    }

    public static AddTaxVersionInput Version(DateOnly from, params TaxVersionComponentInput[] lines) => new(from, lines);

    public static TaxVersionComponentInput Line(TaxComponent c, TaxApplicability a, decimal? rate = null, decimal? amount = null) => new(c.Id, a, rate, amount);

    /// <summary>The standard GST set for a total rate: CGST + SGST halves intra-state, IGST inter-state.</summary>
    public async Task<TaxVersionComponentInput[]> GstSetAsync(decimal rate) =>
    [
        Line(await ComponentAsync("CGST"), TaxApplicability.IntraState, rate / 2),
        Line(await ComponentAsync("SGST"), TaxApplicability.IntraState, rate / 2),
        Line(await ComponentAsync("IGST"), TaxApplicability.InterState, rate),
    ];

    public static async Task<DomainValidationException> FailsAsync(Func<Task> act) => await Assert.ThrowsAsync<DomainValidationException>(act);

    public static async Task<string> FieldOfAsync(Func<Task> act) => (await FailsAsync(act)).Field;

    public static async Task<IReadOnlyList<FieldError>> ErrorsOfAsync(Func<Task> act) => (await FailsAsync(act)).Errors;

    /// <summary>A sales line of the profile's mirror tax dated <paramref name="date"/>: what makes a version "used" (BR4).</summary>
    public async Task UseVersionOnSaleAsync(TaxProfile profile, DateOnly date)
    {
        var invoice = new SalesInvoice { Id = Guid.NewGuid(), InvoiceNo = "INV-" + Guid.NewGuid().ToString("N")[..6], InvoiceDate = date };
        var product = await AddProductAsync("U" + Guid.NewGuid().ToString("N")[..6], taxId: profile.LegacyTaxId);
        Db.SalesInvoiceLines.Add(new SalesInvoiceLine { Id = Guid.NewGuid(), SalesInvoiceId = invoice.Id, SalesInvoice = invoice, ProductId = product.Id, TaxId = profile.LegacyTaxId });
        await Db.SaveChangesAsync();
    }
}
