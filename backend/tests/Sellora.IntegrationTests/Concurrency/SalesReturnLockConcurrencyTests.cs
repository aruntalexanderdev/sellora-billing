using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.PenScenario;
using V = Sellora.Domain.Inventory.ProductUnitConfigurationValidator;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// Phase 5 lock order for sales returns (SalesInvoices FOR UPDATE, Products FOR KEY SHARE, StockItems FOR UPDATE, SRT
/// advisory lock) and for list-price changes, under real PostgreSQL locks made deterministic with <see cref="LockRace"/>.
/// <list type="bullet">
/// <item>Return vs product-unit edit: a return in a Half Carton (priced by nobody, referenced by no document) reads the
/// unit's factor and then waits in its INSERT behind an uncommitted return with the same number. The edit of that unit's
/// quantity must wait on the return's product KEY SHARE and then be rejected as locked.</item>
/// <item>Two concurrent returns of everything that is left: exactly one succeeds.</item>
/// <item>Sale vs price change: a sale holding the product KEY SHARE makes SetPrice wait; the sale keeps the price it read.</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
public class SalesReturnLockConcurrencyTests(PostgresFixture postgres)
{
    private const string ProductsForUpdate = """%"Products"%FOR UPDATE%""";
    private sealed record Seed(PenScenario Pen, Guid SupplierId, Guid CustomerId, Guid SaleId, Guid LineId, Guid BoxRowId);

    /// <summary>Pen: 10 Box (100 pc) bought, 5 Box sold at 100 (Box and Piece priced and referenced; Half Carton and Full
    /// Carton neither priced nor referenced).</summary>
    private async Task<(TestDatabase Database, Seed Seed)> SeedAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var pen = await PenScenario.SeedAsync(db);
        var inr = await db.Currencies.SingleAsync(c => c.Code == "INR");
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Walk-in Customer", IsWalkIn = true };
        db.AddRange(supplier, customer);   // the settings row (with the company country) comes from PenScenario
        await db.SaveChangesAsync();
        var (box, pc) = (await ActiveRowAsync(db, pen.PenId, pen.Box), await ActiveRowAsync(db, pen.PenId, pen.Pc));
        await new PurchaseService(db, new ExchangeRateService(db)).CreatePurchaseAsync(
            new CreatePurchaseInput(supplier.Id, new DateOnly(2026, 9, 1), null, null, PaymentMethod.Cash, 0,
                [new PurchaseLineInput(pen.PenId, box.Id, 10m, 100m, 0, 0)]), pen.UserId);
        await StockScenario.EnsureListPriceAsync(db, pen.UserId, box.Id, 100m);
        await StockScenario.EnsureListPriceAsync(db, pen.UserId, pc.Id, 12m);
        var sale = await new SalesService(db).CreateSaleAsync(new CreateSaleInput(customer.Id, new DateOnly(2026, 9, 2), PaymentMethod.Cash, 0,
            [new SaleLineInput(pen.PenId, box.Id, 5m, 100m, 0)]), pen.UserId);
        return (database, new Seed(pen, supplier.Id, customer.Id, sale.Id, sale.Lines.Single().Id, box.Id));
    }

    private static Task ReturnAsync(SelloraDbContext db, Seed s, Guid productUnitId, decimal qty) =>
        new SalesService(db).CreateReturnAsync(new CreateSaleReturnInput(s.SaleId, new DateOnly(2026, 9, 3), null,
            [new SaleReturnLineInput(s.LineId, productUnitId, qty)]), s.Pen.UserId);

    [Fact]
    public async Task ReturnHoldingProductKeyShare_EditOfTheReturnUnit_WaitsAndIsRejectedNeverAFactorMismatch()
    {
        var (database, s) = await SeedAsync();
        await using var _ = database;
        Dictionary<Guid, ProductUnitInputItem> current;
        Guid hctnRowId;
        await using (var db = database.CreateDbContext())
        {
            current = await CurrentAsync(db, s.Pen.PenId);
            hctnRowId = (await ActiveRowAsync(db, s.Pen.PenId, s.Pen.HCtn)).Id;
        }
        // A second sale to hang the gate's return on: the gate's FK check would otherwise KEY SHARE-lock the very invoice row
        // the real return takes FOR UPDATE on, and the return would block there instead.
        Guid otherSaleId;
        await using (var db = database.CreateDbContext())
        {
            var pc = await ActiveRowAsync(db, s.Pen.PenId, s.Pen.Pc);
            otherSaleId = (await new SalesService(db).CreateSaleAsync(new CreateSaleInput(s.CustomerId, new DateOnly(2026, 9, 2), PaymentMethod.Cash, 0,
                [new SaleLineInput(s.Pen.PenId, pc.Id, 1m, 12m, 0)]), s.Pen.UserId)).Id;
        }
        ProductUnitInputItem[] edit = [current[s.Pen.Pc], current[s.Pen.Box], current[s.Pen.HCtn] with { InnerQuantity = 6m }, current[s.Pen.FCtn]];

        await using var race = await LockRace.BeginAsync(database, async gate =>
        {
            gate.SalesReturns.Add(new SalesReturn
            {
                Id = Guid.NewGuid(), BranchId = Sellora.Domain.Companies.DefaultTenant.BranchId, StockLocationId = Sellora.Domain.Companies.DefaultTenant.MainStoreLocationId, ReturnNo = "SRT-0001", SalesInvoiceId = otherSaleId,
                ReturnDate = new DateOnly(2026, 9, 3), CreatedByUserId = s.Pen.UserId,
            });
            await gate.SaveChangesAsync();
        });
        var ret = await race.StartAsync("return", db => ReturnAsync(db, s, hctnRowId, 1m));
        await race.WaitBlockedAsync(ret, """%INSERT INTO "SalesReturns"%""");
        var editor = await race.StartAsync("edit", db => s.Pen.UpdatePenAsync(db, edit));
        var editBlocked = await race.WaitBlockedOrFinishedAsync(editor, ProductsForUpdate);
        var outcomes = await race.ReleaseAsync();

        await using var check = database.CreateDbContext();
        var mismatches = await check.SalesReturnLines.AsNoTracking()
            .Where(l => l.ConversionFactorToBase != l.ProductUnit.ConversionFactorToBase)
            .Select(l => $"return line {l.Id}: line {l.ConversionFactorToBase} vs row {l.ProductUnit.ConversionFactorToBase}").ToListAsync();
        Assert.True(mismatches.Count == 0, "Return line factor snapshots disagree with their product unit: " + string.Join("; ", mismatches));
        Assert.True(editBlocked, "The product edit did not wait for the return holding the product's KEY SHARE lock.");
        Assert.True(outcomes[0] is null, $"return failed: {outcomes[0]}");
        var rejected = Assert.IsType<DomainValidationException>(outcomes[1]);
        Assert.Contains(rejected.Errors, e => e.Message == V.LockedMessage);
        var line = await check.SalesReturnLines.AsNoTracking().SingleAsync();
        Assert.Equal((50m, 50m, hctnRowId, "hctn"), (line.ConversionFactorToBase, line.BaseQty, line.ProductUnitId, line.UnitCode));
        Assert.Equal(50m, (await check.ProductUnits.AsNoTracking().SingleAsync(u => u.Id == hctnRowId)).ConversionFactorToBase);
        await ProductUnitValidation.AssertHoldAsync(check);
        await StockInvariants.AssertHoldAsync(check);
    }

    [Fact]
    public async Task CreateReturnAsync_TwoConcurrentReturnsOfTheWholeLine_ExactlyOneSucceeds()
    {
        var (database, s) = await SeedAsync();
        await using var _ = database;
        Guid pcRowId;
        await using (var db = database.CreateDbContext()) pcRowId = (await ActiveRowAsync(db, s.Pen.PenId, s.Pen.Pc)).Id;

        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("SalesInvoices", "Id", s.SaleId));
        var first = await race.StartAsync("return 1", db => ReturnAsync(db, s, pcRowId, 50m));
        await race.WaitBlockedAsync(first, """%"SalesInvoices"%FOR UPDATE%""");
        var second = await race.StartAsync("return 2", db => ReturnAsync(db, s, pcRowId, 50m));
        await race.WaitBlockedAsync(second, """%"SalesInvoices"%FOR UPDATE%""");
        var outcomes = await race.ReleaseAsync();

        Assert.Single(outcomes, o => o is null);
        var failure = Assert.IsType<DomainValidationException>(outcomes.Single(o => o is not null));
        Assert.Contains(failure.Errors, e => e.Field == "Lines" && e.Message == "Only 0 pc of Ball Pen STN-PEN can still be returned.");
        await using var check = database.CreateDbContext();
        Assert.Single(await check.SalesReturns.AsNoTracking().ToListAsync());
        Assert.Equal(100m, (await check.StockItems.AsNoTracking().SingleAsync()).QuantityOnHand);
        await StockInvariants.AssertHoldAsync(check);
    }

    /// <summary>The sale reads the Box list price (100) under the product KEY SHARE and waits on the StockItems gate. A manager's
    /// SetPrice (Products FOR UPDATE) must wait for it; the sale commits at 100 and snapshots ListUnitPrice 100, then the new
    /// price 130 becomes the open one. Without the KEY SHARE the price would flip underneath the sale.</summary>
    [Fact]
    public async Task SaleHoldingProductKeyShare_PriceChange_WaitsAndTheSaleKeepsThePriceItRead()
    {
        var (database, s) = await SeedAsync();
        await using var _ = database;

        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("StockItems", "ProductId", s.Pen.PenId));
        var sale = await race.StartAsync("sale", db => new SalesService(db).CreateSaleAsync(new CreateSaleInput(s.CustomerId, new DateOnly(2026, 9, 4), PaymentMethod.Cash, 0,
            [new SaleLineInput(s.Pen.PenId, s.BoxRowId, 1m, 100m, 0)]), s.Pen.UserId));
        await race.WaitBlockedAsync(sale, """%"StockItems"%FOR UPDATE%""");
        var price = await race.StartAsync("price", db => new PricingService(db).SetPriceAsync(s.BoxRowId, 130m, s.Pen.UserId));
        var priceBlocked = await race.WaitBlockedOrFinishedAsync(price, ProductsForUpdate);
        var outcomes = await race.ReleaseAsync();

        Assert.True(priceBlocked, "SetPrice did not wait for the sale holding the product's KEY SHARE lock.");
        Assert.True(outcomes[0] is null, $"sale failed: {outcomes[0]}");
        Assert.True(outcomes[1] is null, $"price change failed: {outcomes[1]}");
        await using var check = database.CreateDbContext();
        var line = await check.SalesInvoiceLines.AsNoTracking().OrderByDescending(l => l.SalesInvoice.CreatedAt).FirstAsync();
        Assert.Equal((100m, 100m, false), (line.UnitPrice, line.ListUnitPrice, line.PriceOverridden));
        Assert.Equal(130m, (await check.SellingPrices.AsNoTracking().SingleAsync(p => p.ProductUnitId == s.BoxRowId && p.EffectiveTo == null)).Price);
        await StockInvariants.AssertHoldAsync(check);
    }
}
