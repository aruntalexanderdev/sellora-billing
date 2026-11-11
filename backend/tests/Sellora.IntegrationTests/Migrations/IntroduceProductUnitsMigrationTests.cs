using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using Sellora.IntegrationTests.TestData;

namespace Sellora.IntegrationTests.Migrations;

/// <summary>
/// Applies <c>20261104121850_IntroduceProductUnits</c> to legacy selling modes written into the previous schema
/// (<c>ProductSellingModes</c> with Label + factor, <c>SellingModeId</c> / <c>ProductSellingModeId</c> references) and
/// checks the backfill against the mapping table of design §4.3, the V1–V6 validation queries, the in-migration
/// guard and the Down path.
/// </summary>
[Collection(PostgresCollection.Name)]
public class IntroduceProductUnitsMigrationTests(PostgresFixture postgres)
{
    private const string PreviousMigration = "20261104105655_ExtendUnitMaster";
    private const string IntroduceProductUnits = "20261104121850_IntroduceProductUnits";

    private static Guid G(int prefix, int n) => Guid.Parse($"{prefix:x8}-0000-0000-0000-{n:x12}");

    // Units (Phase 1 schema). "box" is a legacy container kept as Count; "crate" is inactive.
    private static readonly Guid UKg = G(3, 1), UG = G(3, 2), UPc = G(3, 3), UBox = G(3, 4), UBag = G(3, 5), UCrate = G(3, 6);
    private static readonly Guid TaxId = G(4, 1), RoleId = G(4, 2), UserId = G(4, 3), SupplierId = G(4, 4), CustomerId = G(4, 5);
    private static readonly Guid PurchaseId = G(4, 6), PurchaseLineId = G(4, 7), InvoiceId = G(4, 8), InvoiceLineId = G(4, 9);

    // Products
    private static readonly Guid PRice = G(1, 1), PPen = G(1, 2), PKgBox = G(1, 3), PNoBase = G(1, 4), PTwoBase = G(1, 5),
        PGrams = G(1, 6), PBags = G(1, 7), PCrate = G(1, 8), PInactiveBase = G(1, 9), PNoModes = G(1, 10);

    // Legacy modes (Id order matters for the "first wins" rules: lower Id first).
    private static readonly Guid MRiceBag = G(2, 0x11), MRiceLoose = G(2, 0x12);
    private static readonly Guid MPenPiece = G(2, 0x21), MPenBox = G(2, 0x22);
    private static readonly Guid MKgBox = G(2, 0x31);
    private static readonly Guid MNoBase5kg = G(2, 0x41);
    private static readonly Guid MTwoLoose = G(2, 0x51), MTwoKg = G(2, 0x52);
    private static readonly Guid MGram = G(2, 0x61), MGramHalf = G(2, 0x62), MGram500 = G(2, 0x63);
    private static readonly Guid MBag10 = G(2, 0x71), MBag25 = G(2, 0x72);
    private static readonly Guid MCrateLoose = G(2, 0x81), MCrate = G(2, 0x82);
    private static readonly Guid MInactivePiece = G(2, 0x91), MInactiveBox = G(2, 0x92);

    private static readonly Guid PriceRiceBag = G(5, 1), PriceRiceLoose = G(5, 2), PricePenBox = G(5, 3), PriceInactiveBox = G(5, 4), PriceRiceBagOld = G(5, 5);

    /// <summary>(Id, ProductId, Label, Factor, IsActive) as written into the old schema.</summary>
    private static readonly (Guid Id, Guid ProductId, string Label, decimal Factor, bool IsActive)[] LegacyModes =
    [
        (MRiceBag, PRice, "10 kg bag", 10m, true), (MRiceLoose, PRice, "Loose kg", 1m, true),
        (MPenPiece, PPen, "Piece", 1m, true), (MPenBox, PPen, "Box", 10m, true),
        (MKgBox, PKgBox, "Box", 5m, true),
        (MNoBase5kg, PNoBase, "5 kg bag", 5m, true),
        (MTwoLoose, PTwoBase, "Loose", 1m, true), (MTwoKg, PTwoBase, "kg", 1m, true),
        (MGram, PGrams, "g", 0.001m, true), (MGramHalf, PGrams, "g", 0.5m, true), (MGram500, PGrams, "500 g", 0.5m, true),
        (MBag10, PBags, "Bag", 10m, true), (MBag25, PBags, "bag", 25m, true),
        (MCrateLoose, PCrate, "Loose kg", 1m, true), (MCrate, PCrate, "Crate", 20m, true),
        (MInactivePiece, PInactiveBase, "Piece", 1m, false), (MInactiveBox, PInactiveBase, "Box", 12m, true),
    ];

    /// <summary>Products that have no factor-1 mode and therefore get a new base row (P2).</summary>
    private static readonly Guid[] ProductsWithoutFactorOne = [PKgBox, PNoBase, PGrams, PBags, PNoModes];

    private static readonly Dictionary<Guid, Guid> BaseUnitOf = new()
    {
        [PRice] = UKg, [PPen] = UPc, [PKgBox] = UKg, [PNoBase] = UKg, [PTwoBase] = UKg, [PGrams] = UKg,
        [PBags] = UKg, [PCrate] = UKg, [PInactiveBase] = UPc, [PNoModes] = UPc,
    };

    private enum Resolution { Base, Matched, Generated }

    /// <summary>Expected outcome per legacy mode (design §4.3). For generated units, <c>NameIsLabel</c> says whether
    /// the generated unit is named after the label itself (no unit of that name exists) or "label [PKG-nnn]".</summary>
    private static readonly Dictionary<Guid, (Resolution Kind, Guid? Unit, bool NameIsLabel)> Expected = new()
    {
        [MRiceLoose] = (Resolution.Base, UKg, false),
        [MRiceBag] = (Resolution.Generated, null, true),          // "10 kg bag": quantities in labels never parsed
        [MPenPiece] = (Resolution.Base, UPc, false),
        [MPenBox] = (Resolution.Matched, UBox, false),             // legacy Count box over a Count base (D9)
        [MKgBox] = (Resolution.Generated, null, false),            // Count box over a kg base: incompatible -> "Box [PKG-nnn]"
        [MNoBase5kg] = (Resolution.Generated, null, true),
        [MTwoKg] = (Resolution.Base, UKg, false),                  // label equals the base unit: preferred as base
        [MTwoLoose] = (Resolution.Generated, null, true),          // extra factor-1 row: generated, InnerQuantity 1
        [MGram] = (Resolution.Matched, UG, false),                 // exact name + StandardFactor consistent
        [MGramHalf] = (Resolution.Generated, null, false),         // label "g" but factor 0.5: StandardFactor mismatch; "g" is a ShortCode -> "g [PKG-nnn]"
        [MGram500] = (Resolution.Generated, null, true),           // "500 g" is never mapped to g
        [MBag10] = (Resolution.Matched, UBag, false),              // first of two "bag" labels wins (lower Id)
        [MBag25] = (Resolution.Generated, null, false),            // (ProductId, UnitId) conflict -> "bag [PKG-nnn]"
        [MCrateLoose] = (Resolution.Base, UKg, false),
        [MCrate] = (Resolution.Generated, null, false),            // label equals an inactive unit -> "Crate [PKG-nnn]"
        [MInactivePiece] = (Resolution.Base, UPc, false),          // inactive factor-1 row -> active base, not sellable
        [MInactiveBox] = (Resolution.Matched, UBox, false),
    };

    private const int GeneratedUnitCount = 8; // 10 kg bag, Box, 5 kg bag, Loose, g, 500 g, bag, Crate
    private const int LegacyUnitCount = 6;

    private static async Task<TestDatabase> CreateLegacyDatabaseAsync(PostgresFixture postgres, Action<System.Text.StringBuilder>? extra = null)
    {
        var database = await postgres.CreateDatabaseAsync(migrate: false);
        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Assert.DoesNotContain(IntroduceProductUnits, await db.Database.GetAppliedMigrationsAsync());
        }

        var sql = new System.Text.StringBuilder();
        sql.AppendLine($"""
            INSERT INTO "Units" ("Id", "Name", "ShortCode", "MeasurementCategory", "DecimalPlaces", "StandardFactor", "IsActive") VALUES
              ('{UKg}', 'Kilogram', 'kg', 'Weight', 3, 1, true),
              ('{UG}', 'Gram', 'g', 'Weight', 0, 0.001, true),
              ('{UPc}', 'Piece', 'pc', 'Count', 0, NULL, true),
              ('{UBox}', 'Box', 'box', 'Count', 0, NULL, true),
              ('{UBag}', 'Bag', 'bag', 'Packaging', 0, NULL, true),
              ('{UCrate}', 'Crate', 'crate', 'Packaging', 0, NULL, false);
            INSERT INTO "Taxes" ("Id", "Name", "RatePercent", "IsActive") VALUES ('{TaxId}', 'GST 0%', 0, true);
            """);
        var i = 0;
        foreach (var (productId, unitId) in BaseUnitOf)
        {
            i++;
            sql.AppendLine($"""
                INSERT INTO "Products" ("Id", "Sku", "Name", "Category", "BaseUnitId", "TaxId", "ReorderLevel", "IsActive")
                VALUES ('{productId}', 'LEG-{i:00}', 'Legacy product {i}', NULL, '{unitId}', '{TaxId}', 0, true);
                """);
        }
        foreach (var (id, productId, label, factor, isActive) in LegacyModes)
        {
            sql.AppendLine($"""
                INSERT INTO "ProductSellingModes" ("Id", "ProductId", "Label", "ConversionFactorToBase", "IsActive")
                VALUES ('{id}', '{productId}', '{label}', {factor.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {(isActive ? "true" : "false")});
                """);
        }
        sql.AppendLine($"""
            INSERT INTO "Roles" ("Id", "Name", "Description", "IsActive", "IsSystem") VALUES ('{RoleId}', 'Administrator', NULL, true, true);
            INSERT INTO "Users" ("Id", "CreatedAt", "Email", "FirstName", "LastName", "PasswordHash", "RoleId", "Status", "Username")
            VALUES ('{UserId}', now(), 'admin@selloramart.in', 'Ravi', 'Krishnan', 'x', '{RoleId}', 'Active', 'admin');
            INSERT INTO "Suppliers" ("Id", "Name", "CurrencyId", "IsActive")
            SELECT '{SupplierId}', 'Annapoorna Traders', c."Id", true FROM "Currencies" c WHERE c."Code" = 'INR';
            INSERT INTO "Customers" ("Id", "Name", "IsActive", "IsWalkIn") VALUES ('{CustomerId}', 'Walk-in Customer', true, true);

            INSERT INTO "SellingPrices" ("Id", "CreatedAt", "CreatedByUserId", "EffectiveFrom", "EffectiveTo", "Price", "ProductSellingModeId") VALUES
              ('{PriceRiceBagOld}', now(), '{UserId}', now() - interval '10 days', now() - interval '1 day', 600, '{MRiceBag}'),
              ('{PriceRiceBag}', now(), '{UserId}', now() - interval '1 day', NULL, 620, '{MRiceBag}'),
              ('{PriceRiceLoose}', now(), '{UserId}', now() - interval '1 day', NULL, 65, '{MRiceLoose}'),
              ('{PricePenBox}', now(), '{UserId}', now() - interval '1 day', NULL, 100, '{MPenBox}'),
              ('{PriceInactiveBox}', now(), '{UserId}', now() - interval '1 day', NULL, 120, '{MInactiveBox}');

            INSERT INTO "PurchaseOrders" ("Id", "CreatedAt", "CreatedByUserId", "CurrencyId", "DiscountTotal", "ExchangeRateToBase",
              "GrandTotal", "OriginalGrandTotal", "OriginalPaidAmount", "OriginalReturnedAmount", "OtherChargesTotal", "PaidAmount",
              "PaymentMethod", "PurchaseDate", "PurchaseNo", "ReturnedAmount", "Status", "Subtotal", "SupplierId", "TaxTotal")
            SELECT '{PurchaseId}', now(), '{UserId}', c."Id", 0, 1, 1160, 1160, 1160, 0, 0, 1160,
              'Cash', DATE '2026-09-01', 'PUR-0001', 0, 'Completed', 1160, '{SupplierId}', 0 FROM "Currencies" c WHERE c."Code" = 'INR';
            INSERT INTO "PurchaseOrderLines" ("Id", "BaseQty", "ConversionFactorToBase", "Discount", "LineTotal", "OtherCharges", "ProductId",
              "PurchaseOrderId", "Qty", "Rate", "SellingModeId", "TaxAmount", "TaxId", "TaxRatePercent", "TaxableValue", "UnitCost")
            VALUES ('{PurchaseLineId}', 20, 10, 0, 1160, 0, '{PRice}', '{PurchaseId}', 2, 580, '{MRiceBag}', 0, '{TaxId}', 0, 1160, 58);

            INSERT INTO "SalesInvoices" ("Id", "CreatedAt", "CreatedByUserId", "CustomerId", "DiscountTotal", "GrandTotal", "InvoiceDate",
              "InvoiceNo", "PaidAmount", "PaymentMethod", "ReturnedAmount", "Status", "Subtotal", "TaxTotal")
            VALUES ('{InvoiceId}', now(), '{UserId}', '{CustomerId}', 0, 195, DATE '2026-09-18', 'INV-0001', 195, 'Cash', 0, 'Completed', 195, 0);
            INSERT INTO "SalesInvoiceLines" ("Id", "BaseQty", "ConversionFactorToBase", "CostOfGoodsSold", "Discount", "LineTotal", "ProductId",
              "Qty", "SalesInvoiceId", "SellingModeId", "TaxAmount", "TaxId", "TaxRatePercent", "TaxableValue", "UnitPrice")
            VALUES ('{InvoiceLineId}', 3, 1, 174, 0, 195, '{PRice}', 3, '{InvoiceId}', '{MRiceLoose}', 0, '{TaxId}', 0, 195, 65);
            """);
        extra?.Invoke(sql);
        await ExecuteAsync(database, sql.ToString());
        return database;
    }

    [Fact]
    public async Task IntroduceProductUnits_LegacyModes_PreservesIdsAndFactorsAndMapsUnitsPerDesign()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var countsBefore = await CountsAsync(database, "ProductSellingModes", "ProductSellingModeId");

        await using var db = database.CreateDbContext();
        await db.Database.MigrateAsync();

        var units = await db.ProductUnits.AsNoTracking().ToListAsync();
        var byId = units.ToDictionary(u => u.Id);
        var allUnits = await db.Units.AsNoTracking().ToDictionaryAsync(u => u.Id);

        // Ids and factors preserved; labels and active flags kept.
        foreach (var (id, productId, label, factor, isActive) in LegacyModes)
        {
            Assert.True(byId.TryGetValue(id, out var row), $"Legacy mode {label} ({id}) was lost.");
            Assert.Equal((productId, label, factor), (row!.ProductId, row.Label, row.ConversionFactorToBase));
            if (id != MInactivePiece) Assert.True(isActive == row.IsActive, $"{label}: IsActive changed.");
        }

        // Exactly one active base per product, on the product base unit.
        var products = await db.Products.AsNoTracking().ToListAsync();
        Assert.Equal(BaseUnitOf.Count, products.Count);
        foreach (var product in products)
        {
            var bases = units.Where(u => u.ProductId == product.Id && u.IsBaseUnit).ToList();
            var baseRow = Assert.Single(bases);
            Assert.Equal(product.BaseUnitId, baseRow.UnitId);
            Assert.Equal((1m, 1m, true, (Guid?)null), (baseRow.InnerQuantity, baseRow.ConversionFactorToBase, baseRow.IsActive, baseRow.InnerProductUnitId));
            Assert.Equal(0, baseRow.SortOrder);

            // Flat hierarchy: every other row sits directly on the base with InnerQuantity = old factor.
            foreach (var row in units.Where(u => u.ProductId == product.Id && !u.IsBaseUnit))
            {
                Assert.Equal(baseRow.Id, row.InnerProductUnitId);
                Assert.Equal(row.ConversionFactorToBase, row.InnerQuantity);
            }

            // SortOrder: base first, then by factor (then Id), 0..n-1.
            var ordered = units.Where(u => u.ProductId == product.Id)
                .OrderByDescending(u => u.IsBaseUnit).ThenBy(u => u.ConversionFactorToBase).ThenBy(u => u.Id).ToList();
            Assert.Equal(Enumerable.Range(0, ordered.Count).Select(n => (short)n), ordered.Select(u => u.SortOrder));
        }

        // New base rows only for products without a factor-1 mode: no history, not sellable / purchasable.
        var inserted = units.Where(u => !LegacyModes.Any(m => m.Id == u.Id)).ToList();
        Assert.Equal(ProductsWithoutFactorOne.OrderBy(x => x), inserted.Select(u => u.ProductId).OrderBy(x => x));
        Assert.All(inserted, u =>
        {
            Assert.True(u.IsBaseUnit && u.IsActive && !u.IsSellable && !u.IsPurchasable, $"Inserted base row {u.Id} flags.");
            Assert.Null(u.Label);
            Assert.Equal(BaseUnitOf[u.ProductId], u.UnitId);
        });

        // The inactive factor-1 row becomes an active base that cannot be sold or bought (same POS behaviour).
        var reactivated = byId[MInactivePiece];
        Assert.True(reactivated.IsBaseUnit && reactivated.IsActive && !reactivated.IsSellable && !reactivated.IsPurchasable);
        // Active legacy base rows stay sellable / purchasable.
        Assert.All(new[] { MRiceLoose, MPenPiece, MTwoKg, MCrateLoose }, id => Assert.True(byId[id].IsSellable && byId[id].IsPurchasable));

        // Mapping per §4.3.
        var generatedUnitIds = new HashSet<Guid>();
        foreach (var (id, (kind, unitId, nameIsLabel)) in Expected)
        {
            var row = byId[id];
            var label = LegacyModes.Single(m => m.Id == id).Label;
            Assert.True((kind == Resolution.Base) == row.IsBaseUnit, $"{label} ({id}): IsBaseUnit {row.IsBaseUnit}, expected {kind}.");
            if (kind != Resolution.Generated)
            {
                Assert.True(unitId == row.UnitId, $"{label} ({id}): mapped to {allUnits[row.UnitId].ShortCode}, expected {allUnits[unitId!.Value].ShortCode}.");
                continue;
            }

            var unit = allUnits[row.UnitId];
            Assert.True(Regex.IsMatch(unit.ShortCode, "^PKG-[0-9]{3}$"), $"{label}: generated code {unit.ShortCode}.");
            Assert.Equal((MeasurementCategory.Packaging, (short)0, (decimal?)null, true), (unit.MeasurementCategory, unit.DecimalPlaces, unit.StandardFactor, unit.IsActive));
            Assert.Equal(nameIsLabel ? label : $"{label} [{unit.ShortCode}]", unit.Name);
            Assert.True(generatedUnitIds.Add(unit.Id), $"{label}: generated unit {unit.ShortCode} shared within the legacy set.");
        }
        Assert.Equal(1m, byId[MTwoLoose].InnerQuantity); // extra factor-1 row sits 1:1 on the base

        // Units: legacy rows untouched, exactly the generated Packaging units added.
        Assert.Equal(LegacyUnitCount + GeneratedUnitCount, allUnits.Count);
        Assert.Equal(GeneratedUnitCount, generatedUnitIds.Count);
        Assert.False(allUnits[UCrate].IsActive);
        Assert.Equal(MeasurementCategory.Count, allUnits[UBox].MeasurementCategory);

        // Counts: ProductUnits = modes + products without a factor-1 mode; prices / lines unchanged (V7).
        var countsAfter = await CountsAsync(database, "ProductUnits", "ProductUnitId");
        Assert.Equal(countsBefore.Modes + ProductsWithoutFactorOne.Length, countsAfter.Modes);
        Assert.Equal((countsBefore.Prices, countsBefore.PurchaseLines, countsBefore.SalesLines), (countsAfter.Prices, countsAfter.PurchaseLines, countsAfter.SalesLines));
        Assert.Equal(LegacyModes.Length, countsBefore.Modes);

        // References now point at the same (preserved) ids under the new column names.
        Assert.Equal(MRiceBag, (await db.PurchaseOrderLines.AsNoTracking().SingleAsync()).ProductUnitId);
        Assert.Equal(MRiceLoose, (await db.SalesInvoiceLines.AsNoTracking().SingleAsync()).ProductUnitId);
        var prices = await db.SellingPrices.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.ProductUnitId);
        Assert.Equal(MRiceBag, prices[PriceRiceBag]);
        Assert.Equal(MRiceBag, prices[PriceRiceBagOld]);
        Assert.Equal(MRiceLoose, prices[PriceRiceLoose]);
        Assert.Equal(MPenBox, prices[PricePenBox]);
        Assert.Equal(MInactiveBox, prices[PriceInactiveBox]);

        await ProductUnitValidation.AssertHoldAsync(db);
        await UnitMasterValidation.AssertHoldAsync(db);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task IntroduceProductUnits_LegacyModes_EfLoadsHierarchyWithUnitsAndInner()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        await using (var db = database.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using var check = database.CreateDbContext();
        var rice = await check.Products.AsNoTracking()
            .Include(p => p.Units).ThenInclude(u => u.Unit)
            .Include(p => p.Units).ThenInclude(u => u.InnerProductUnit)
            .SingleAsync(p => p.Id == PRice);

        Assert.Equal(2, rice.Units.Count);
        var bag = rice.Units.Single(u => u.Id == MRiceBag);
        Assert.Equal(MRiceLoose, bag.InnerProductUnit!.Id);
        Assert.Equal("10 kg bag", bag.Unit.Name);
        Assert.Equal("kg", rice.Units.Single(u => u.IsBaseUnit).Unit.ShortCode);
    }

    [Fact]
    public async Task IntroduceProductUnits_LegacyModeWithNonPositiveFactor_RaisesAndRollsBackAtomically()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, sql => sql.AppendLine($"""
            INSERT INTO "ProductSellingModes" ("Id", "ProductId", "Label", "ConversionFactorToBase", "IsActive")
            VALUES ('{G(2, 0xF1)}', '{PNoModes}', 'Broken', 0, true);
            """));

        await using (var db = database.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains("factor <= 0", ex.MessageText);
        }

        await using var check = database.CreateDbContext();
        Assert.DoesNotContain(IntroduceProductUnits, await check.Database.GetAppliedMigrationsAsync());
        Assert.True(await TableExistsAsync(database, "ProductSellingModes"));
        Assert.False(await TableExistsAsync(database, "ProductUnits"));
        Assert.Contains("SellingModeId", await ColumnsAsync(database, "PurchaseOrderLines"));
        Assert.Equal(LegacyUnitCount, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Units" """)); // no generated units left behind
    }

    [Fact]
    public async Task IntroduceProductUnits_Down_RestoresOldSchemaAndHidesInsertedBaseRows()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var activeBefore = LegacyModes.Where(m => m.IsActive).Select(m => m.Id).ToHashSet();
        await using (var db = database.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Assert.DoesNotContain(IntroduceProductUnits, await db.Database.GetAppliedMigrationsAsync());
        }

        // Schema identical to a database that never saw the migration (columns, types, nullability, defaults,
        // constraints incl. FK delete rules, indexes) on every table the migration touches.
        await using var reference = await postgres.CreateDatabaseAsync(migrate: false);
        await using (var db = reference.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }
        var expectedSchema = await SchemaSignatureAsync(reference);
        var actualSchema = await SchemaSignatureAsync(database);
        Assert.Contains(expectedSchema, x => x.StartsWith("con SellingPrices.FK_SellingPrices_ProductSellingModes_ProductSellingModeId f del=c"));
        Assert.Contains(expectedSchema, x => x.StartsWith("con ProductSellingModes.FK_ProductSellingModes_Products_ProductId f del=c"));
        Assert.Contains(expectedSchema, x => x.StartsWith("col ProductSellingModes.ConversionFactorToBase numeric(18,3) null=NO"));
        Assert.Contains(expectedSchema, x => x.StartsWith("col ProductSellingModes.Label character varying") && x.Contains("null=NO"));
        Assert.Equal(expectedSchema.Except(actualSchema).Order(), Array.Empty<string>());
        Assert.Equal(actualSchema.Except(expectedSchema).Order(), Array.Empty<string>());
        Assert.False(await TableExistsAsync(database, "ProductUnits"));
        Assert.Equal(0L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM pg_constraint WHERE conname LIKE '%ProductUnit%' """));

        // Data: legacy rows intact, inserted base rows remain but are inactive with the unit name as label, so the
        // set of visible (active) modes is exactly the original one; the reactivated inactive base is inactive again.
        var rows = await QueryAsync(database, """SELECT "Id", "ProductId", "Label", "ConversionFactorToBase", "IsActive" FROM "ProductSellingModes" """,
            r => (Id: r.GetGuid(0), ProductId: r.GetGuid(1), Label: r.GetString(2), Factor: r.GetDecimal(3), IsActive: r.GetBoolean(4)));
        Assert.Equal(LegacyModes.Length + ProductsWithoutFactorOne.Length, rows.Count);
        foreach (var (id, productId, label, factor, isActive) in LegacyModes)
        {
            var row = Assert.Single(rows, r => r.Id == id);
            Assert.Equal((productId, label, factor, isActive), (row.ProductId, row.Label, row.Factor, row.IsActive));
        }
        var insertedRows = rows.Where(r => !LegacyModes.Any(m => m.Id == r.Id)).ToList();
        Assert.All(insertedRows, r =>
        {
            Assert.False(r.IsActive);
            Assert.Equal(1m, r.Factor);
            Assert.Equal(BaseUnitOf[r.ProductId] == UKg ? "Kilogram" : "Piece", r.Label);
        });
        Assert.Equal(activeBefore, rows.Where(r => r.IsActive).Select(r => r.Id).ToHashSet());

        Assert.Equal(MRiceBag, await ScalarAsync<Guid>(database, """SELECT "SellingModeId" FROM "PurchaseOrderLines" """));
        Assert.Equal(MRiceLoose, await ScalarAsync<Guid>(database, """SELECT "SellingModeId" FROM "SalesInvoiceLines" """));
        Assert.Equal(5L, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "SellingPrices" s JOIN "ProductSellingModes" m ON m."Id" = s."ProductSellingModeId" """));
        // Generated PKG units remain (additive).
        Assert.Equal(GeneratedUnitCount, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Units" WHERE "ShortCode" ~ '^PKG-' """));
    }

    [Fact]
    public async Task IntroduceProductUnits_DownThenUpAgain_ReappliesWithSameIdsAndNoExtraRows()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        Dictionary<Guid, Guid> firstUnitIds;
        await using (var db = database.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            firstUnitIds = await db.ProductUnits.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.UnitId);
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        await using (var db = database.CreateDbContext())
        {
            await db.Database.MigrateAsync();

            var units = await db.ProductUnits.AsNoTracking().ToListAsync();
            Assert.Equal(firstUnitIds.Keys.Order(), units.Select(u => u.Id).Order()); // inserted base rows are re-used as base
            foreach (var (id, _, label, factor, _) in LegacyModes)
            {
                Assert.True(factor == units.Single(u => u.Id == id).ConversionFactorToBase, $"{label}: factor changed.");
            }
            Assert.All(units.Where(u => u.IsBaseUnit), u => Assert.Equal(firstUnitIds[u.Id], u.UnitId));
            // A generated unit named exactly after its label is matched on the second run instead of generated again.
            Assert.Equal(firstUnitIds[MRiceBag], units.Single(u => u.Id == MRiceBag).UnitId);
            Assert.All(ProductsWithoutFactorOne, p => Assert.Single(units, u => u.ProductId == p && u.IsBaseUnit));

            await ProductUnitValidation.AssertHoldAsync(db);
            Assert.False(db.Database.HasPendingModelChanges());
        }
    }

    [Fact]
    public async Task IntroduceProductUnits_DownWithFactorBeyondThreeDecimals_RefusesInsteadOfRounding()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        await using (var db = database.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }
        // 0.5 g sachet on a kg base: factor 0.0005 is valid in numeric(28,10) but not representable in numeric(18,3).
        await ExecuteAsync(database, $"""
            INSERT INTO "ProductUnits" ("Id", "ProductId", "UnitId", "InnerProductUnitId", "InnerQuantity", "ConversionFactorToBase",
              "IsBaseUnit", "Label", "IsActive", "IsPurchasable", "IsSellable", "IsDefaultPurchaseUnit", "IsDefaultSalesUnit", "SortOrder", "CompanyId")
            VALUES (gen_random_uuid(), '{PRice}', '{UG}', '{MRiceLoose}', 0.0005, 0.0005, false, 'Sachet', true, true, true, false, false, 9, '{Sellora.IntegrationTests.TestData.CompanyMigrations.DefaultCompany}');
            """);

        await using (var db = database.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(PreviousMigration));
            Assert.Contains("more than 3 decimals", ex.MessageText);
        }

        await using var check = database.CreateDbContext();
        Assert.Contains(IntroduceProductUnits, await check.Database.GetAppliedMigrationsAsync());
        Assert.True(await TableExistsAsync(database, "ProductUnits"));
        Assert.Equal(0.0005m, await ScalarAsync<decimal>(database, """SELECT "ConversionFactorToBase" FROM "ProductUnits" WHERE "Label" = 'Sachet' """));
    }

    /// <summary>
    /// Production path (D10): the idempotent script generated for ExtendUnitMaster → IntroduceProductUnits, applied with
    /// a plain connection to the legacy fixture. EF wraps every statement of an idempotent script in
    /// <c>DO $EF$ … IF NOT EXISTS (history row) THEN … END $EF$</c>, so this proves the migration's own DO blocks, temp
    /// tables and RAISE guards work nested in those wrappers, and that a second run of the same script is a no-op.
    /// </summary>
    [Fact]
    public async Task IntroduceProductUnits_IdempotentScript_AppliesToLegacyDataAndSecondRunIsNoOp()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres);
        var countsBefore = await CountsAsync(database, "ProductSellingModes", "ProductSellingModeId");
        string script;
        await using (var db = database.CreateDbContext())
        {
            script = db.GetService<IMigrator>().GenerateScript(PreviousMigration, IntroduceProductUnits, MigrationsSqlGenerationOptions.Idempotent);
        }
        Assert.Contains("DO $EF$", script);
        Assert.Contains(IntroduceProductUnits, script);

        await ExecuteAsync(database, script);

        var countsAfter = await CountsAsync(database, "ProductUnits", "ProductUnitId");
        Assert.Equal(countsBefore.Modes + ProductsWithoutFactorOne.Length, countsAfter.Modes);
        Assert.Equal((countsBefore.Prices, countsBefore.PurchaseLines, countsBefore.SalesLines), (countsAfter.Prices, countsAfter.PurchaseLines, countsAfter.SalesLines));
        await using (var db = database.CreateDbContext())
        {
            await ProductUnitValidation.AssertHoldAsync(db);
            await UnitMasterValidation.AssertHoldAsync(db);
            Assert.Contains(IntroduceProductUnits, await db.Database.GetAppliedMigrationsAsync());
            // The script targets IntroduceProductUnits: exactly the migrations after it remain pending (none skipped,
            // none partially applied), in order.
            Assert.Equal(db.Database.GetMigrations().Where(m => string.CompareOrdinal(m, IntroduceProductUnits) > 0),
                await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
            // Multi-company: the head model's CompanyId filters read columns added by later migrations, so bring the schema to head
            // before the EF-model assertions below (the data assertions themselves are unchanged).
            await db.Database.MigrateAsync();
            var rows = await db.ProductUnits.AsNoTracking().ToDictionaryAsync(u => u.Id);
            Assert.All(LegacyModes, m => Assert.Equal(m.Factor, rows[m.Id].ConversionFactorToBase));
        }
        // Raw SQL: the current entity maps columns that later migrations add, which this older schema does not have.
        Assert.Equal(MRiceBag, await ScalarAsync<Guid>(database, """SELECT "ProductUnitId" FROM "PurchaseOrderLines" """));
        Assert.Equal(MRiceLoose, await ScalarAsync<Guid>(database, """SELECT "ProductUnitId" FROM "SalesInvoiceLines" """));
        Assert.Equal(LegacyUnitCount + GeneratedUnitCount, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "Units" """));

        var signature = await SchemaSignatureAsync(database);
        var data = await QueryAsync(database, """
            SELECT "Id"::text || '|' || "UnitId"::text || '|' || COALESCE("InnerProductUnitId"::text, '-') || '|' || "ConversionFactorToBase"::text
                   || '|' || "IsActive"::text || '|' || "IsSellable"::text || '|' || "SortOrder"::text
            FROM "ProductUnits" ORDER BY "Id"
            """, r => r.GetString(0));
        var units = await QueryAsync(database, """SELECT "Id"::text || '|' || "Name" || '|' || "ShortCode" FROM "Units" ORDER BY "Id" """, r => r.GetString(0));
        var history = await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "__EFMigrationsHistory" """);

        await ExecuteAsync(database, script); // second run: every block is skipped

        Assert.Equal(signature, await SchemaSignatureAsync(database));
        Assert.Equal(data, await QueryAsync(database, """
            SELECT "Id"::text || '|' || "UnitId"::text || '|' || COALESCE("InnerProductUnitId"::text, '-') || '|' || "ConversionFactorToBase"::text
                   || '|' || "IsActive"::text || '|' || "IsSellable"::text || '|' || "SortOrder"::text
            FROM "ProductUnits" ORDER BY "Id"
            """, r => r.GetString(0)));
        Assert.Equal(units, await QueryAsync(database, """SELECT "Id"::text || '|' || "Name" || '|' || "ShortCode" FROM "Units" ORDER BY "Id" """, r => r.GetString(0)));
        Assert.Equal(history, await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "__EFMigrationsHistory" """));
        await using (var db = database.CreateDbContext()) await ProductUnitValidation.AssertHoldAsync(db);
    }

    /// <summary>A failing in-migration guard (factor &lt;= 0) inside the idempotent script raises through the
    /// <c>DO $EF$</c> wrapper and rolls the migration back: no history row, old table still there.</summary>
    [Fact]
    public async Task IntroduceProductUnits_IdempotentScriptWithBadLegacyFactor_RaisesAndLeavesSchemaUnchanged()
    {
        await using var database = await CreateLegacyDatabaseAsync(postgres, sql => sql.AppendLine(
            $"""UPDATE "ProductSellingModes" SET "ConversionFactorToBase" = 0 WHERE "Id" = '{MBag25}';"""));
        string script;
        await using (var db = database.CreateDbContext())
        {
            script = db.GetService<IMigrator>().GenerateScript(PreviousMigration, IntroduceProductUnits, MigrationsSqlGenerationOptions.Idempotent);
        }

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, script));

        Assert.Contains("factor <= 0", ex.MessageText);
        Assert.True(await TableExistsAsync(database, "ProductSellingModes"));
        Assert.False(await TableExistsAsync(database, "ProductUnits"));
        await using var check = database.CreateDbContext();
        Assert.DoesNotContain(IntroduceProductUnits, await check.Database.GetAppliedMigrationsAsync());
    }

    private sealed record Counts(long Modes, long Prices, long PurchaseLines, long SalesLines);

    private static async Task<Counts> CountsAsync(TestDatabase database, string modesTable, string priceColumn) => new(
        await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "{modesTable}" """),
        await ScalarAsync<long>(database, $"""SELECT COUNT(*) FROM "SellingPrices" WHERE "{priceColumn}" IS NOT NULL"""),
        await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "PurchaseOrderLines" """),
        await ScalarAsync<long>(database, """SELECT COUNT(*) FROM "SalesInvoiceLines" """));

    /// <summary>Columns, constraints and indexes of the tables the migration touches, one string per item.</summary>
    private static async Task<HashSet<string>> SchemaSignatureAsync(TestDatabase database)
    {
        const string tables = "'ProductSellingModes','ProductUnits','PurchaseOrderLines','SalesInvoiceLines','SellingPrices','Products','Units'";
        var items = new HashSet<string>();
        foreach (var sql in new[]
        {
            $"""
            SELECT 'col ' || table_name || '.' || column_name || ' ' || data_type || COALESCE('(' || numeric_precision || ',' || numeric_scale || ')', '')
                   || COALESCE(' len ' || character_maximum_length, '') || ' null=' || is_nullable || ' default=' || COALESCE(column_default, '-')
            FROM information_schema.columns WHERE table_schema = 'public' AND table_name IN ({tables})
            """,
            $"""
            SELECT 'con ' || t.relname || '.' || c.conname || ' ' || c.contype::text || ' del=' || c.confdeltype::text || ' ' || pg_get_constraintdef(c.oid)
            FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid WHERE t.relname IN ({tables})
            """,
            $"""
            SELECT 'idx ' || tablename || '.' || indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ({tables})
            """,
        })
        {
            foreach (var item in await QueryAsync(database, sql, r => r.GetString(0))) items.Add(item);
        }
        return items;
    }

    private static async Task ExecuteAsync(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<T>> QueryAsync<T>(TestDatabase database, string sql, Func<NpgsqlDataReader, T> map)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(map(reader));
        return rows;
    }

    private static async Task<bool> TableExistsAsync(TestDatabase database, string table) =>
        await ScalarAsync<bool>(database, $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{table}')");

    private static async Task<HashSet<string>> ColumnsAsync(TestDatabase database, string table) =>
        (await QueryAsync(database, $"SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}'",
            r => r.GetString(0))).ToHashSet();
}
