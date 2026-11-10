using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Platform;

/// <summary>
/// Phase 7 D11/Q3 at the service layer: while Platform:ReferenceDataEditableByCompanies is false, company callers are refused (ReferenceDataPlatformManagedException,
/// 403 reference_data_platform_managed) by UnitService and CurrencyService and platform callers are allowed; every platform write is audited with before/after in the same save.
/// </summary>
public class ReferenceDataGuardTests
{
    private static readonly UpsertUnitInput Dozen = new("Dozen", "dz", MeasurementCategory.Count, 0, null);
    private static readonly UpsertCurrencyInput Eur = new("eur", "Euro", "E", 2);
    private static readonly ReferenceDataActor Operator = ReferenceDataActor.Platform(Guid.NewGuid(), "trace-1");

    private static SelloraDbContext NewDb() => TestDb.NewInMemory();

    private static Microsoft.Extensions.Options.IOptions<PlatformOptions> Flag(bool editable) =>
        Microsoft.Extensions.Options.Options.Create(new PlatformOptions { ReferenceDataEditableByCompanies = editable });

    [Fact]
    public async Task UnitWrites_ByACompany_WhileThePlatformManagesThem_AreRefused()
    {
        var db = NewDb();
        var service = new UnitService(db, Flag(false));
        var existing = await new UnitService(db).CreateUnitAsync(Dozen);

        await Assert.ThrowsAsync<ReferenceDataPlatformManagedException>(() => service.CreateUnitAsync(new("Gross", "gr", MeasurementCategory.Count, 0, null)));
        await Assert.ThrowsAsync<ReferenceDataPlatformManagedException>(() => service.UpdateUnitAsync(existing.Id, Dozen));
        await Assert.ThrowsAsync<ReferenceDataPlatformManagedException>(() => service.SetActiveAsync(existing.Id, false, default, ReferenceDataActor.Company));
        Assert.Equal(1, await db.Units.CountAsync());
        Assert.Empty(await db.PlatformAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task UnitWrites_ByACompany_WhenTheFlagIsTrue_AreAllowedAndNotAudited()
    {
        var db = NewDb();

        var unit = await new UnitService(db, Flag(true)).CreateUnitAsync(Dozen);

        Assert.Equal("Dozen", unit.Name);
        Assert.Empty(await db.PlatformAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task UnitWrites_ByThePlatform_AreAllowedAndAudited()
    {
        var db = NewDb();
        var service = new UnitService(db, Flag(false));

        var unit = await service.CreateUnitAsync(Dozen, default, Operator);
        await service.UpdateUnitAsync(unit.Id, Dozen with { Name = "Dozens" }, default, Operator);
        await service.SetActiveAsync(unit.Id, false, default, Operator);

        var events = await db.PlatformAuditEvents.OrderBy(e => e.OccurredAt).ToListAsync();
        Assert.Equal(
            new[] { PlatformAuditActions.UnitCreated, PlatformAuditActions.UnitUpdated, PlatformAuditActions.UnitStatusChanged }.Order(),
            events.Select(e => e.Action).Order());
        Assert.All(events, e => Assert.Equal((PlatformAuditTargets.Unit, unit.Id, Operator.UserId, "trace-1"), (e.TargetType, e.TargetId, e.ActorUserId, e.CorrelationId)));
        var updated = events.Single(e => e.Action == PlatformAuditActions.UnitUpdated);
        Assert.Contains("\"name\":\"Dozen\"", updated.Before);
        Assert.Contains("\"name\":\"Dozens\"", updated.After);
    }

    [Fact]
    public async Task CurrencyWrites_ByACompany_WhileThePlatformManagesThem_AreRefused()
    {
        var db = NewDb();
        var created = await new CurrencyService(db).CreateAsync(Eur, ReferenceDataActor.Company);
        var service = new CurrencyService(db, Flag(false));

        await Assert.ThrowsAsync<ReferenceDataPlatformManagedException>(() => service.CreateAsync(new("usd", "Dollar", "$", 2), ReferenceDataActor.Company));
        await Assert.ThrowsAsync<ReferenceDataPlatformManagedException>(() => service.UpdateAsync(created.Id, Eur, ReferenceDataActor.Company));
        await Assert.ThrowsAsync<ReferenceDataPlatformManagedException>(() => service.SetActiveAsync(created.Id, false, ReferenceDataActor.Company));
        Assert.Equal(1, await db.Currencies.CountAsync());
    }

    [Fact]
    public async Task CurrencyWrites_ByThePlatform_AreAllowedAndAuditedWithBeforeAndAfter()
    {
        var db = NewDb();
        var service = new CurrencyService(db, Flag(false));

        var currency = await service.CreateAsync(Eur, Operator);
        await service.UpdateAsync(currency.Id, Eur with { Name = "Euro (EU)" }, Operator);
        var outcome = await service.SetActiveAsync(currency.Id, false, Operator);

        Assert.Equal(CurrencyStatusOutcome.Changed, outcome);
        var events = await db.PlatformAuditEvents.ToListAsync();
        Assert.Equal(
            new[] { PlatformAuditActions.CurrencyCreated, PlatformAuditActions.CurrencyUpdated, PlatformAuditActions.CurrencyStatusChanged }.Order(),
            events.Select(e => e.Action).Order());
        Assert.All(events, e => Assert.Equal((PlatformAuditTargets.Currency, currency.Id), (e.TargetType, e.TargetId)));
        var updated = events.Single(e => e.Action == PlatformAuditActions.CurrencyUpdated);
        Assert.Contains("\"name\":\"Euro\"", updated.Before);
        Assert.Contains("\"name\":\"Euro (EU)\"", updated.After);
    }

    [Fact]
    public async Task CurrencyCreate_DuplicateCode_IsAFieldErrorOnCode_AndWritesNoAudit()
    {
        var db = NewDb();
        var service = new CurrencyService(db, Flag(false));
        await service.CreateAsync(Eur, Operator);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(Eur, Operator));

        Assert.Equal("Code", ex.Field);
        Assert.Single(await db.PlatformAuditEvents.ToListAsync());
    }
}
