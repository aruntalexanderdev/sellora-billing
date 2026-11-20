using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Geography;

/// <summary>CountryService / StateService: D8 guard, both-actor audit, IsSystem identity lock, BR2-BR7, no-op status, platform CompanyId NULL.</summary>
public class GeographyServiceTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid CompanyId = Guid.NewGuid();
    private static readonly ReferenceDataActor Operator = ReferenceDataActor.Platform(UserId, "trace-p");
    private static readonly ReferenceDataActor Tenant = ReferenceDataActor.ForCompany(UserId, CompanyId, "trace-c");

    private static IOptions<PlatformOptions> Flag(bool editable) => Options.Create(new PlatformOptions { ReferenceDataEditableByCompanies = editable });

    private static async Task<(SelloraDbContext Db, Country India, Country Uae)> NewAsync()
    {
        var db = TestDb.NewInMemory();
        var india = new Country { Id = GeoSeedData.CountryId("IN"), Code = "IN", Iso3Code = "IND", Name = "India", IsActive = true, IsSystem = true };
        var uae = new Country { Id = GeoSeedData.CountryId("AE"), Code = "AE", Iso3Code = "ARE", Name = "United Arab Emirates", IsActive = true, IsSystem = true };
        db.Countries.AddRange(india, uae);
        await db.SaveChangesAsync();
        return (db, india, uae);
    }

    private static async Task<DomainValidationException> Invalid(Func<Task> act) => await Assert.ThrowsAsync<DomainValidationException>(act);

    [Fact]
    public async Task CompanyWrite_WhileThePlatformManagesData_IsRefused_AndNothingIsWritten()
    {
        var (db, india, _) = await NewAsync();
        var countries = new CountryService(db, Flag(false));
        var states = new StateService(db, Flag(false));

        await Assert.ThrowsAsync<ReferenceDataPlatformManagedException>(() => countries.CreateAsync(new("ZZ", null, "Zedland"), Tenant));
        await Assert.ThrowsAsync<ReferenceDataPlatformManagedException>(() => countries.SetActiveAsync(india.Id, false, Tenant));
        await Assert.ThrowsAsync<ReferenceDataPlatformManagedException>(() => states.CreateAsync(new(india.Id, "KL", "Kerala", StateKind.State, "32"), Tenant));

        Assert.Equal(2, await db.Countries.CountAsync());
        Assert.Empty(await db.PlatformAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task CompanyWrite_WhenFlagIsTrue_Succeeds_AndAuditsRealUserAndCompany()
    {
        var (db, india, _) = await NewAsync();

        var state = await new StateService(db, Flag(true)).CreateAsync(new(india.Id, "kl", " Kerala ", StateKind.State, "32"), Tenant);

        Assert.Equal("KL", state.Code);
        Assert.Equal("Kerala", state.Name);
        var audit = Assert.Single(await db.PlatformAuditEvents.ToListAsync());
        Assert.Equal(PlatformAuditActions.StateCreated, audit.Action);
        Assert.Equal(UserId, audit.ActorUserId);
        Assert.Equal(CompanyId, audit.CompanyId);
        Assert.Equal(state.Id, audit.TargetId);
    }

    [Fact]
    public async Task PlatformWrite_IsAuditedWithActorAndNullCompany_EvenWhenGuardWouldRefuseCompanies()
    {
        var (db, _, _) = await NewAsync();

        var country = await new CountryService(db, Flag(false)).CreateAsync(new("qa2".Substring(0, 2), "qat", "Qatar2"), Operator);

        Assert.Equal("QA", country.Code);
        Assert.Equal("QAT", country.Iso3Code);
        var audit = Assert.Single(await db.PlatformAuditEvents.ToListAsync());
        Assert.Equal(PlatformAuditActions.CountryCreated, audit.Action);
        Assert.Equal(UserId, audit.ActorUserId);
        Assert.Null(audit.CompanyId);
    }

    [Fact]
    public async Task Write_WithoutRealIdentity_Throws_AndWritesNothing()
    {
        var (db, _, _) = await NewAsync();
        var service = new CountryService(db, Flag(true));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(new("ZZ", null, "Zedland"), ReferenceDataActor.Company));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(new("ZZ", null, "Zedland"), new ReferenceDataActor(false, UserId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(new("ZZ", null, "Zedland"), new ReferenceDataActor(true)));

        Assert.Equal(2, await db.Countries.CountAsync());
        Assert.Empty(await db.PlatformAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task CountryCreate_DuplicateCodeNameOrIso3_Is400OnThatField()
    {
        var (db, _, _) = await NewAsync();
        var service = new CountryService(db);

        Assert.Equal("code", (await Invalid(() => service.CreateAsync(new("in", null, "Other"), Operator))).Field);
        Assert.Equal("name", (await Invalid(() => service.CreateAsync(new("ZZ", null, "INDIA"), Operator))).Field);
        Assert.Equal("iso3Code", (await Invalid(() => service.CreateAsync(new("ZZ", "ind", "Zedland"), Operator))).Field);
        Assert.Equal("code", (await Invalid(() => service.CreateAsync(new("Z1", null, "Zedland"), Operator))).Field);
        Assert.Equal("iso3Code", (await Invalid(() => service.CreateAsync(new("ZZ", "ZZ", "Zedland"), Operator))).Field);
    }

    [Fact]
    public async Task CountryUpdate_SystemRow_CodeAndIso3Locked_NameEditableAndAudited_UnchangedValuesAccepted()
    {
        var (db, india, _) = await NewAsync();
        var service = new CountryService(db);

        Assert.Equal("code", (await Invalid(() => service.UpdateAsync(india.Id, new("XX", "IND", "India"), Operator))).Field);
        Assert.Equal("iso3Code", (await Invalid(() => service.UpdateAsync(india.Id, new("IN", "XXX", "India"), Operator))).Field);
        Assert.Equal("iso3Code", (await Invalid(() => service.UpdateAsync(india.Id, new("IN", null, "India"), Operator))).Field);

        var updated = await service.UpdateAsync(india.Id, new("in", "ind", "Republic of India"), Operator);

        Assert.Equal("Republic of India", updated!.Name);
        var audit = Assert.Single(await db.PlatformAuditEvents.ToListAsync());
        Assert.Equal(PlatformAuditActions.CountryUpdated, audit.Action);
        Assert.Contains("Republic of India", audit.After);
        Assert.Contains("\"name\":\"India\"", audit.Before);
        Assert.Null(await service.UpdateAsync(Guid.NewGuid(), new("ZZ", null, "Zedland"), Operator));
    }

    [Fact]
    public async Task CountryStatus_NoOpWritesNoAudit_AndDeactivationDoesNotCascade()
    {
        var (db, india, _) = await NewAsync();
        var state = await new StateService(db).CreateAsync(new(india.Id, "KL", "Kerala", StateKind.State, "32"), Operator);
        var service = new CountryService(db);
        var baseline = await db.PlatformAuditEvents.CountAsync(); // the audit log is append-only: compare counts

        Assert.Equal(GeoStatusOutcome.Changed, await service.SetActiveAsync(india.Id, true, Operator));
        Assert.Equal(baseline, await db.PlatformAuditEvents.CountAsync());

        await service.SetActiveAsync(india.Id, false, Operator);

        Assert.Equal(baseline + 1, await db.PlatformAuditEvents.CountAsync());
        Assert.Equal(PlatformAuditActions.CountryDeactivated, (await db.PlatformAuditEvents.OrderBy(e => e.OccurredAt).LastAsync()).Action);
        Assert.True((await db.States.SingleAsync(s => s.Id == state.Id)).IsActive);
        Assert.Equal(GeoStatusOutcome.NotFound, await service.SetActiveAsync(Guid.NewGuid(), true, Operator));
    }

    [Fact]
    public async Task StateCreate_DuplicateNameOrCodeWithinCountry_Is400_ButSameNameInAnotherCountryIsFine()
    {
        var (db, india, uae) = await NewAsync();
        var service = new StateService(db);
        await service.CreateAsync(new(india.Id, "KL", "Kerala", StateKind.State, null), Operator);

        Assert.Equal("name", (await Invalid(() => service.CreateAsync(new(india.Id, "K2", "kerala", StateKind.State, null), Operator))).Field);
        Assert.Equal("code", (await Invalid(() => service.CreateAsync(new(india.Id, "kl", "Other", StateKind.State, null), Operator))).Field);
        Assert.NotNull(await service.CreateAsync(new(uae.Id, "KL", "Kerala", StateKind.Emirate, null), Operator));
    }

    [Fact]
    public async Task StateCreate_GstCode_IndiaOnly_TwoDigits_Unique()
    {
        var (db, india, uae) = await NewAsync();
        var service = new StateService(db);
        await service.CreateAsync(new(india.Id, "KL", "Kerala", StateKind.State, "32"), Operator);

        Assert.Equal("gstStateCode", (await Invalid(() => service.CreateAsync(new(uae.Id, "DU", "Dubai", StateKind.Emirate, "99"), Operator))).Field);
        Assert.Equal("gstStateCode", (await Invalid(() => service.CreateAsync(new(india.Id, "TN", "Tamil Nadu", StateKind.State, "3"), Operator))).Field);
        Assert.Equal("gstStateCode", (await Invalid(() => service.CreateAsync(new(india.Id, "TN", "Tamil Nadu", StateKind.State, "32"), Operator))).Field);
    }

    [Fact]
    public async Task StateCreate_UnknownCountry_Is400CountryId_AndInactiveCountryRejectsActiveState()
    {
        var (db, india, _) = await NewAsync();
        var service = new StateService(db);

        Assert.Equal("countryId", (await Invalid(() => service.CreateAsync(new(Guid.NewGuid(), "KL", "Kerala", StateKind.State, null), Operator))).Field);

        await new CountryService(db).SetActiveAsync(india.Id, false, Operator);
        Assert.Equal("isActive", (await Invalid(() => service.CreateAsync(new(india.Id, "KL", "Kerala", StateKind.State, null), Operator))).Field);
        Assert.False((await service.CreateAsync(new(india.Id, "KL", "Kerala", StateKind.State, null, IsActive: false), Operator)).IsActive);
    }

    [Fact]
    public async Task StateStatus_ActivatingUnderInactiveCountry_Is400IsActive_NoOpWritesNoAudit()
    {
        var (db, india, _) = await NewAsync();
        var service = new StateService(db);
        var state = await service.CreateAsync(new(india.Id, "KL", "Kerala", StateKind.State, null), Operator);
        await service.SetActiveAsync(state.Id, false, Operator);
        await new CountryService(db).SetActiveAsync(india.Id, false, Operator);
        var events = await db.PlatformAuditEvents.CountAsync();

        Assert.Equal("isActive", (await Invalid(() => service.SetActiveAsync(state.Id, true, Operator))).Field);
        Assert.Equal(GeoStatusOutcome.Changed, await service.SetActiveAsync(state.Id, false, Operator));

        Assert.Equal(events, await db.PlatformAuditEvents.CountAsync());
    }

    [Fact]
    public async Task StateUpdate_SystemRow_CodeAndGstLocked_NameKindEditable_CountryNeverChanges()
    {
        var (db, india, _) = await NewAsync();
        var system = new State { Id = GeoSeedData.StateId("IN", "KL"), CountryId = india.Id, Code = "KL", Name = "Kerala", Kind = StateKind.State, GstStateCode = "32", IsSystem = true, IsActive = true };
        db.States.Add(system);
        await db.SaveChangesAsync();
        var service = new StateService(db);

        Assert.Equal("code", (await Invalid(() => service.UpdateAsync(system.Id, new("XX", "Kerala", StateKind.State, "32"), Operator))).Field);
        Assert.Equal("gstStateCode", (await Invalid(() => service.UpdateAsync(system.Id, new("KL", "Kerala", StateKind.State, "33"), Operator))).Field);
        Assert.Equal("gstStateCode", (await Invalid(() => service.UpdateAsync(system.Id, new("KL", "Kerala", StateKind.State, null), Operator))).Field);

        var updated = await service.UpdateAsync(system.Id, new("kl", "Keralam", StateKind.Region, "32"), Operator);

        Assert.Equal("Keralam", updated!.Name);
        Assert.Equal(StateKind.Region, updated.Kind);
        Assert.Equal(india.Id, updated.CountryId);
        Assert.Equal(PlatformAuditActions.StateUpdated, Assert.Single(await db.PlatformAuditEvents.ToListAsync()).Action);
    }

    [Fact]
    public async Task StateUpdate_NonSystemRow_CanChangeGstCode_SubjectToUniqueness()
    {
        var (db, india, _) = await NewAsync();
        var service = new StateService(db);
        var a = await service.CreateAsync(new(india.Id, "KL", "Kerala", StateKind.State, "32"), Operator);
        var b = await service.CreateAsync(new(india.Id, "TN", "Tamil Nadu", StateKind.State, null), Operator);

        Assert.Equal("gstStateCode", (await Invalid(() => service.UpdateAsync(b.Id, new("TN", "Tamil Nadu", StateKind.State, "32"), Operator))).Field);
        Assert.Equal("33", (await service.UpdateAsync(b.Id, new("TN", "Tamil Nadu", StateKind.State, "33"), Operator))!.GstStateCode);
        Assert.NotNull(await service.UpdateAsync(a.Id, new("KL", "Kerala", StateKind.State, "32"), Operator));
    }
}
