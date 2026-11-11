using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.Branches;

namespace Sellora.Tests.TestData;

/// <summary>
/// Phase 9 InMemory fixture on top of <see cref="LocationWorld"/>: the Pen (Piece base, Box = 10 pc, Half Carton = 5 Box = 50 pc,
/// Full Carton = 2 Half = 100 pc, analysis 13.4) and a Flour product whose "Pouch" is 0.3335 kg (a base quantity that is NOT storable at 3 dp,
/// built directly: the product service would not need to, the transfer service must refuse it). Stock is hand-seeded (<see cref="StockSeed"/>);
/// every call opens its own context like a separate request.
/// </summary>
public sealed class TransferWorld
{
    public static readonly string[] AllCodes =
    [
        "StockTransfers.View", "StockTransfers.Create", "StockTransfers.Approve", "StockTransfers.Dispatch",
        "StockTransfers.Receive", "StockTransfers.Cancel", "StockTransfers.Resolve",
    ];

    public required LocationWorld L { get; init; }
    public required Guid PenId { get; init; }
    public required Guid Pc { get; init; }
    public required Guid Box { get; init; }
    public required Guid Half { get; init; }
    public required Guid Full { get; init; }
    public required Guid FlourId { get; init; }
    public required Guid FlourKg { get; init; }
    public required Guid FlourPouch { get; init; }

    public BranchWorld W => L.W;
    public Guid Main => L.W.Main;
    public Guid Blr => L.W.Blr;

    /// <summary>Every transfer code held company-wide (the happy-path caller; also the company-level bypass).</summary>
    public static IEffectivePermissionService Everything() => LocationWorld.Company(AllCodes);

    public static async Task<TransferWorld> CreateAsync()
    {
        var lw = await LocationWorld.CreateAsync();
        await using var db = lw.W.Db();
        var (pcUnit, boxUnit, halfUnit, fullUnit) = (TestUnits.Piece(), TestUnits.Box(), TestUnits.HalfCarton(), TestUnits.FullCarton());
        var tax = await db.Taxes.FirstAsync();
        var pen = new ProductBuilder().WithSku("PEN-T").WithName("Pen").WithBaseUnit(pcUnit).WithTax(tax).WithUnits(p =>
        {
            var pc = TestProductUnits.Base(p);
            var box = TestProductUnits.Pack(p, boxUnit, pc, 10m);
            var half = TestProductUnits.Pack(p, halfUnit, box, 5m);
            TestProductUnits.Pack(p, fullUnit, half, 2m);
        }).Build();

        var (kg, pouchUnit) = (TestUnits.Kilogram(), TestUnits.New("Pouch", "pch", MeasurementCategory.Packaging, 0, null));
        var flour = new ProductBuilder().WithSku("FLOUR-T").WithName("Flour").WithBaseUnit(kg).WithTax(tax).WithUnits(p =>
        {
            var b = TestProductUnits.Base(p, "Loose kg");
            TestProductUnits.Pack(p, pouchUnit, b, 0.3335m);
        }).Build();
        db.AddRange(pen, flour);
        await db.SaveChangesAsync();

        ProductUnit Row(Product p, string code) => p.Units.Single(u => u.Unit.ShortCode == code);
        return new TransferWorld
        {
            L = lw, PenId = pen.Id, Pc = Row(pen, "pc").Id, Box = Row(pen, "box").Id, Half = Row(pen, "hctn").Id, Full = Row(pen, "fctn").Id,
            FlourId = flour.Id, FlourKg = Row(flour, "kg").Id, FlourPouch = Row(flour, "pch").Id,
        };
    }

    public SelloraDbContext Db(Guid? branch = null, IReadOnlySet<Guid>? allowed = null) => L.W.Db(branch, allowed);

    public static StockTransferLineInput Line(Guid productId, Guid productUnitId, decimal qty) => new(productId, productUnitId, qty);

    public StockTransferLineInput Pen(Guid unit, decimal qty) => new(PenId, unit, qty);

    /// <summary>Hand-seeds stock (one batch) of the Pen at a location.</summary>
    public async Task StockPenAsync(Guid location, decimal qty, decimal cost = 5m)
    {
        await using var db = Db();
        await StockSeed.AddAsync(db, location, PenId, qty, cost, W.UserId);
    }

    public async Task StockAsync(Guid location, Guid productId, decimal qty, decimal cost)
    {
        await using var db = Db();
        await StockSeed.AddAsync(db, location, productId, qty, cost, W.UserId);
    }

    // ---- operations: each on a fresh context of the given working branch ----------------------------------------------------------

    public async Task<StockTransfer> CreateTransferAsync(Guid source, Guid destination, StockTransferLineInput[] lines, bool submit = false, Guid? branch = null,
        IEffectivePermissionService? permissions = null, string? key = null)
    {
        await using var db = Db(branch ?? Main);
        return await new StockTransferService(db, permissions ?? Everything()).CreateAsync(new CreateStockTransferInput(source, destination, "t", submit, lines, key), W.UserId);
    }

    public async Task<StockTransfer> RequestedAsync(Guid source, Guid destination, params StockTransferLineInput[] lines) =>
        await CreateTransferAsync(source, destination, lines, submit: true);

    public async Task<StockTransfer> ApprovedAsync(Guid source, Guid destination, params StockTransferLineInput[] lines)
    {
        var t = await RequestedAsync(source, destination, lines);
        return (await ApproveAsync(t.Id))!;
    }

    public async Task<StockTransfer?> ApproveAsync(Guid id, Guid? branch = null, IEffectivePermissionService? permissions = null)
    {
        await using var db = Db(branch ?? Main);
        return await new StockTransferService(db, permissions ?? Everything()).ApproveAsync(id, W.UserId);
    }

    /// <summary>Dispatches every line of the transfer exactly as requested (same unit, same quantity).</summary>
    public async Task<StockTransfer?> DispatchAsRequestedAsync(Guid id, Guid? branch = null, IEffectivePermissionService? permissions = null, string? key = null)
    {
        await using var db = Db(branch ?? Main);
        var t = await db.StockTransfers.Include(x => x.Lines).SingleAsync(x => x.Id == id);
        var lines = t.Lines.OrderBy(l => l.LineNo).Select(l => new DispatchStockTransferLineInput(l.Id, l.ProductUnitId, l.RequestedQty)).ToList();
        return await new StockTransferService(db, permissions ?? Everything()).DispatchAsync(id, new DispatchStockTransferInput(lines, key), W.UserId);
    }

    public async Task<StockTransfer?> DispatchAsync(Guid id, DispatchStockTransferInput input, Guid? branch = null, IEffectivePermissionService? permissions = null)
    {
        await using var db = Db(branch ?? Main);
        return await new StockTransferService(db, permissions ?? Everything()).DispatchAsync(id, input, W.UserId);
    }

    public async Task<StockTransferReceiptResult?> ReceiveAsync(Guid id, ReceiveStockTransferInput input, Guid? branch = null, IEffectivePermissionService? permissions = null)
    {
        await using var db = Db(branch ?? Blr);
        return await new StockTransferService(db, permissions ?? Everything()).ReceiveAsync(id, input, W.UserId);
    }

    public Task<StockTransferReceiptResult?> ReceiveAsync(Guid id, Guid lineId, Guid productUnit, decimal accepted, decimal damaged = 0m, string? key = null, Guid? branch = null) =>
        ReceiveAsync(id, new ReceiveStockTransferInput(null, [new ReceiveStockTransferLineInput(lineId, productUnit, accepted, damaged)], key), branch);

    public async Task<StockTransferReceiptResult?> ResolveAsync(Guid id, ResolveStockTransferInput input, Guid? branch = null, IEffectivePermissionService? permissions = null)
    {
        await using var db = Db(branch ?? Main);
        return await new StockTransferService(db, permissions ?? Everything()).ResolveAsync(id, input, W.UserId);
    }

    public async Task<StockTransfer> ReloadAsync(Guid id)
    {
        await using var db = Db();
        return await db.StockTransfers.AsNoTracking().Include(t => t.Lines).ThenInclude(l => l.Batches).Include(t => t.Receipts).ThenInclude(r => r.Lines).SingleAsync(t => t.Id == id);
    }

    // ---- reads ------------------------------------------------------------------------------------------------------------------

    public async Task<decimal> QohAsync(Guid location, Guid? productId = null)
    {
        await using var db = Db();
        var id = productId ?? PenId;
        return await db.StockItems.Where(i => i.StockLocationId == location && i.ProductId == id).Select(i => i.QuantityOnHand).SingleOrDefaultAsync();
    }

    public async Task<decimal> AverageCostAsync(Guid location, Guid? productId = null)
    {
        await using var db = Db();
        var id = productId ?? PenId;
        return await db.StockItems.Where(i => i.StockLocationId == location && i.ProductId == id).Select(i => i.AverageCost).SingleAsync();
    }

    /// <summary>Σ QOH × average cost of a product over every location (Transit included): company value.</summary>
    public async Task<decimal> CompanyValueAsync(Guid? productId = null)
    {
        await using var db = Db();
        var id = productId ?? PenId;
        return (await db.StockItems.Where(i => i.ProductId == id).ToListAsync()).Sum(i => i.QuantityOnHand * i.AverageCost);
    }

    /// <summary>Company value is conserved by dispatch, receipt and return (design R2): averages are stored at 6 dp, so the drift is at most 1e-6 per unit held.</summary>
    public async Task AssertValueConservedAsync(decimal expected, string step, Guid? productId = null)
    {
        var tolerance = 0.000001m * Math.Max(1m, await CompanyQuantityAsync(productId));
        var actual = await CompanyValueAsync(productId);
        Assert.True(Math.Abs(actual - expected) <= tolerance, $"{step}: company value {actual} drifted from {expected} by more than {tolerance}");
    }

    public async Task<decimal> CompanyQuantityAsync(Guid? productId = null)
    {
        await using var db = Db();
        var id = productId ?? PenId;
        return (await db.StockItems.Where(i => i.ProductId == id).ToListAsync()).Sum(i => i.QuantityOnHand);
    }

    public async Task<List<StockMovement>> MovementsAsync(Guid transferId)
    {
        await using var db = Db();
        return (await db.StockMovements.AsNoTracking().Where(m => m.ReferenceType == "StockTransfer" && m.ReferenceId == transferId).ToListAsync()).OrderBy(m => m.OccurredAt).ToList();
    }

    public async Task<int> TransferCountAsync()
    {
        await using var db = Db();
        return await db.StockTransfers.CountAsync();
    }

    /// <summary>Location reconciliation (<see cref="LocationWorld.AssertInvariantsAsync"/>) plus V4: Transit holds exactly the quantity still outstanding on transfers, per batch.</summary>
    public async Task AssertInvariantsAsync(string step)
    {
        await L.AssertInvariantsAsync(step);
        await using var db = Db();
        var balances = await db.BatchLocationBalances.AsNoTracking().Where(b => b.StockLocationId == L.Transit).ToListAsync();
        var rows = await db.StockTransferLineBatches.AsNoTracking().ToListAsync();
        var outstanding = rows.GroupBy(r => r.PurchaseBatchId).ToDictionary(g => g.Key, g => g.Sum(r => r.DispatchedBaseQty - r.ReceivedBaseQty - r.DamagedBaseQty - r.WrittenOffBaseQty - r.ReturnedBaseQty));
        foreach (var batchId in balances.Select(b => b.PurchaseBatchId).Concat(outstanding.Keys).Distinct())
        {
            Assert.True(balances.Where(b => b.PurchaseBatchId == batchId).Sum(b => b.RemainingBaseQty) == outstanding.GetValueOrDefault(batchId), $"{step}: Transit vs outstanding for batch {batchId}");
        }
    }
}
