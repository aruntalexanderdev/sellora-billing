using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Api.ErrorHandling;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Companies;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Api;

/// <summary>Phase 11a WP-G: X-Correlation-Id middleware (G1), ReferenceId on movement DTOs (G2), stockLocationId filter on the adjustments list (G3).</summary>
public class Phase11aGapsTests
{
    // ---- G1 -------------------------------------------------------------------------------------------------------

    // DefaultHttpContext never fires Response.OnStarting; this feature does (on StartAsync / first write), like Kestrel.
    private sealed class StartingResponseFeature : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _starting = [];
        private bool _started;
        public override bool HasStarted => _started;
        public override void OnStarting(Func<object, Task> callback, object state) => _starting.Add((callback, state));
        public async Task FireAsync()
        {
            if (_started) return;
            _started = true;
            for (var i = _starting.Count - 1; i >= 0; i--) await _starting[i].Callback(_starting[i].State);
        }
    }

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        var feature = new StartingResponseFeature();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(feature);
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(new FiringBodyFeature(feature, new MemoryStream()));
        return context;
    }

    private sealed class FiringBodyFeature(StartingResponseFeature response, Stream stream)
        : Microsoft.AspNetCore.Http.StreamResponseBodyFeature(stream)
    {
        public override async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await response.FireAsync();
            await base.StartAsync(cancellationToken);
        }
    }

    private static async Task<HttpContext> RunMiddlewareAsync(string? header)
    {
        var context = NewContext();
        if (header is not null) context.Request.Headers[CorrelationIdMiddleware.HeaderName] = header;
        await new CorrelationIdMiddleware(_ => context.Response.StartAsync()).InvokeAsync(context);
        return context;
    }

    [Fact]
    public async Task Middleware_ValidHeader_IsAdoptedAndEchoed()
    {
        var context = await RunMiddlewareAsync("abc12345-def-6789");

        Assert.Equal("abc12345-def-6789", context.TraceIdentifier);
        Assert.Equal("abc12345-def-6789", context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has spaces in it 123")]
    [InlineData("bad_chars!!/12345678")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 66 chars
    public async Task Middleware_InvalidHeader_IsIgnoredAndGeneratedIdEchoed(string header)
    {
        var context = NewContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = header;
        var before = context.TraceIdentifier;

        await new CorrelationIdMiddleware(_ => context.Response.StartAsync()).InvokeAsync(context);

        Assert.Equal(before, context.TraceIdentifier);
        Assert.NotEqual(header, context.TraceIdentifier);
        Assert.Equal(before, context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
        Assert.False(string.IsNullOrEmpty(before));
    }

    [Fact]
    public async Task ProblemDetails_On400_CarriesTheClientCorrelationId()
    {
        var context = NewContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "client-trace-0001";
        await new CorrelationIdMiddleware(async ctx =>
        {
            await new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance)
                .TryHandleAsync(ctx, new DomainValidationException("Lines", "Bad."), CancellationToken.None);
        }).InvokeAsync(context);
        await context.Response.CompleteAsync(); // the server completes the response: starting callbacks run if not yet run

        context.Response.Body.Position = 0;
        var body = (await JsonDocument.ParseAsync(context.Response.Body)).RootElement;
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal("client-trace-0001", body.GetProperty("traceId").GetString());
        Assert.Equal("client-trace-0001", context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
    }

    // ---- G2 / G3 --------------------------------------------------------------------------------------------------

    private sealed record Fixture(SelloraDbContext Db, User User, Product Pen, ProductUnit Pc, Guid SaleRef, Guid TransferRef);

    private static async Task<Fixture> SeedAsync()
    {
        var db = TestDb.NewInMemory();
        var user = TestUsers.Admin();
        var pcUnit = TestUnits.Piece();
        ProductUnit pc = null!;
        var pen = new ProductBuilder().WithSku("STN-PEN").WithName("Ball Pen").WithBaseUnit(pcUnit).WithUnits(p => pc = TestProductUnits.Base(p)).Build();
        db.AddRange(user.Role, user, pcUnit, pen.Tax, pen);
        await db.SaveChangesAsync();
        var (saleRef, transferRef) = (Guid.NewGuid(), Guid.NewGuid());
        foreach (var (type, refType, id, no) in new[]
        {
            (StockMovementType.Sale, "SalesInvoice", saleRef, "INV-1"),
            (StockMovementType.TransferOut, "StockTransfer", transferRef, "TRF-1"),
        })
        {
            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(), ProductId = pen.Id, StockLocationId = DefaultTenant.MainStoreLocationId, Type = type, QuantityBaseUnits = -1m,
                ReferenceType = refType, ReferenceId = id, ReferenceNo = no, UserId = user.Id, OccurredAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Fixture(db, user, pen, pc, saleRef, transferRef);
    }

    [Fact]
    public async Task StockDetail_Movements_CarryReferenceId()
    {
        var f = await SeedAsync();

        var result = await new StockController(f.Db, new FakeEffectivePermissions("Inventory.View", "Reports.Stock", "Reports.Movement")).Detail(f.Pen.Id, default);

        var movements = ((StockDetailDto)Assert.IsType<OkObjectResult>(result.Result).Value!).Movements;
        Assert.Equal(f.SaleRef, Assert.Single(movements, m => m.ReferenceNo == "INV-1").ReferenceId);
        Assert.Equal(f.TransferRef, Assert.Single(movements, m => m.ReferenceNo == "TRF-1").ReferenceId);
    }

    [Fact]
    public async Task MovementReport_Rows_CarryReferenceId()
    {
        var f = await SeedAsync();

        var result = await new ReportsController(f.Db, new FakeEffectivePermissions("Inventory.View", "Reports.Stock", "Reports.Movement")).StockMovement(null, null, null, null, 1, 20, default);

        var items = ((PagedResult<StockMovementReportRowDto>)Assert.IsType<OkObjectResult>(result.Result).Value!).Items;
        Assert.Equal(f.SaleRef, Assert.Single(items, m => m.ReferenceNo == "INV-1").ReferenceId);
        Assert.Equal(f.TransferRef, Assert.Single(items, m => m.ReferenceNo == "TRF-1").ReferenceId);
    }

    [Fact]
    public async Task AdjustmentsList_StockLocationFilter_Narrows()
    {
        var f = await SeedAsync();
        var service = new StockAdjustmentService(f.Db);
        var first = await service.CreateAdjustmentAsync(new CreateAdjustmentInput(f.Pen.Id, f.Pc.Id, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 5m, 1m, "a"), f.User.Id);
        var second = await service.CreateAdjustmentAsync(new CreateAdjustmentInput(f.Pen.Id, f.Pc.Id, AdjustmentType.OpeningStock, AdjustmentDirection.Increase, 5m, 1m, "b"), f.User.Id);
        var otherLocation = Guid.NewGuid();
        var tracked = await f.Db.StockAdjustments.SingleAsync(a => a.Id == second.Id);
        tracked.StockLocationId = otherLocation;
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, f.User.Id.ToString())], "test")),
        };
        var controller = new StockAdjustmentsController(new StockAdjustmentService(f.Db), f.Db) { ControllerContext = new ControllerContext { HttpContext = http } };

        var all = (PagedResult<StockAdjustmentListItemDto>)Assert.IsType<OkObjectResult>((await controller.List(null, null, null, null, null, null, 1, 20, null, null, default)).Result).Value!;
        f.Db.ChangeTracker.Clear();
        var filtered = (PagedResult<StockAdjustmentListItemDto>)Assert.IsType<OkObjectResult>((await controller.List(null, null, null, null, null, null, 1, 20, null, otherLocation, default)).Result).Value!;

        Assert.Equal(2, all.Total);
        Assert.Equal(second.Id, Assert.Single(filtered.Items).Id);
        Assert.NotEqual(first.Id, filtered.Items[0].Id);
    }
}
