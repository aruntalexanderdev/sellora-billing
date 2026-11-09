using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Api;

/// <summary>
/// Phase 6a: <see cref="StockController.Detail"/> and <see cref="ReportsController.StockMovement"/> expose ReasonCode and the
/// entered snapshot (frozen DTO contract), serialised camelCase with string enums like the API's JSON options; ReasonCode only
/// on Adjustment / OpeningStock rows, null entered fields for legacy rows, report type filter accepts the new types.
/// </summary>
public class StockMovementEnrichmentEndpointsTests
{
    private sealed record Fixture(SelloraDbContext Db, User User, Product Pen, ProductUnit Pc, ProductUnit Box);

    private static async Task<Fixture> SeedAsync()
    {
        var db = TestDb.NewInMemory();
        var user = TestUsers.Admin();
        var (pcUnit, boxUnit) = (TestUnits.Piece(), TestUnits.Box());
        ProductUnit pc = null!, box = null!;
        var pen = new ProductBuilder().WithSku("STN-PEN").WithName("Ball Pen").WithBaseUnit(pcUnit)
            .WithUnits(p => { pc = TestProductUnits.Base(p); box = TestProductUnits.Pack(p, boxUnit, pc, 10m); }).Build();
        db.AddRange(user.Role, user, pcUnit, boxUnit, pen.Tax, pen);
        await db.SaveChangesAsync();
        return new Fixture(db, user, pen, pc, box);
    }

    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static async Task<StockAdjustment> AdjustAsync(Fixture f, ProductUnit unit, AdjustmentType type, AdjustmentDirection dir, decimal qty, decimal? cost = null) =>
        await new StockAdjustmentService(f.Db).CreateAdjustmentAsync(new CreateAdjustmentInput(f.Pen.Id, unit.Id, type, dir, qty, cost, "t"), f.User.Id);

    /// <summary>Opening 3 Box (OpeningStock), Damage -5 pc (Adjustment) and one hand-written legacy Purchase row with no entered fields.</summary>
    private static async Task<Fixture> SeedHistoryAsync()
    {
        var f = await SeedAsync();
        await AdjustAsync(f, f.Box, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 3m, 30m);
        await AdjustAsync(f, f.Pc, AdjustmentType.Damage, AdjustmentDirection.Decrease, 5m);
        f.Db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(), ProductId = f.Pen.Id, Type = StockMovementType.Purchase, QuantityBaseUnits = 1m, ReferenceType = "PurchaseOrder",
            ReferenceId = Guid.NewGuid(), ReferenceNo = "PO-LEGACY", UserId = f.User.Id, OccurredAt = DateTimeOffset.UtcNow.AddYears(-1),
        });
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        return f;
    }

    private static T Ok<T>(ActionResult<T> result) => (T)Assert.IsType<OkObjectResult>(result.Result).Value!;

    [Fact]
    public async Task Detail_Movements_CarryReasonCodeAndEnteredSnapshotWithDecimalPlaces()
    {
        var f = await SeedHistoryAsync();

        var detail = Ok(await new StockController(f.Db).Detail(f.Pen.Id, default));

        var opening = Assert.Single(detail.Movements, m => m.Type == StockMovementType.OpeningStock);
        Assert.Equal((AdjustmentType?)AdjustmentType.OpeningStock, opening.ReasonCode);
        Assert.Equal((3m, f.Box.Id, "box", (decimal?)10m, (int?)0), (opening.EnteredQuantity, opening.EnteredProductUnitId, opening.EnteredUnitCode, opening.ConversionFactorSnapshot, opening.EnteredDecimalPlaces));
        var damage = Assert.Single(detail.Movements, m => m.Type == StockMovementType.Adjustment);
        Assert.Equal((AdjustmentType?)AdjustmentType.Damage, damage.ReasonCode);
        Assert.Equal((5m, f.Pc.Id, "pc", (decimal?)1m), (damage.EnteredQuantity, damage.EnteredProductUnitId, damage.EnteredUnitCode, damage.ConversionFactorSnapshot));
    }

    [Fact]
    public async Task Detail_LegacyMovement_HasNullReasonAndNullEnteredFields()
    {
        var f = await SeedHistoryAsync();

        var legacy = Assert.Single(Ok(await new StockController(f.Db).Detail(f.Pen.Id, default)).Movements, m => m.ReferenceNo == "PO-LEGACY");

        Assert.Null(legacy.ReasonCode);
        Assert.Null(legacy.EnteredQuantity);
        Assert.Null(legacy.EnteredProductUnitId);
        Assert.Null(legacy.EnteredUnitCode);
        Assert.Null(legacy.ConversionFactorSnapshot);
        Assert.Null(legacy.EnteredDecimalPlaces);
    }

    [Fact]
    public async Task Detail_ReasonCode_OnlyOnAdjustmentAndOpeningStockRows()
    {
        var f = await SeedHistoryAsync();

        var movements = Ok(await new StockController(f.Db).Detail(f.Pen.Id, default)).Movements;

        Assert.All(movements, m => Assert.Equal(m.Type is StockMovementType.Adjustment or StockMovementType.OpeningStock, m.ReasonCode is not null));
    }

    [Fact]
    public async Task Detail_SerialisedWithApiJsonOptions_UsesCamelCaseNamesAndStringEnums()
    {
        var f = await SeedHistoryAsync();
        var detail = Ok(await new StockController(f.Db).Detail(f.Pen.Id, default));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(detail, Api));
        var row = doc.RootElement.GetProperty("movements").EnumerateArray().Single(m => m.GetProperty("type").GetString() == "OpeningStock");

        Assert.Equal("OpeningStock", row.GetProperty("reasonCode").GetString());
        Assert.Equal(3m, row.GetProperty("enteredQuantity").GetDecimal());
        Assert.Equal(f.Box.Id, row.GetProperty("enteredProductUnitId").GetGuid());
        Assert.Equal("box", row.GetProperty("enteredUnitCode").GetString());
        Assert.Equal(10m, row.GetProperty("conversionFactorSnapshot").GetDecimal());
        Assert.Equal(0, row.GetProperty("enteredDecimalPlaces").GetInt32());
        var legacy = doc.RootElement.GetProperty("movements").EnumerateArray().Single(m => m.GetProperty("referenceNo").GetString() == "PO-LEGACY");
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("enteredProductUnitId").ValueKind);
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("reasonCode").ValueKind);
    }

    [Fact]
    public async Task StockMovementReport_Rows_CarryTheNewFieldsAndSerialiseCamelCase()
    {
        var f = await SeedHistoryAsync();

        var page = Ok(await new ReportsController(f.Db).StockMovement(null, null, null, null, 1, 20, default));

        Assert.Equal(3, page.Total);
        var opening = Assert.Single(page.Items, r => r.Type == StockMovementType.OpeningStock);
        Assert.Equal(((AdjustmentType?)AdjustmentType.OpeningStock, (decimal?)3m, (Guid?)f.Box.Id, "box", (decimal?)10m, (int?)0),
            (opening.ReasonCode, opening.EnteredQuantity, opening.EnteredProductUnitId, opening.EnteredUnitCode, opening.ConversionFactorSnapshot, opening.EnteredDecimalPlaces));
        var legacy = Assert.Single(page.Items, r => r.ReferenceNo == "PO-LEGACY");
        Assert.Null(legacy.ReasonCode);
        Assert.Null(legacy.EnteredProductUnitId);
        Assert.Null(legacy.EnteredDecimalPlaces);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(opening, Api));
        foreach (var name in new[] { "reasonCode", "enteredQuantity", "enteredProductUnitId", "enteredUnitCode", "conversionFactorSnapshot", "enteredDecimalPlaces" })
            Assert.True(doc.RootElement.TryGetProperty(name, out _), name);
    }

    [Theory]
    [InlineData(StockMovementType.OpeningStock, 1)]
    [InlineData(StockMovementType.Adjustment, 1)]
    [InlineData(StockMovementType.Purchase, 1)]
    [InlineData(StockMovementType.PurchaseCancellation, 0)]
    public async Task StockMovementReport_TypeFilter_AcceptsTheNewTypes(StockMovementType type, int expected)
    {
        var f = await SeedHistoryAsync();

        var page = Ok(await new ReportsController(f.Db).StockMovement(null, type, null, null, 1, 20, default));

        Assert.Equal(expected, page.Total);
        Assert.All(page.Items, r => Assert.Equal(type, r.Type));
    }

    [Fact]
    public async Task StockMovementReport_TypeFilterPurchaseCancellation_ReturnsCancellationRowsWithTheirSnapshot()
    {
        var f = await SeedAsync();
        f.Db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(), ProductId = f.Pen.Id, Type = StockMovementType.PurchaseCancellation, QuantityBaseUnits = -30m, ReferenceType = "PurchaseOrder",
            ReferenceId = Guid.NewGuid(), ReferenceNo = "PO-1", UserId = f.User.Id, EnteredQuantity = 3m, EnteredProductUnitId = f.Box.Id, EnteredUnitCode = "box", ConversionFactorSnapshot = 10m,
        });
        await f.Db.SaveChangesAsync();

        var row = Assert.Single(Ok(await new ReportsController(f.Db).StockMovement(null, StockMovementType.PurchaseCancellation, null, null, 1, 20, default)).Items);

        Assert.Equal((-30m, 3m, "box"), (row.QuantityBaseUnits, row.EnteredQuantity, row.EnteredUnitCode));
        Assert.Null(row.ReasonCode);
    }
}
