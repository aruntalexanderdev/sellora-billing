using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Api.Validation;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Api;

/// <summary>
/// <see cref="StockAdjustmentsController"/> run directly against EF InMemory (the pattern of
/// <see cref="ProductUnitEndpointsTests"/>): list filters, paging and ordering, the detail DTO (entered snapshot vs base
/// quantities, batches from movements), 404, and POST returning the full DTO — the same one on an idempotent replay.
/// </summary>
public class StockAdjustmentsControllerTests
{
    private sealed record Fixture(
        SelloraDbContext Db, User User, Product Pen, ProductUnit Pc, ProductUnit Box, ProductUnit FCtn, Product Rice, ProductUnit Kg);

    private static async Task<Fixture> SeedAsync()
    {
        var db = TestDb.NewInMemory();
        var user = TestUsers.Admin();
        var (pcUnit, boxUnit, hctnUnit, fctnUnit, kgUnit) = (TestUnits.Piece(), TestUnits.Box(), TestUnits.HalfCarton(), TestUnits.FullCarton(), TestUnits.Kilogram());
        ProductUnit pc = null!, box = null!, fctn = null!, kg = null!;
        var pen = new ProductBuilder().WithSku("STN-PEN").WithName("Ball Pen").WithBaseUnit(pcUnit)
            .WithUnits(p =>
            {
                pc = TestProductUnits.Base(p);
                box = TestProductUnits.Pack(p, boxUnit, pc, 10m, "Box of 10");
                var hctn = TestProductUnits.Pack(p, hctnUnit, box, 5m);
                fctn = TestProductUnits.Pack(p, fctnUnit, hctn, 2m);
            })
            .Build();
        var rice = new ProductBuilder().WithSku("RICE-BP10").WithName("Ponni Rice").WithBaseUnit(kgUnit).WithTax(pen.Tax)
            .WithUnits(p => kg = TestProductUnits.Base(p))
            .Build();
        db.AddRange(user.Role, user, pcUnit, boxUnit, hctnUnit, fctnUnit, kgUnit, pen.Tax, pen, rice);
        await db.SaveChangesAsync();
        return new Fixture(db, user, pen, pc, box, fctn, rice, kg);
    }

    private static StockAdjustmentsController Controller(Fixture f, string? idempotencyKey = null)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, f.User.Id.ToString())], "test")),
        };
        if (idempotencyKey is not null) http.Request.Headers[IdempotencyHeader.Name] = idempotencyKey;
        return new StockAdjustmentsController(new StockAdjustmentService(f.Db), f.Db) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static T Ok<T>(ActionResult<T> result) => (T)Assert.IsType<OkObjectResult>(result.Result).Value!;

    private static async Task<StockAdjustment> PostAsync(Fixture f, ProductUnit unit, AdjustmentType type, AdjustmentDirection direction, decimal qty,
        decimal? cost = null, DateTimeOffset? createdAt = null)
    {
        var adjustment = await new StockAdjustmentService(f.Db).CreateAdjustmentAsync(
            new CreateAdjustmentInput(unit.ProductId, unit.Id, type, direction, qty, cost, "Counted"), f.User.Id);
        if (createdAt is { } at)
        {
            adjustment.CreatedAt = at;
            await f.Db.SaveChangesAsync();
        }
        return adjustment;
    }

    private static Task<ActionResult<PagedResult<StockAdjustmentListItemDto>>> ListAsync(Fixture f, string? search = null, Guid? productId = null,
        AdjustmentType? type = null, AdjustmentDirection? direction = null, DateOnly? dateFrom = null, DateOnly? dateTo = null, int page = 1, int pageSize = 20)
    {
        f.Db.ChangeTracker.Clear();
        return Controller(f).List(search, productId, type, direction, dateFrom, dateTo, page, pageSize, default);
    }

    private static readonly DateTimeOffset Sep1 = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Four adjustments on two products over three days: ADJ-0001 (pen, 3 FCtn opening, Sep 1), ADJ-0002 (rice, 12.5 kg
    /// opening, Sep 2), ADJ-0003 (pen, 2 Box damage, Sep 2 late), ADJ-0004 (rice, 0.75 kg correction decrease, Sep 3 00:00 UTC).</summary>
    private static async Task<Fixture> SeedHistoryAsync()
    {
        var f = await SeedAsync();
        await PostAsync(f, f.FCtn, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 3m, 250m, Sep1);
        await PostAsync(f, f.Kg, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 12.5m, 60m, Sep1.AddDays(1));
        await PostAsync(f, f.Box, AdjustmentType.Damage, AdjustmentDirection.Decrease, 2m, null, Sep1.AddDays(1).AddHours(13).AddMinutes(59));
        await PostAsync(f, f.Kg, AdjustmentType.Correction, AdjustmentDirection.Decrease, 0.75m, null, new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero));
        return f;
    }

    [Fact]
    public async Task List_NoFilters_NewestFirstWithEnteredAndBaseQuantities()
    {
        var f = await SeedHistoryAsync();

        var page = Ok(await ListAsync(f));

        Assert.Equal((4, 1, 20), (page.Total, page.Page, page.PageSize));
        Assert.Equal(new[] { "ADJ-0004", "ADJ-0003", "ADJ-0002", "ADJ-0001" }, page.Items.Select(i => i.AdjustmentNo));
        var opening = page.Items.Single(i => i.AdjustmentNo == "ADJ-0001");
        Assert.Equal((f.Pen.Id, "Ball Pen", "STN-PEN", AdjustmentType.OpeningStock, AdjustmentDirection.Increase),
            (opening.ProductId, opening.ProductName, opening.Sku, opening.Type, opening.Direction));
        Assert.Equal((3m, "fctn", (short)0, 300m, "pc", (short)0), (opening.EnteredQuantity, opening.UnitCode, opening.EnteredDecimalPlaces,
            opening.QuantityBaseUnits, opening.BaseUnitCode, opening.BaseDecimalPlaces));
        Assert.Equal(((decimal?)250m, (decimal?)2.5m, 0m, 300m, "Ravi Krishnan"), (opening.EnteredUnitCost, opening.UnitCost, opening.BeforeQty, opening.AfterQty, opening.UserName));
        var damage = page.Items.Single(i => i.AdjustmentNo == "ADJ-0003");
        Assert.Equal((2m, "box", 20m, (decimal?)null, (decimal?)null, 300m, 280m), (damage.EnteredQuantity, damage.UnitCode, damage.QuantityBaseUnits,
            damage.EnteredUnitCost, damage.UnitCost, damage.BeforeQty, damage.AfterQty));
        var correction = page.Items.Single(i => i.AdjustmentNo == "ADJ-0004");
        Assert.Equal((0.75m, "kg", (short)3, 0.75m, "kg", (short)3), (correction.EnteredQuantity, correction.UnitCode, correction.EnteredDecimalPlaces,
            correction.QuantityBaseUnits, correction.BaseUnitCode, correction.BaseDecimalPlaces));
    }

    [Theory]
    [InlineData("adj-0002", new[] { "ADJ-0002" })]
    [InlineData("  BALL ", new[] { "ADJ-0003", "ADJ-0001" })] // product name, trimmed, case-insensitive
    [InlineData("rice-bp", new[] { "ADJ-0004", "ADJ-0002" })]  // SKU
    [InlineData("nothing", new string[0])]
    public async Task List_Search_MatchesAdjustmentNoProductNameOrSku(string search, string[] expected)
    {
        var f = await SeedHistoryAsync();

        var page = Ok(await ListAsync(f, search: search));

        Assert.Equal(expected, page.Items.Select(i => i.AdjustmentNo));
        Assert.Equal(expected.Length, page.Total);
    }

    [Fact]
    public async Task List_ProductTypeAndDirectionFilters_Combine()
    {
        var f = await SeedHistoryAsync();

        Assert.Equal(new[] { "ADJ-0003", "ADJ-0001" }, Ok(await ListAsync(f, productId: f.Pen.Id)).Items.Select(i => i.AdjustmentNo));
        Assert.Equal(new[] { "ADJ-0002", "ADJ-0001" }, Ok(await ListAsync(f, type: AdjustmentType.OpeningStock)).Items.Select(i => i.AdjustmentNo));
        Assert.Equal(new[] { "ADJ-0004", "ADJ-0003" }, Ok(await ListAsync(f, direction: AdjustmentDirection.Decrease)).Items.Select(i => i.AdjustmentNo));
        Assert.Equal(new[] { "ADJ-0004" }, Ok(await ListAsync(f, productId: f.Rice.Id, direction: AdjustmentDirection.Decrease)).Items.Select(i => i.AdjustmentNo));
        Assert.Empty(Ok(await ListAsync(f, productId: f.Pen.Id, type: AdjustmentType.Correction)).Items);
    }

    [Fact]
    public async Task List_DateRange_InclusiveUtcDayBounds()
    {
        var f = await SeedHistoryAsync();
        var sep2 = new DateOnly(2026, 9, 2);

        Assert.Equal(new[] { "ADJ-0003", "ADJ-0002" }, Ok(await ListAsync(f, dateFrom: sep2, dateTo: sep2)).Items.Select(i => i.AdjustmentNo));
        Assert.Equal(new[] { "ADJ-0004", "ADJ-0003", "ADJ-0002" }, Ok(await ListAsync(f, dateFrom: sep2)).Items.Select(i => i.AdjustmentNo));
        Assert.Equal(new[] { "ADJ-0001" }, Ok(await ListAsync(f, dateTo: new DateOnly(2026, 9, 1))).Items.Select(i => i.AdjustmentNo));
        Assert.Equal(new[] { "ADJ-0004" }, Ok(await ListAsync(f, dateFrom: new DateOnly(2026, 9, 3), dateTo: new DateOnly(2026, 9, 3))).Items.Select(i => i.AdjustmentNo));
    }

    [Fact]
    public async Task List_Paging_SlicesNewestFirstAndKeepsTotal()
    {
        var f = await SeedHistoryAsync();

        var first = Ok(await ListAsync(f, page: 1, pageSize: 3));
        var second = Ok(await ListAsync(f, page: 2, pageSize: 3));

        Assert.Equal((4, 3), (first.Total, first.PageSize));
        Assert.Equal(new[] { "ADJ-0004", "ADJ-0003", "ADJ-0002" }, first.Items.Select(i => i.AdjustmentNo));
        Assert.Equal(new[] { "ADJ-0001" }, second.Items.Select(i => i.AdjustmentNo));
        Assert.Empty(Ok(await ListAsync(f, page: 3, pageSize: 3)).Items);
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-4, 1000, 1, 100)]
    public async Task List_OutOfRangePaging_Clamped(int page, int pageSize, int expectedPage, int expectedPageSize)
    {
        var f = await SeedHistoryAsync();

        var result = Ok(await ListAsync(f, page: page, pageSize: pageSize));

        Assert.Equal((expectedPage, expectedPageSize), (result.Page, result.PageSize));
    }

    [Fact]
    public async Task List_SameCreatedAt_TieBrokenByAdjustmentNoDescending()
    {
        var f = await SeedAsync();
        await PostAsync(f, f.Pc, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 5m, 2m, Sep1);
        await PostAsync(f, f.Pc, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 5m, 2m, Sep1);

        Assert.Equal(new[] { "ADJ-0002", "ADJ-0001" }, Ok(await ListAsync(f)).Items.Select(i => i.AdjustmentNo));
    }

    [Fact]
    public async Task Get_UnknownId_NotFound()
    {
        var f = await SeedAsync();

        var result = await Controller(f).Get(Guid.NewGuid(), default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Get_IncreaseInFullCartons_EnteredSnapshotBaseQuantitiesAndItsBatch()
    {
        var f = await SeedAsync();
        var adjustment = await PostAsync(f, f.FCtn, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 3m, 250m);
        var batch = await f.Db.PurchaseBatches.AsNoTracking().SingleAsync();
        f.Db.ChangeTracker.Clear();

        var dto = Ok(await Controller(f).Get(adjustment.Id, default));

        Assert.Equal((adjustment.Id, adjustment.AdjustmentNo, f.Pen.Id, "Ball Pen", "STN-PEN"), (dto.Id, dto.AdjustmentNo, dto.ProductId, dto.ProductName, dto.Sku));
        Assert.Equal((3m, f.FCtn.Id, "fctn", "Full Carton", 100m, (short)0), (dto.EnteredQuantity, dto.ProductUnitId, dto.UnitCode, dto.UnitDisplayName,
            dto.ConversionFactorToBase, dto.EnteredDecimalPlaces));
        Assert.Equal((300m, "pc", (short)0, (decimal?)250m, (decimal?)2.5m), (dto.QuantityBaseUnits, dto.BaseUnitCode, dto.BaseDecimalPlaces, dto.EnteredUnitCost, dto.UnitCost));
        Assert.Equal(("Counted", 0m, 300m, "Ravi Krishnan"), (dto.Reason, dto.BeforeQty, dto.AfterQty, dto.UserName));
        var line = Assert.Single(dto.Batches);
        Assert.Equal((batch.Id, batch.InternalBatchNo, 300m, 2.5m), (line.PurchaseBatchId, line.InternalBatchNo, line.QuantityBaseUnits, line.UnitCost));
    }

    [Fact]
    public async Task Get_DecreaseAcrossTwoBatches_SignedBatchDrawsOldestFirstAndLabelDisplayName()
    {
        var f = await SeedAsync();
        await PostAsync(f, f.Pc, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 15m, 2m, Sep1);
        await PostAsync(f, f.FCtn, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 1m, 300m, Sep1.AddHours(1));
        var batches = await f.Db.PurchaseBatches.AsNoTracking().OrderBy(b => b.CreatedAt).ToListAsync();
        var damage = await PostAsync(f, f.Box, AdjustmentType.Damage, AdjustmentDirection.Decrease, 2m);
        f.Db.ChangeTracker.Clear();

        var dto = Ok(await Controller(f).Get(damage.Id, default));

        Assert.Equal((2m, "box", "Box of 10", 10m, 20m), (dto.EnteredQuantity, dto.UnitCode, dto.UnitDisplayName, dto.ConversionFactorToBase, dto.QuantityBaseUnits));
        Assert.Equal(((decimal?)null, (decimal?)null), (dto.EnteredUnitCost, dto.UnitCost));
        Assert.Equal(new[] { (batches[0].Id, -15m, 2m), (batches[1].Id, -5m, 3m) }, dto.Batches.Select(b => (b.PurchaseBatchId, b.QuantityBaseUnits, b.UnitCost)));
    }

    [Fact]
    public async Task Get_UnitCodeChangedInUnitMasterAfterPosting_DtoShowsPostingTimeCode()
    {
        var f = await SeedAsync();
        var adjustment = await PostAsync(f, f.Box, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 2m, 30m);
        var boxUnit = await f.Db.Units.SingleAsync(u => u.Id == f.Box.UnitId);
        boxUnit.ShortCode = "bx";
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        var dto = Ok(await Controller(f).Get(adjustment.Id, default));

        Assert.Equal("box", dto.UnitCode);
    }

    [Fact]
    public async Task Create_BoxIncrease_ReturnsFullDtoAndReplayWithSameKeyReturnsTheSameDto()
    {
        var f = await SeedAsync();
        var request = new CreateStockAdjustmentRequest(f.Pen.Id, f.Box.Id, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 4m, 30m, "  Opening count  ");

        var created = Ok(await Controller(f, "pen-open-1").Create(request, default));
        f.Db.ChangeTracker.Clear();
        var replayed = Ok(await Controller(f, "pen-open-1").Create(request, default));

        Assert.Equal((4m, "box", 10m, 40m, (decimal?)30m, (decimal?)3m, "Opening count"), (created.EnteredQuantity, created.UnitCode, created.ConversionFactorToBase,
            created.QuantityBaseUnits, created.EnteredUnitCost, created.UnitCost, created.Reason));
        Assert.Single(created.Batches);
        Assert.Equal(created.Id, replayed.Id);
        Assert.Equal(created with { Batches = [] }, replayed with { Batches = [] });
        Assert.Equal(created.Batches, replayed.Batches);
        Assert.Equal(1, await f.Db.StockAdjustments.CountAsync());
        Assert.Equal(40m, (await f.Db.StockItems.AsNoTracking().SingleAsync()).QuantityOnHand);
    }

    [Theory]
    [InlineData(1.5, "Quantity")]
    [InlineData(1, "ProductUnitId")]
    public async Task Create_DomainError_ValidationProblemKeyedByRequestField(double qty, string field)
    {
        var f = await SeedAsync();
        var unitId = field == "ProductUnitId" ? f.Kg.Id : f.Box.Id; // rice's unit on the pen
        var request = new CreateStockAdjustmentRequest(f.Pen.Id, unitId, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, (decimal)qty, 30m, null);

        var result = await Controller(f).Create(request, default);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal(new[] { field }, problem.Errors.Keys);
        Assert.Empty(await f.Db.StockAdjustments.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Create_OverlongIdempotencyKey_400AndNothingPosted()
    {
        var f = await SeedAsync();
        var request = new CreateStockAdjustmentRequest(f.Pen.Id, f.Box.Id, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 1m, 30m, null);

        var result = await Controller(f, new string('k', 101)).Create(request, default);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode);
        Assert.Empty(await f.Db.StockAdjustments.AsNoTracking().ToListAsync());
    }
}
