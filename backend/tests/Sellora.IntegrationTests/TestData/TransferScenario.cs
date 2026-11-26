using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Domain.Purchasing;
using Sellora.Domain.Sales;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.IntegrationTests.Infrastructure;

namespace Sellora.IntegrationTests.TestData;

/// <summary>
/// Phase 9 PostgreSQL fixture on top of <see cref="LocationScenario"/> (Main + BLR with stores, warehouses, a BLR van, Transit): the canonical Pen of analysis 13.4
/// (Piece 1, Box 10, Half Carton 50, Full Carton 100; its OWN unit rows, priced Piece 10 / Box 100 so it can be sold) plus helpers that run every transfer
/// step on its own context, like separate requests. Callers default to NO permission service (branch fit only, fail closed) unless a test passes one.
/// </summary>
public sealed record TransferScenario(LocationScenario L, Guid PenId, Guid Pc, Guid Box, Guid Half, Guid Full)
{
    public static readonly string[] TransferCodes =
        ["StockTransfers.View", "StockTransfers.Create", "StockTransfers.Approve", "StockTransfers.ApproveRequest", "StockTransfers.Dispatch", "StockTransfers.Receive", "StockTransfers.Cancel", "StockTransfers.Resolve"];

    /// <summary>Every transfer code company-wide (+ the posting codes the fixture services need).</summary>
    public static readonly IEffectivePermissionService All = new FakePermissions([.. TransferCodes, "Purchase.Create", "Sales.Create", "Inventory.Adjust"]);

    public Guid Main => Sellora.Domain.Companies.DefaultTenant.BranchId;
    public Guid Blr => L.Blr;
    public Guid MainStore => L.MainStore;
    public Guid BlrStore => L.BlrStore;
    public Guid Transit { get; init; }
    public TestDatabase Database => L.Database;
    public Guid UserId => L.S.UserId;

    public static async Task<TransferScenario> CreateAsync(TestDatabase database)
    {
        var l = await LocationScenario.CreateAsync(database);
        await using var db = l.Db();
        static Unit U(string name, string code) => new() { Id = Guid.NewGuid(), Name = name, ShortCode = code, MeasurementCategory = code == "pc" ? MeasurementCategory.Count : MeasurementCategory.Packaging, DecimalPlaces = 0 };
        var (pc, box, half, full) = (U("Piece", "pc"), U("Box", "box"), U("Half Carton", "hctn"), U("Full Carton", "fctn"));
        db.AddRange(pc, box, half, full);
        await db.SaveChangesAsync();
        await TaxFixtures.UseAsCompanyDefaultAsync(db, await db.Taxes.FirstAsync());   // TP-1: products need a company country and a resolvable profile
        static ProductUnitInputItem Row(Guid unit, Guid? inner, decimal? qty) => new(null, unit, inner, qty, null, null, true, true, false, false, true);
        var pen = await new ProductService(db).CreateProductAsync(new UpsertProductInput("TRF-PEN", "Transfer Pen", null, null, pc.Id, 0m,
            [Row(pc.Id, null, 1m), Row(box.Id, pc.Id, 10m), Row(half.Id, box.Id, 5m), Row(full.Id, half.Id, 2m)]));
        var rows = await db.ProductUnits.AsNoTracking().Where(u => u.ProductId == pen.Id).Include(u => u.Unit).ToListAsync();
        ProductUnit R(string code) => rows.Single(r => r.Unit.ShortCode == code);
        await StockScenario.EnsureListPriceAsync(db, l.S.UserId, R("pc").Id, 10m);
        await StockScenario.EnsureListPriceAsync(db, l.S.UserId, R("box").Id, 100m);
        var transit = await db.StockLocations.Where(x => x.LocationType == StockLocationType.Transit).Select(x => x.Id).SingleAsync();
        return new TransferScenario(l, pen.Id, R("pc").Id, R("box").Id, R("hctn").Id, R("fctn").Id) { Transit = transit };
    }

    public SelloraDbContext Db(Guid? branch = null) => L.Db(branch);

    public static StockTransferLineInput Line(Guid productId, Guid unit, decimal qty) => new(productId, unit, qty);
    public StockTransferLineInput Pen(Guid unit, decimal qty) => new(PenId, unit, qty);

    // ---- documents -----------------------------------------------------------------------------------------------------------------

    /// <summary>Buys Pen in the given unit (rate per unit) at a location; the batch cost is rate / factor per piece.</summary>
    public async Task<PurchaseOrder> BuyAsync(Guid? branch, Guid? location, Guid unit, decimal qty, decimal rate)
    {
        await using var db = Db(branch);
        return await new PurchaseService(db, new ExchangeRateService(db), LocationScenario.CompanyLevel).CreatePurchaseAsync(
            new CreatePurchaseInput(L.S.SupplierId, StockScenario.PurchaseDate, null, null, PaymentMethod.Cash, 0, [new PurchaseLineInput(PenId, unit, qty, rate, 0, 0)], StockLocationId: location), UserId);
    }

    public async Task<SalesInvoice> SellAsync(Guid? branch, Guid? location, Guid unit, decimal qty, decimal price)
    {
        await using var db = Db(branch);
        return await new SalesService(db, LocationScenario.CompanyLevel).CreateSaleAsync(
            new CreateSaleInput(L.S.CustomerId, StockScenario.SaleDate, PaymentMethod.Cash, 0, [new SaleLineInput(PenId, unit, qty, price, 0)], StockLocationId: location), UserId);
    }

    public async Task<StockAdjustment> DamageAsync(Guid? branch, Guid? location, decimal pieces)
    {
        await using var db = Db(branch);
        return await new StockAdjustmentService(db, LocationScenario.CompanyLevel).CreateAdjustmentAsync(
            new CreateAdjustmentInput(PenId, Pc, AdjustmentType.Damage, AdjustmentDirection.Decrease, pieces, null, "damage", StockLocationId: location), UserId);
    }

    // ---- transfer steps (each on a fresh context of the working branch) ---------------------------------------------------------------------

    public async Task<StockTransfer> CreateAsync(Guid source, Guid destination, StockTransferLineInput[] lines, bool submit = true, Guid? branch = null,
        IEffectivePermissionService? permissions = null, string? key = null)
    {
        await using var db = Db(branch ?? Main);
        return await new StockTransferService(db, permissions).CreateAsync(new CreateStockTransferInput(source, destination, "test", submit, lines, key), UserId);
    }

    public async Task<StockTransfer> ApprovedAsync(Guid source, Guid destination, params StockTransferLineInput[] lines)
    {
        var t = await CreateAsync(source, destination, lines, submit: true);
        return (await ApproveAsync(t.Id))!;
    }

    /// <summary>Created, submitted and approved at <paramref name="branch"/> (needed when the source belongs to another branch than Main).</summary>
    public async Task<StockTransfer> ApprovedAtAsync(Guid branch, Guid source, Guid destination, params StockTransferLineInput[] lines)
    {
        var t = await CreateAsync(source, destination, lines, submit: true, branch: branch);
        return (await ApproveAsync(t.Id, branch))!;
    }

    public async Task<StockTransfer?> ApproveAsync(Guid id, Guid? branch = null, IEffectivePermissionService? permissions = null)
    {
        await using var db = Db(branch ?? Main);
        return await new StockTransferService(db, permissions).ApproveAsync(id, UserId);
    }

    public async Task<StockTransfer?> DispatchAsRequestedAsync(Guid id, Guid? branch = null, IEffectivePermissionService? permissions = null, string? key = null)
    {
        await using var db = Db(branch ?? Main);
        var t = await db.StockTransfers.Include(x => x.Lines).SingleAsync(x => x.Id == id);
        var lines = t.Lines.OrderBy(x => x.LineNo).Select(x => new DispatchStockTransferLineInput(x.Id, x.ProductUnitId, x.RequestedQty)).ToList();
        return await LegacyTransferSeed.DispatchAsync(db, UserId, id, lines); // FI-D3: production dispatches through shipments; this seeds the legacy-dispatched state
    }

    public async Task<StockTransferReceiptResult?> ReceiveAsync(Guid id, Guid unit, decimal accepted, decimal damaged = 0m, Guid? branch = null,
        IEffectivePermissionService? permissions = null, string? key = null)
    {
        await using var db = Db(branch ?? Blr);
        var t = await db.StockTransfers.Include(x => x.Lines).SingleAsync(x => x.Id == id);
        return await new StockTransferService(db, permissions).ReceiveAsync(id,
            new ReceiveStockTransferInput(null, [new ReceiveStockTransferLineInput(t.Lines.Single().Id, unit, accepted, damaged)], key), UserId);
    }

    public async Task<StockTransferReceiptResult?> ResolveAsync(Guid id, StockTransferResolution action, Guid? branch = null, IEffectivePermissionService? permissions = null, string? key = null)
    {
        await using var db = Db(branch ?? Main);
        var t = await db.StockTransfers.Include(x => x.Lines).SingleAsync(x => x.Id == id);
        return await new StockTransferService(db, permissions ?? All).ResolveAsync(id,
            new ResolveStockTransferInput("test", t.Lines.Select(x => new ResolveStockTransferLineInput(x.Id, action)).ToList(), key), UserId);
    }

    public async Task<StockTransfer> ReloadAsync(Guid id)
    {
        await using var db = Db();
        return await db.StockTransfers.AsNoTracking().Include(t => t.Lines).ThenInclude(l => l.Batches).Include(t => t.Receipts).ThenInclude(r => r.Lines).SingleAsync(t => t.Id == id);
    }

    // ---- reads ---------------------------------------------------------------------------------------------------------------------

    public async Task<decimal> QohAsync(Guid location, Guid? productId = null)
    {
        await using var db = Db();
        var id = productId ?? PenId;
        return await db.StockItems.Where(i => i.StockLocationId == location && i.ProductId == id).Select(i => i.QuantityOnHand).SingleOrDefaultAsync();
    }

    public async Task<decimal> CompanyQuantityAsync()
    {
        await using var db = Db();
        return (await db.StockItems.Where(i => i.ProductId == PenId).ToListAsync()).Sum(i => i.QuantityOnHand);
    }

    public async Task<decimal> CompanyValueAsync()
    {
        await using var db = Db();
        return (await db.StockItems.Where(i => i.ProductId == PenId).ToListAsync()).Sum(i => i.QuantityOnHand * i.AverageCost);
    }

    /// <summary>Company value is conserved by dispatch / receipt / return (design R2): averages are stored at 6 dp, so the drift is at most 1e-6 per unit held.</summary>
    public async Task AssertValueConservedAsync(decimal expected, string step)
    {
        var tolerance = 0.000001m * Math.Max(1m, await CompanyQuantityAsync());
        var actual = await CompanyValueAsync();
        Assert.True(Math.Abs(actual - expected) <= tolerance, $"{step}: company value {actual} drifted from {expected} by more than {tolerance}");
    }

    public async Task<decimal> BatchTotalAsync()
    {
        await using var db = Db();
        return await db.PurchaseBatches.Where(b => b.ProductId == PenId).SumAsync(b => b.RemainingBaseQty);
    }

    public async Task<int> MovementCountAsync()
    {
        await using var db = Db();
        return await db.StockMovements.CountAsync();
    }

    /// <summary>Every stock invariant (QOH / ledger / balances / batches, no negatives) AND the transfer validation script V1-V8 (each must return no rows).</summary>
    public async Task AssertInvariantsAsync(string step)
    {
        await L.AssertInvariantsAsync(step);
        await TransferValidation.AssertCleanAsync(Database, step);
    }
}

/// <summary>Runs <c>backend/db/validation/V__stock-transfers-validation.sql</c> (V1-V8) exactly as shipped: every statement must return no rows.</summary>
public static class TransferValidation
{
    public static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend", "db", "validation", "V__stock-transfers-validation.sql"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "backend", "db", "validation", "V__stock-transfers-validation.sql");
    }

    /// <summary>The script's statements. Lines tagged <c>-- [P06: text]</c> use schema that exists only from Phase 06: <paramref name="phase06Schema"/> false replaces each tagged line with its text (older-schema migration tests).</summary>
    public static List<string> Statements(bool phase06Schema = true)
    {
        var lines = File.ReadAllLines(ScriptPath()).Where(l => !l.TrimStart().StartsWith("--"))
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"--\s*\[P06:(?<text>.*)\]\s*$") is { Success: true } m && !phase06Schema ? m.Groups["text"].Value.Trim() : l);
        var sql = string.Join('\n', lines);
        return sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s.Length > 0).ToList();
    }

    public static async Task AssertCleanAsync(TestDatabase database, string step)
    {
        await using var db = database.CreateDbContext();
        var phase06 = await db.Database.SqlQueryRaw<int>("""SELECT COUNT(*)::int AS "Value" FROM information_schema.columns WHERE table_name = 'StockTransferLines' AND column_name = 'AcceptedCorrectionBaseQty'""").SingleAsync() > 0;
        var statements = Statements(phase06);
        Assert.Equal(9, statements.Count); // V1 V2 V3 V4 V5 V6a V6b V7 V8
        foreach (var statement in statements)
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            var offenders = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) offenders.Add(string.Join("/", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
            }
            Assert.True(offenders.Count == 0, $"after {step}: validation script returned rows: {string.Join("; ", offenders.Take(5))} for {statement[..Math.Min(40, statement.Length)]}");
        }
    }
}
