using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Purchasing;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;
using static Sellora.IntegrationTests.TestData.PenScenario;
using V = Sellora.Domain.Inventory.ProductUnitConfigurationValidator;

namespace Sellora.IntegrationTests.Concurrency;

/// <summary>
/// Phase 4 lock order for purchase returns (PurchaseOrders FOR UPDATE, Products FOR KEY SHARE, StockItems FOR UPDATE,
/// PRT advisory lock) under real PostgreSQL locks, made deterministic with <see cref="LockRace"/>.
/// <list type="bullet">
/// <item>Return vs product-unit edit: a return in a Half Carton (a unit no document references yet) reads the unit's
/// factor and then waits in its INSERT behind an uncommitted return with the same number. The edit of that unit's
/// quantity must wait on the return's product KEY SHARE and, once the return has committed, be rejected as locked;
/// without the KEY SHARE the edit commits first and the return stores a line factor that disagrees with the row.</item>
/// <item>Two concurrent returns of everything that is left: exactly one succeeds.</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
public class PurchaseReturnLockConcurrencyTests(PostgresFixture postgres)
{
    private const string ProductsForUpdate = """%"Products"%FOR UPDATE%""";
    private sealed record Seed(PenScenario Pen, Guid SupplierId, Guid PurchaseId, Guid LineId);

    /// <summary>Pen with 10 Box (100 pc) bought; Box and Piece are locked by that line, Half Carton and Full Carton are not.</summary>
    private async Task<(TestDatabase Database, Seed Seed)> SeedAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var pen = await PenScenario.SeedAsync(db);
        var inr = await db.Currencies.SingleAsync(c => c.Code == "INR");
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Annapoorna Traders", CurrencyId = inr.Id };
        db.AddRange(supplier, new BusinessSettings { Id = Guid.NewGuid(), BusinessName = "Sellora Mart", InvoicePrefix = "INV", BaseCurrencyId = inr.Id });
        await db.SaveChangesAsync();
        var box = await ActiveRowAsync(db, pen.PenId, pen.Box);
        var purchase = await new PurchaseService(db, new ExchangeRateService(db)).CreatePurchaseAsync(
            new CreatePurchaseInput(supplier.Id, new DateOnly(2026, 9, 1), null, null, PaymentMethod.Cash, 0,
                [new PurchaseLineInput(pen.PenId, box.Id, 10m, 100m, 0, 0)]), pen.UserId);
        return (database, new Seed(pen, supplier.Id, purchase.Id, purchase.Lines.Single().Id));
    }

    private static Task ReturnAsync(SelloraDbContext db, Seed s, Guid productUnitId, decimal qty) =>
        new PurchaseService(db, new ExchangeRateService(db)).CreateReturnAsync(new CreatePurchaseReturnInput(s.PurchaseId, new DateOnly(2026, 9, 2), null,
            [new PurchaseReturnLineInput(s.LineId, productUnitId, qty)]), s.Pen.UserId);

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
        // A second purchase to hang the gate's return on: the gate's FK check would otherwise KEY SHARE-lock the very
        // purchase row the real return takes FOR UPDATE on, and the return would block there instead.
        Guid otherPurchaseId;
        await using (var db = database.CreateDbContext())
        {
            var pc = await ActiveRowAsync(db, s.Pen.PenId, s.Pen.Pc);
            otherPurchaseId = (await new PurchaseService(db, new ExchangeRateService(db)).CreatePurchaseAsync(
                new CreatePurchaseInput(s.SupplierId, new DateOnly(2026, 9, 1), null, null, PaymentMethod.Cash, 0,
                    [new PurchaseLineInput(s.Pen.PenId, pc.Id, 1m, 10m, 0, 0)]), s.Pen.UserId)).Id;
        }
        ProductUnitInputItem[] edit = [current[s.Pen.Pc], current[s.Pen.Box], current[s.Pen.HCtn] with { InnerQuantity = 6m }, current[s.Pen.FCtn]];

        // Gate: an uncommitted return with the number the real return will take, so the return blocks in its INSERT
        // after it has read the Half Carton factor (50).
        await using var race = await LockRace.BeginAsync(database, async gate =>
        {
            gate.PurchaseReturns.Add(new PurchaseReturn
            {
                Id = Guid.NewGuid(), BranchId = Sellora.Domain.Companies.DefaultTenant.BranchId, StockLocationId = Sellora.Domain.Companies.DefaultTenant.MainStoreLocationId, ReturnNo = "PRT-0001", PurchaseOrderId = otherPurchaseId,
                ReturnDate = new DateOnly(2026, 9, 2), CreatedByUserId = s.Pen.UserId,
            });
            await gate.SaveChangesAsync();
        });
        var ret = await race.StartAsync("return", db => ReturnAsync(db, s, hctnRowId, 1m));
        await race.WaitBlockedAsync(ret, """%INSERT INTO "PurchaseReturns"%""");
        var editor = await race.StartAsync("edit", db => s.Pen.UpdatePenAsync(db, edit));
        var editBlocked = await race.WaitBlockedOrFinishedAsync(editor, ProductsForUpdate);
        var outcomes = await race.ReleaseAsync();

        await using var check = database.CreateDbContext();
        var mismatches = await check.PurchaseReturnLines.AsNoTracking()
            .Where(l => l.ConversionFactorToBase != l.ProductUnit.ConversionFactorToBase)
            .Select(l => $"return line {l.Id}: line {l.ConversionFactorToBase} vs row {l.ProductUnit.ConversionFactorToBase}").ToListAsync();
        Assert.True(mismatches.Count == 0, "Return line factor snapshots disagree with their product unit: " + string.Join("; ", mismatches));
        Assert.True(editBlocked, "The product edit did not wait for the return holding the product's KEY SHARE lock.");
        Assert.True(outcomes[0] is null, $"return failed: {outcomes[0]}");
        var rejected = Assert.IsType<DomainValidationException>(outcomes[1]);
        Assert.Contains(rejected.Errors, e => e.Message == V.LockedMessage);
        var line = await check.PurchaseReturnLines.AsNoTracking().SingleAsync();
        Assert.Equal((50m, 50m, hctnRowId), (line.ConversionFactorToBase, line.BaseQty, line.ProductUnitId));
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

        await using var race = await LockRace.BeginAsync(database, LockRace.LockRow("PurchaseOrders", "Id", s.PurchaseId));
        var first = await race.StartAsync("return 1", db => ReturnAsync(db, s, pcRowId, 100m));
        await race.WaitBlockedAsync(first, """%"PurchaseOrders"%FOR UPDATE%""");
        var second = await race.StartAsync("return 2", db => ReturnAsync(db, s, pcRowId, 100m));
        await race.WaitBlockedAsync(second, """%"PurchaseOrders"%FOR UPDATE%""");
        var outcomes = await race.ReleaseAsync();

        Assert.Single(outcomes, o => o is null);
        var failure = Assert.IsType<DomainValidationException>(outcomes.Single(o => o is not null));
        Assert.Contains(failure.Errors, e => e.Field == "Lines" && e.Message == "Only 0 pc of Ball Pen STN-PEN can still be returned.");
        await using var check = database.CreateDbContext();
        Assert.Single(await check.PurchaseReturns.AsNoTracking().ToListAsync());
        Assert.Equal(0m, (await check.StockItems.AsNoTracking().SingleAsync()).QuantityOnHand);
        await StockInvariants.AssertHoldAsync(check);
    }
}
