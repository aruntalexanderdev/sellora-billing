using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Branches;

/// <summary>Phase 11a D14 - branch / warehouse / vehicle status changes require a reason (field <c>reason</c>), stored in the audit event.</summary>
public class StatusReasonTests
{
    private static async Task<BranchWorld> WorldAsync()
    {
        var w = await BranchWorld.CreateAsync();
        await using var db = w.Db();
        await BranchBuilder.EnsureLocationsAsync(db);
        return w;
    }

    private static BranchService Branches(SelloraDbContext db) => new(db, new StockLocationUsageService(db));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetStatusAsync_MissingOrBlankReason_RejectsOnReasonForAllThree(string? reason)
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var usage = new FakeStockLocationUsage();
        var wh = await new WarehouseService(db, usage).CreateAsync(new("BW1", "Back", w.Blr), w.UserId);
        var v = await new VehicleService(db, usage).CreateAsync(new("V1", "Van", VehicleType.Van, w.Blr), w.UserId);

        var b = await Assert.ThrowsAsync<DomainValidationException>(() => Branches(db).SetStatusAsync(w.Blr, false, reason, w.UserId));
        var x = await Assert.ThrowsAsync<DomainValidationException>(() => new WarehouseService(db, usage).SetStatusAsync(wh.Id, false, reason, w.UserId));
        var y = await Assert.ThrowsAsync<DomainValidationException>(() => new VehicleService(db, usage).SetStatusAsync(v.Id, false, reason, w.UserId));

        Assert.Equal("reason", b.Field);
        Assert.Equal("reason", x.Field);
        Assert.Equal("reason", y.Field);
        Assert.Empty(await db.AuditEvents.Where(a => a.Action.EndsWith(".status_changed")).ToListAsync());
    }

    [Fact]
    public async Task SetStatusAsync_ReasonLongerThan500_RejectsOnReason()
    {
        var w = await WorldAsync();
        await using var db = w.Db();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Branches(db).SetStatusAsync(w.Blr, false, new string('x', 501), w.UserId));

        Assert.Equal("reason", ex.Field);
    }

    [Fact]
    public async Task SetStatusAsync_WithReason_StoresTrimmedReasonAndBeforeAfterInTheAuditEvent()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var usage = new FakeStockLocationUsage();
        var wh = await new WarehouseService(db, usage).CreateAsync(new("BW1", "Back", w.Blr), w.UserId);
        var v = await new VehicleService(db, usage).CreateAsync(new("V1", "Van", VehicleType.Van, w.Blr), w.UserId);

        await new WarehouseService(db, usage).SetStatusAsync(wh.Id, false, "  closed for audit ", w.UserId);
        await new VehicleService(db, usage).SetStatusAsync(v.Id, false, "sold", w.UserId);
        await Branches(db).SetStatusAsync(w.Blr, false, "branch shut", w.UserId);

        var events = await db.AuditEvents.ToListAsync();
        var warehouse = events.Single(e => e.Action == AuditActions.WarehouseStatusChanged);
        Assert.Equal("closed for audit", warehouse.Reason);
        Assert.Equal(wh.Id, warehouse.TargetId);
        Assert.Equal(w.UserId, warehouse.ActorUserId);
        Assert.Contains("true", warehouse.Before!);
        Assert.Contains("false", warehouse.After!);
        Assert.Equal("sold", events.Single(e => e.Action == AuditActions.VehicleStatusChanged).Reason);
        Assert.Equal("branch shut", events.Single(e => e.Action == AuditActions.BranchStatusChanged).Reason);
    }

    [Fact]
    public async Task SetStatusAsync_InUseRefusal_StillRejectsOnIsActive_AndWritesNoAudit()
    {
        var w = await WorldAsync();
        await using var db = w.Db();
        var usage = new FakeStockLocationUsage();
        await new WarehouseService(db, usage).CreateAsync(new("BW1", "Back", w.Blr), w.UserId);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Branches(db).SetStatusAsync(w.Blr, false, "why", w.UserId));

        Assert.Equal("IsActive", ex.Field);
        Assert.Empty(await db.AuditEvents.Where(a => a.Action == AuditActions.BranchStatusChanged).ToListAsync());
    }
}
