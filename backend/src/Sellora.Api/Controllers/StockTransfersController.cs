using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Validation;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Stock;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Controllers;

/// <summary>
/// Phase 9 stock transfers. No class-level policy (policies combine with AND): each action names its own permission. Reads are scoped by the
/// "Branch" query filter on StockTransfers (creating, source or destination branch); a hidden transfer is a 404. Write logic lives in
/// <see cref="IStockTransferService"/>; every write answers with the shared detail projection so a fresh post and an idempotent replay
/// return the same shape.
/// </summary>
[ApiController]
[Route("api/v1/stock-transfers")]
public class StockTransfersController(IStockTransferService transfers, SelloraDbContext db, BranchContext branchContext) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "StockTransfers.View")]
    public async Task<ActionResult<PagedResult<StockTransferListItemDto>>> List(
        [FromQuery] StockTransferStatus? status, [FromQuery] Guid? sourceLocationId, [FromQuery] Guid? destinationLocationId,
        [FromQuery] string? direction, [FromQuery] DateOnly? dateFrom, [FromQuery] DateOnly? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var dir = direction?.Trim().ToLowerInvariant();
        if (dir is not (null or "" or "incoming" or "outgoing"))
        {
            ModelState.AddModelError("direction", "Direction must be 'incoming' or 'outgoing'.");
            return ValidationProblem(ModelState);
        }

        // Filters are applied to the root query before projection.
        var query = db.StockTransfers.AsNoTracking().AsQueryable();
        if (status is not null) query = query.Where(t => t.Status == status);
        if (sourceLocationId is not null) query = query.Where(t => t.SourceLocationId == sourceLocationId);
        if (destinationLocationId is not null) query = query.Where(t => t.DestinationLocationId == destinationLocationId);

        // incoming/outgoing are relative to the caller's working branch; without one (company-wide, no X-Branch-Id) there is nothing to be relative to.
        var working = branchContext.RequestedBranchId ?? branchContext.PrimaryBranchId;
        if (working is { } w)
        {
            if (dir == "incoming") query = query.Where(t => t.DestinationBranchId == w);
            else if (dir == "outgoing") query = query.Where(t => t.SourceBranchId == w);
        }

        if (dateFrom is not null)
        {
            var from = new DateTimeOffset(dateFrom.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(t => t.CreatedAt >= from);
        }
        if (dateTo is not null)
        {
            var to = new DateTimeOffset(dateTo.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(t => t.CreatedAt < to);
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.TransferNo)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(t => new
            {
                t.Id, t.TransferNo, t.Status, t.CreatedAt, t.BranchId,
                t.SourceLocationId, t.DestinationLocationId, t.SourceBranchId, t.DestinationBranchId,
                LineCount = t.Lines.Count,
                OutstandingLineCount = t.Lines.Count(l =>
                    l.DispatchedBaseQty - l.ReceivedBaseQty - l.DamagedBaseQty - l.WrittenOffBaseQty - l.ReturnedBaseQty > 0),
                t.DispatchedAt, t.CompletedAt,
            })
            .ToListAsync(ct);

        var branches = await BranchLabels.LoadAsync(db, ct);
        var locations = await LocationLookup.LoadAsync(db, ct);
        var items = rows.Select(r => new StockTransferListItemDto(
            r.Id, r.TransferNo, r.Status, r.CreatedAt,
            Branch(branches, r.BranchId),
            locations.Dto(r.SourceLocationId), locations.Dto(r.DestinationLocationId),
            Branch(branches, r.SourceBranchId), Branch(branches, r.DestinationBranchId),
            r.LineCount, r.OutstandingLineCount, r.DispatchedAt, r.CompletedAt)).ToList();

        return Ok(new PagedResult<StockTransferListItemDto>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "StockTransfers.View")]
    public async Task<ActionResult<StockTransferDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await LoadDtoAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpPost]
    [Authorize(Policy = "StockTransfers.Create")]
    public async Task<ActionResult<StockTransferDto>> Create(CreateStockTransferRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);

        var input = new CreateStockTransferInput(
            request.SourceLocationId!.Value, request.DestinationLocationId!.Value, TrimOrNull(request.Notes), request.Submit,
            MapLines(request.Lines), key);

        return await RunAsync<StockTransferDto>(async () =>
        {
            var transfer = await transfers.CreateAsync(input, UserId(), HttpContext.TraceIdentifier, ct);
            var dto = await LoadDtoAsync(transfer.Id, ct);
            if (dto is null) return NotFound();
            return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
        });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "StockTransfers.Create")]
    public async Task<ActionResult<StockTransferDto>> Update(Guid id, UpdateStockTransferRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var input = new UpdateStockTransferInput(
            request.SourceLocationId!.Value, request.DestinationLocationId!.Value, TrimOrNull(request.Notes), MapLines(request.Lines));

        return await RunAsync(async () =>
            await RespondAsync(await transfers.UpdateDraftAsync(id, input, UserId(), HttpContext.TraceIdentifier, ct), ct));
    }

    [HttpPost("{id:guid}/submit")]
    [Authorize(Policy = "StockTransfers.Create")]
    public Task<ActionResult<StockTransferDto>> Submit(Guid id, CancellationToken ct) =>
        RunAsync(async () => await RespondAsync(await transfers.SubmitAsync(id, UserId(), HttpContext.TraceIdentifier, ct), ct));

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = "StockTransfers.Approve")]
    public Task<ActionResult<StockTransferDto>> Approve(Guid id, CancellationToken ct) =>
        RunAsync(async () => await RespondAsync(await transfers.ApproveAsync(id, UserId(), HttpContext.TraceIdentifier, ct), ct));

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = "StockTransfers.Approve")]
    public async Task<ActionResult<StockTransferDto>> Reject(Guid id, StockTransferReasonRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return await RunAsync(async () =>
            await RespondAsync(await transfers.RejectAsync(id, request.Reason.Trim(), UserId(), HttpContext.TraceIdentifier, ct), ct));
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = "StockTransfers.Cancel")]
    public async Task<ActionResult<StockTransferDto>> Cancel(Guid id, StockTransferReasonRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return await RunAsync(async () =>
            await RespondAsync(await transfers.CancelAsync(id, request.Reason.Trim(), UserId(), HttpContext.TraceIdentifier, ct), ct));
    }

    [HttpPost("{id:guid}/dispatch")]
    [Authorize(Policy = "StockTransfers.Dispatch")]
    public async Task<ActionResult<StockTransferDto>> Dispatch(Guid id, DispatchStockTransferRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);

        var input = new DispatchStockTransferInput(
            request.Lines.Select(l => new DispatchStockTransferLineInput(l.LineId!.Value, l.ProductUnitId!.Value, l.Quantity)).ToList(), key);

        return await RunAsync(async () =>
            await RespondAsync(await transfers.DispatchAsync(id, input, UserId(), HttpContext.TraceIdentifier, ct), ct));
    }

    [HttpPost("{id:guid}/receipts")]
    [Authorize(Policy = "StockTransfers.Receive")]
    public async Task<ActionResult<StockTransferReceiptResultDto>> Receive(Guid id, ReceiveStockTransferRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);

        var input = new ReceiveStockTransferInput(
            TrimOrNull(request.Notes),
            request.Lines.Select(l => new ReceiveStockTransferLineInput(l.LineId!.Value, l.ProductUnitId!.Value, l.AcceptedQuantity, l.DamagedQuantity)).ToList(),
            key);

        return await RunAsync(async () =>
            await RespondReceiptAsync(id, await transfers.ReceiveAsync(id, input, UserId(), HttpContext.TraceIdentifier, ct), ct));
    }

    [HttpPost("{id:guid}/resolve")]
    [Authorize(Policy = "StockTransfers.Resolve")]
    public async Task<ActionResult<StockTransferReceiptResultDto>> Resolve(Guid id, ResolveStockTransferRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);

        var input = new ResolveStockTransferInput(
            request.Reason.Trim(),
            request.Lines.Select(l => new ResolveStockTransferLineInput(l.LineId!.Value, l.Action)).ToList(),
            key);

        return await RunAsync(async () =>
            await RespondReceiptAsync(id, await transfers.ResolveAsync(id, input, UserId(), HttpContext.TraceIdentifier, ct), ct));
    }

    // ---- helpers ----

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private static string? TrimOrNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static List<StockTransferLineInput> MapLines(IEnumerable<StockTransferLineRequest> lines) =>
        lines.Select(l => new StockTransferLineInput(l.ProductId!.Value, l.ProductUnitId!.Value, l.Quantity)).ToList();

    private static StockTransferBranchDto Branch(BranchLabels labels, Guid? id) => new(id, labels.Code(id), labels.Name(id));

    /// <summary>DomainValidationException → 400 ValidationProblemDetails keyed by the request field.</summary>
    private async Task<ActionResult<T>> RunAsync<T>(Func<Task<ActionResult<T>>> action)
    {
        try
        {
            return await action();
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }

    private async Task<ActionResult<StockTransferDto>> RespondAsync(StockTransfer? transfer, CancellationToken ct)
    {
        if (transfer is null) return NotFound();
        var dto = await LoadDtoAsync(transfer.Id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    private async Task<ActionResult<StockTransferReceiptResultDto>> RespondReceiptAsync(Guid id, StockTransferReceiptResult? result, CancellationToken ct)
    {
        if (result is null) return NotFound();
        var dto = await LoadDtoAsync(id, ct);
        return dto is null ? NotFound() : Ok(new StockTransferReceiptResultDto(result.ReceiptId, dto));
    }

    /// <summary>Shared detail projection. The root is filtered (Branch filter hides foreign transfers); children are unfiltered
    /// collections loaded with the root, and labels come from per-request lookups (location and branch FKs are navigation-less).</summary>
    private async Task<StockTransferDto?> LoadDtoAsync(Guid id, CancellationToken ct)
    {
        var t = await db.StockTransfers.AsNoTracking().AsSplitQuery()
            .Include(x => x.Lines).ThenInclude(l => l.Batches)
            .Include(x => x.Receipts).ThenInclude(r => r.Lines)
            .Where(x => x.Id == id)
            .SingleOrDefaultAsync(ct);
        if (t is null) return null;

        var branches = await BranchLabels.LoadAsync(db, ct);
        var locations = await LocationLookup.LoadAsync(db, ct);

        var productIds = t.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Sku, BaseUnitCode = p.BaseUnit.ShortCode, BaseDecimalPlaces = p.BaseUnit.DecimalPlaces })
            .ToDictionaryAsync(p => p.Id, ct);

        var batchIds = t.Lines.SelectMany(l => l.Batches).Select(b => b.PurchaseBatchId).Distinct().ToList();
        var batchSeq = await db.PurchaseBatches.AsNoTracking()
            .Where(b => batchIds.Contains(b.Id))
            .Select(b => new { b.Id, b.BatchSequence })
            .ToDictionaryAsync(b => b.Id, b => b.BatchSequence, ct);

        var userIds = new HashSet<Guid> { t.CreatedByUserId };
        foreach (var u in new[] { t.SubmittedByUserId, t.ApprovedByUserId, t.RejectedByUserId, t.DispatchedByUserId, t.CompletedByUserId, t.CancelledByUserId })
        {
            if (u is { } uid) userIds.Add(uid);
        }
        foreach (var r in t.Receipts) userIds.Add(r.ByUserId);
        var idList = userIds.ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => idList.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName })
            .ToDictionaryAsync(u => u.Id, u => (u.FirstName + " " + u.LastName).Trim(), ct);

        string? UserName(Guid? uid) => uid is { } x && users.TryGetValue(x, out var n) ? n : null;
        StockTransferActorDto? Actor(Guid? uid, DateTimeOffset? at) => at is null && uid is null ? null : new(uid, UserName(uid), at);

        var lines = t.Lines.OrderBy(l => l.LineNo).Select(l =>
        {
            products.TryGetValue(l.ProductId, out var p);
            var batches = l.Batches
                .OrderBy(b => batchSeq.GetValueOrDefault(b.PurchaseBatchId))
                .Select(b => new StockTransferLineBatchDto(
                    b.PurchaseBatchId,
                    batchSeq.TryGetValue(b.PurchaseBatchId, out var seq) ? PurchaseBatch.FormatInternalBatchNo(seq) : "",
                    b.UnitCost, b.DispatchedBaseQty, b.ReceivedBaseQty, b.DamagedBaseQty, b.WrittenOffBaseQty, b.ReturnedBaseQty,
                    b.DispatchedBaseQty - b.ReceivedBaseQty - b.DamagedBaseQty - b.WrittenOffBaseQty - b.ReturnedBaseQty))
                .ToList();
            return new StockTransferLineDto(
                l.Id, l.LineNo, l.ProductId, p?.Name, p?.Sku, p?.BaseUnitCode, p?.BaseDecimalPlaces,
                l.ProductUnitId, l.UnitCode, l.ConversionFactorToBase, l.RequestedQty, l.RequestedBaseQty,
                l.DispatchProductUnitId, l.DispatchUnitCode, l.DispatchFactor, l.DispatchedQty, l.DispatchedBaseQty,
                l.ReceivedBaseQty, l.DamagedBaseQty, l.WrittenOffBaseQty, l.ReturnedBaseQty,
                l.DispatchedBaseQty - l.ReceivedBaseQty - l.DamagedBaseQty - l.WrittenOffBaseQty - l.ReturnedBaseQty,
                batches);
        }).ToList();

        var receipts = t.Receipts.OrderBy(r => r.Sequence).Select(r => new StockTransferReceiptDto(
            r.Id, r.Sequence, r.Kind, r.At, r.ByUserId, UserName(r.ByUserId), r.Notes,
            r.Lines.Select(rl => new StockTransferReceiptLineDto(
                rl.TransferLineId, rl.ProductId, rl.ProductUnitId, rl.UnitCode, rl.ConversionFactorToBase,
                rl.AcceptedQty, rl.AcceptedBaseQty, rl.DamagedQty, rl.DamagedBaseQty, rl.WrittenOffBaseQty, rl.ReturnedBaseQty)).ToList())).ToList();

        return new StockTransferDto(
            t.Id, t.TransferNo, t.Status, t.Notes,
            Branch(branches, t.BranchId),
            locations.Dto(t.SourceLocationId), locations.Dto(t.DestinationLocationId), locations.Dto(t.TransitLocationId),
            Branch(branches, t.SourceBranchId), Branch(branches, t.DestinationBranchId),
            new StockTransferActorDto(t.CreatedByUserId, UserName(t.CreatedByUserId), t.CreatedAt),
            Actor(t.SubmittedByUserId, t.SubmittedAt), Actor(t.ApprovedByUserId, t.ApprovedAt),
            Actor(t.RejectedByUserId, t.RejectedAt), t.RejectionReason,
            Actor(t.DispatchedByUserId, t.DispatchedAt), Actor(t.CompletedByUserId, t.CompletedAt),
            Actor(t.CancelledByUserId, t.CancelledAt), t.CancellationReason,
            lines, receipts);
    }

    /// <summary>Per-request id → code/name/type of the company's stock locations (a document the caller can read always names its own
    /// endpoints, so this is not narrowed by permission; see <see cref="StockLocationLabels"/>).</summary>
    private sealed class LocationLookup
    {
        private readonly Dictionary<Guid, StockTransferLocationDto> _byId;
        private LocationLookup(Dictionary<Guid, StockTransferLocationDto> byId) => _byId = byId;

        public static async Task<LocationLookup> LoadAsync(SelloraDbContext db, CancellationToken ct)
        {
            var companyId = db.CompanyContext.RequireCompanyId();
            var rows = await db.StockLocations.AsNoTracking()
                .Where(l => l.CompanyId == companyId)
                .Select(l => new { l.Id, l.Code, l.Name, l.LocationType })
                .ToListAsync(ct);
            return new LocationLookup(rows.ToDictionary(l => l.Id, l => new StockTransferLocationDto(l.Id, l.Code, l.Name, l.LocationType.ToString())));
        }

        public StockTransferLocationDto Dto(Guid id) => _byId.TryGetValue(id, out var d) ? d : new StockTransferLocationDto(id, null, null, null);
    }
}
