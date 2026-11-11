using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Purchasing;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Branches;

/// <summary>
/// InMemory company with Main (reserved id) plus a second branch BLR, the Pen/Kg masters and 100 kg of stock. <see cref="Db"/> opens a new
/// context on the same store with the requested branch / accessible set, the way the HTTP pipeline would after validating X-Branch-Id.
/// </summary>
public sealed class BranchWorld
{
    public required DbContextOptions<SelloraDbContext> Options { get; init; }
    public required Guid Main { get; init; }
    public required Guid Blr { get; init; }
    public required Guid UserId { get; init; }
    public required Guid CustomerId { get; init; }
    public required Guid SupplierId { get; init; }
    public required Guid ProductId { get; init; }
    public required Guid ProductUnitId { get; init; }
    public required Guid CurrencyId { get; init; }
    public required Guid CategoryId { get; init; }

    public static readonly DateOnly Day = new(2026, 11, 1);

    /// <summary>A context of the default company; <paramref name="requested"/> = the X-Branch-Id branch, <paramref name="allowed"/> null = every branch.</summary>
    public SelloraDbContext Db(Guid? requested = null, IReadOnlySet<Guid>? allowed = null, bool unrestricted = true, Guid? primary = null)
    {
        IBranchContext context = unrestricted && allowed is null && primary is null ? BranchContext.Unrestricted(requested) : Scoped(requested, allowed, primary);
        return new SelloraDbContext(Options, TestCompany.Context, context);
    }

    private static BranchContext Scoped(Guid? requested, IReadOnlySet<Guid>? allowed, Guid? primary = null)
    {
        var context = new BranchContext();
        context.Set(requested, allowed ?? (primary is null ? new HashSet<Guid>() : null), primary);
        return context;
    }

    public static async Task<BranchWorld> CreateAsync(string companyPrefix = "INV")
    {
        var options = new DbContextOptionsBuilder<SelloraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = TestCompany.NewDb(options); // adds Main
        var blr = BranchBuilder.Extra("BLR", "Bengaluru");
        var role = new Role { Id = Guid.NewGuid(), Name = "Administrator", IsSystem = true };
        var user = new User { Id = Guid.NewGuid(), FirstName = "R", LastName = "K", Username = "admin", Email = "a@b.c", PasswordHash = "x", RoleId = role.Id, Role = role };
        var kg = TestUnits.Kilogram();
        var tax = new Tax { Id = Guid.NewGuid(), Name = "GST 5%", RatePercent = 5m };
        var inr = new Currency { Id = Guid.NewGuid(), Code = "INR", Name = "Indian Rupee", Symbol = "R" };
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Supplier", CurrencyId = inr.Id };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Walk-in", IsWalkIn = true };
        var settings = new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = companyPrefix, BaseCurrencyId = inr.Id, Gstin = "29AAAAA0000A1Z5" };
        var category = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Rent" };
        var product = new Product { Id = Guid.NewGuid(), Sku = "RICE", Name = "Rice", BaseUnitId = kg.Id, BaseUnit = kg, TaxId = tax.Id, Tax = tax };
        var unit = TestProductUnits.Base(product, "Loose kg");
        db.StockLocations.Add(BranchBuilder.Store(blr)); // every branch owns its store (production invariant)
        db.AddRange(blr, role, user, kg, tax, inr, supplier, customer, settings, category, product);
        await db.SaveChangesAsync();

        var world = new BranchWorld
        {
            Options = options, Main = DefaultTenant.BranchId, Blr = blr.Id, UserId = user.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            ProductId = product.Id, ProductUnitId = unit.Id, CurrencyId = inr.Id, CategoryId = category.Id,
        };
        await using var stock = world.Db();
        await new PurchaseService(stock, new ExchangeRateService(stock)).CreatePurchaseAsync(
            new CreatePurchaseInput(supplier.Id, Day, null, null, PaymentMethod.Cash, 0, [new PurchaseLineInput(product.Id, unit.Id, 100m, 10m, 0, 0)]), user.Id);
        return world;
    }

    /// <summary>The branch's own store location id.</summary>
    public async Task<Guid> StoreOfAsync(Guid branchId)
    {
        await using var db = Db();
        return await db.StockLocations.Where(l => l.BranchId == branchId && l.LocationType == Sellora.Domain.Inventory.StockLocationType.BranchStore).Select(l => l.Id).SingleAsync();
    }

    /// <summary>8b: a branch sells from its OWN store. Hand-seeds <paramref name="qty"/> kg there (no document, so numbering is unchanged).</summary>
    public async Task SeedBranchStockAsync(Guid branchId, decimal qty)
    {
        var store = await StoreOfAsync(branchId);
        await using var db = Db();
        await StockSeed.AddAsync(db, store, ProductId, qty, 10m, UserId);
    }

    public Task<PurchaseOrder> PurchaseAsync(Guid? branch, decimal paid = 0, Guid? account = null)
    {
        var db = Db(branch);
        return new PurchaseService(db, new ExchangeRateService(db)).CreatePurchaseAsync(
            new CreatePurchaseInput(SupplierId, Day, null, null, PaymentMethod.Cash, paid, [new PurchaseLineInput(ProductId, ProductUnitId, 10m, 10m, 0, 0)], account), UserId);
    }

    public async Task<SalesInvoice> SaleAsync(Guid? branch, decimal paid = 0, Guid? account = null)
    {
        if (branch is { } b && b != Main)
        {
            // Exactly the 2 kg the sale takes (unless the test already stocked the store), so the store is empty again afterwards.
            await using var probe = Db();
            var store = await StoreOfAsync(b);
            if (!await probe.StockItems.AnyAsync(i => i.StockLocationId == store && i.QuantityOnHand >= 2m)) await SeedBranchStockAsync(b, 2m);
        }
        var db = Db(branch);
        return await ListPricedSales.For(db).CreateSaleAsync(
            new Sellora.Domain.Sales.CreateSaleInput(CustomerId, Day, PaymentMethod.Cash, paid, [new Sellora.Domain.Sales.SaleLineInput(ProductId, ProductUnitId, 2m, 20m, 0)], account), UserId);
    }
}
