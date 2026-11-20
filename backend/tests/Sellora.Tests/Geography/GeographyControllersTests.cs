using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Geography;

/// <summary>
/// Country/State endpoints at controller level (country-state-masters section 11): company list/lookup/get/write with the D8 guard and canEdit signal,
/// 400 field mapping, 404s, audit actors for company and platform callers, and the platform-console controller (guard never applies, CompanyId NULL).
/// Policy attributes are asserted in EndpointAuthorizationTests.
/// </summary>
public class GeographyControllersTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    /// <summary>Each controller property returns a NEW instance (as one per HTTP request): ModelState must not leak between calls.</summary>
    private sealed class Fixture(SelloraDbContext db, IOptions<PlatformOptions> options, Country india, Country uae, State kerala)
    {
        public SelloraDbContext Db => db;
        public Country India => india;
        public Country Uae => uae;
        public State Kerala => kerala;
        public CountriesController Countries => new(db, new CountryService(db, options), options) { ControllerContext = Context() };
        public StatesController States => new(db, new StateService(db, options), options) { ControllerContext = Context() };
        public PlatformGeographyController Platform => new(db, new CountryService(db, options), new StateService(db, options)) { ControllerContext = Context() };
    }

    private static ControllerContext Context()
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var services = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider();
        return new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), RequestServices = services } };
    }

    private static async Task<Fixture> NewAsync(bool companiesMayEdit = false)
    {
        var db = TestDb.NewInMemory();
        var india = new Country { Id = GeoSeedData.CountryId("IN"), Code = "IN", Iso3Code = "IND", Name = "India", IsSystem = true };
        var uae = new Country { Id = GeoSeedData.CountryId("AE"), Code = "AE", Iso3Code = "ARE", Name = "United Arab Emirates", IsSystem = true };
        var kerala = new State { Id = Guid.NewGuid(), CountryId = india.Id, Code = "KL", Name = "Kerala", Kind = StateKind.State, GstStateCode = "32", IsSystem = true };
        var dubai = new State { Id = Guid.NewGuid(), CountryId = uae.Id, Code = "DU", Name = "Dubai", Kind = StateKind.Emirate, IsSystem = true };
        db.Countries.AddRange(india, uae);
        db.States.AddRange(kerala, dubai);
        await db.SaveChangesAsync();

        return new Fixture(db, Options.Create(new PlatformOptions { ReferenceDataEditableByCompanies = companiesMayEdit }), india, uae, kerala);
    }

    private static T Ok<T>(ActionResult<T> result) => Assert.IsAssignableFrom<T>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static string[] Fields(IActionResult? result)
    {
        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
        Assert.Equal(400, problem.Status);
        return problem.Errors.Keys.ToArray();
    }

    // ---- reads ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountryAndStateList_CanEditEqualsTheFlag_AndInactiveRowsAreHiddenUnlessAsked(bool flag)
    {
        var f = await NewAsync(flag);
        f.Uae.IsActive = false;
        await f.Db.SaveChangesAsync();

        var countries = Ok(await f.Countries.List());
        var withInactive = Ok(await f.Countries.List(includeInactive: true));
        var states = Ok(await f.States.List(null));

        Assert.Equal(flag, countries.CanEdit);
        Assert.Equal(flag, states.CanEdit);
        Assert.Equal(["IN"], countries.Items.Select(c => c.Code));
        Assert.Equal(["AE", "IN"], withInactive.Items.Select(c => c.Code).Order());
        Assert.Equal(2, Ok(await f.States.List(null, includeInactive: true)).Items.Count);
        Assert.Equal(["Kerala", "Dubai"], states.Items.Select(s => s.Name)); // list does not hide states of an inactive country (BR6/D19: no cascade)... ordered by country name
    }

    [Fact]
    public async Task StateList_FilteredByCountry_CarriesCountryNamesAndKindAndGst()
    {
        var f = await NewAsync();

        var items = Ok(await f.States.List(f.India.Id)).Items;

        var kerala = Assert.Single(items);
        Assert.Equal(("IN", "India", "KL", "Kerala", StateKind.State, "32", true), (kerala.CountryCode, kerala.CountryName, kerala.Code, kerala.Name, kerala.Kind, kerala.GstStateCode, kerala.IsSystem));
    }

    [Fact]
    public async Task Lookups_HideInactiveCountriesAndStatesOfInactiveCountries_AndRequireCountryId()
    {
        var f = await NewAsync();
        var extra = new State { Id = Guid.NewGuid(), CountryId = f.India.Id, Code = "XX", Name = "Dormant", IsActive = false };
        f.Db.States.Add(extra);
        await f.Db.SaveChangesAsync();

        Assert.Equal(["IN", "AE"], Ok(await f.Countries.Lookup(default)).Select(c => c.Code).OrderByDescending(c => c)); // active only
        Assert.Equal(["Kerala"], Ok(await f.States.Lookup(f.India.Id, default)).Select(s => s.Name));
        Assert.Equal(["32"], Ok(await f.States.Lookup(f.India.Id, default)).Select(s => s.GstStateCode!));

        f.Uae.IsActive = false;
        await f.Db.SaveChangesAsync();
        Assert.Equal(["IN"], Ok(await f.Countries.Lookup(default)).Select(c => c.Code));
        Assert.Empty(Ok(await f.States.Lookup(f.Uae.Id, default)));
        Assert.Empty(Ok(await f.States.Lookup(Guid.NewGuid(), default)));
        Assert.Equal(["countryId"], Fields((await f.States.Lookup(null, default)).Result));
    }

    [Fact]
    public async Task Get_UnknownIds_Return404_KnownReturnDto()
    {
        var f = await NewAsync();

        Assert.IsType<NotFoundResult>((await f.Countries.Get(Guid.NewGuid(), default)).Result);
        Assert.IsType<NotFoundResult>((await f.States.Get(Guid.NewGuid(), default)).Result);
        Assert.Equal("India", Ok(await f.Countries.Get(f.India.Id, default)).Name);
        Assert.Equal("India", Ok(await f.States.Get(f.Kerala.Id, default)).CountryName);
    }

    // ---- company writes: D8 guard ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CompanyWrites_WhileThePlatformManagesData_Throw403Exception_AndWriteNothing()
    {
        var f = await NewAsync(companiesMayEdit: false);

        await Assert.ThrowsAsync<ReferenceDataPlatformManagedException>(() => f.Countries.Create(new("ZZ", null, "Zedland"), default));
        await Assert.ThrowsAsync<ReferenceDataPlatformManagedException>(() => f.Countries.Update(f.India.Id, new("IN", "IND", "Bharat"), default));
        await Assert.ThrowsAsync<ReferenceDataPlatformManagedException>(() => f.Countries.SetActive(f.India.Id, new(false), default));
        await Assert.ThrowsAsync<ReferenceDataPlatformManagedException>(() => f.States.Create(new(f.India.Id, "TN", "Tamil Nadu", StateKind.State, "33"), default));
        await Assert.ThrowsAsync<ReferenceDataPlatformManagedException>(() => f.States.Update(f.Kerala.Id, new("KL", "Keralam", StateKind.State, "32"), default));
        await Assert.ThrowsAsync<ReferenceDataPlatformManagedException>(() => f.States.SetActive(f.Kerala.Id, new(false), default));

        Assert.Equal(2, await f.Db.Countries.CountAsync());
        Assert.Equal("India", (await f.Db.Countries.SingleAsync(c => c.Id == f.India.Id)).Name);
        Assert.True((await f.Db.States.SingleAsync(s => s.Id == f.Kerala.Id)).IsActive);
        Assert.Empty(await f.Db.PlatformAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task CompanyWrites_WhenTheFlagIsTrue_Succeed_AndAuditRealUserAndCompany()
    {
        var f = await NewAsync(companiesMayEdit: true);

        var created = Ok(await f.Countries.Create(new("zz", "zzz", " Zedland "), default));
        var state = Ok(await f.States.Create(new(created.Id, "z1", "Zed North", StateKind.Region, null), default));
        Assert.Equal(("ZZ", "ZZZ", "Zedland", false), (created.Code, created.Iso3Code, created.Name, created.IsSystem));
        Assert.Equal(("Z1", "Zed North", created.Name, StateKind.Region), (state.Code, state.Name, state.CountryName, state.Kind));
        Assert.IsType<NoContentResult>(await f.Countries.SetActive(created.Id, new(false), default));
        Assert.IsType<NoContentResult>(await f.States.SetActive(state.Id, new(false), default));

        var events = await f.Db.PlatformAuditEvents.OrderBy(e => e.OccurredAt).ToListAsync();
        Assert.Equal(["country.create", "state.create", "country.deactivate", "state.deactivate"], events.Select(e => e.Action));
        Assert.All(events, e => Assert.Equal((UserId, TestCompany.DefaultId), (e.ActorUserId, e.CompanyId)));
    }

    [Fact]
    public async Task CompanyUpdate_SystemRows_Lock400OnIdentityFields_NameStaysEditable()
    {
        var f = await NewAsync(companiesMayEdit: true);

        Assert.Equal(["code"], Fields((await f.Countries.Update(f.India.Id, new("XX", "IND", "India"), default)).Result));
        Assert.Equal(["code"], Fields((await f.States.Update(f.Kerala.Id, new("ZZ", "Kerala", StateKind.State, "32"), default)).Result));
        Assert.Equal(["gstStateCode"], Fields((await f.States.Update(f.Kerala.Id, new("KL", "Kerala", StateKind.State, "99"), default)).Result));
        Assert.Equal("Keralam", Ok(await f.States.Update(f.Kerala.Id, new("KL", "Keralam", StateKind.State, "32"), default)).Name);
        Assert.Equal(f.India.Id, Ok(await f.States.Get(f.Kerala.Id, default)).CountryId); // CountryId never changes (not in UpdateStateRequest)
    }

    [Fact]
    public async Task CompanyWrites_BusinessRules_MapToRequestFieldNames()
    {
        var f = await NewAsync(companiesMayEdit: true);
        f.Uae.IsActive = false;
        await f.Db.SaveChangesAsync();

        Assert.Equal(["code"], Fields((await f.Countries.Create(new("in", null, "Other"), default)).Result));
        Assert.Equal(["name"], Fields((await f.Countries.Create(new("ZZ", null, "india"), default)).Result));
        Assert.Equal(["countryId"], Fields((await f.States.Create(new(Guid.NewGuid(), "TN", "Tamil Nadu", StateKind.State, null), default)).Result));
        Assert.Equal(["name"], Fields((await f.States.Create(new(f.India.Id, "TN", "kerala", StateKind.State, null), default)).Result));
        Assert.Equal(["gstStateCode"], Fields((await f.States.Create(new(f.Uae.Id, "AJ", "Ajman", StateKind.Emirate, "07", false), default)).Result)); // BR4: IN only
        Assert.Equal(["gstStateCode"], Fields((await f.States.Create(new(f.India.Id, "TN", "Tamil Nadu", StateKind.State, "32"), default)).Result)); // duplicate
        Assert.Equal(["isActive"], Fields((await f.States.Create(new(f.Uae.Id, "AJ", "Ajman", StateKind.Emirate, null), default)).Result)); // BR6
        Assert.Equal(["isActive"], Fields(await SetStateActiveAsync(f, f.Db.States.Single(s => s.Code == "DU").Id, true)));
    }

    private static async Task<IActionResult?> SetStateActiveAsync(Fixture f, Guid id, bool active)
    {
        var dubai = await f.Db.States.SingleAsync(s => s.Id == id);
        dubai.IsActive = false;
        await f.Db.SaveChangesAsync();
        return await f.States.SetActive(id, new(active), default);
    }

    [Fact]
    public async Task Writes_UnknownIds_Return404()
    {
        var f = await NewAsync(companiesMayEdit: true);

        Assert.IsType<NotFoundResult>((await f.Countries.Update(Guid.NewGuid(), new("ZZ", null, "Zed"), default)).Result);
        Assert.IsType<NotFoundResult>((await f.States.Update(Guid.NewGuid(), new("ZZ", "Zed", StateKind.State, null), default)).Result);
        Assert.IsType<NotFoundResult>(await f.Countries.SetActive(Guid.NewGuid(), new(false), default));
        Assert.IsType<NotFoundResult>(await f.States.SetActive(Guid.NewGuid(), new(false), default));
    }

    [Fact]
    public async Task StatusPost_AlreadyInTargetState_Is204AndWritesNoAudit()
    {
        var f = await NewAsync(companiesMayEdit: true);

        Assert.IsType<NoContentResult>(await f.Countries.SetActive(f.India.Id, new(true), default));
        Assert.IsType<NoContentResult>(await f.States.SetActive(f.Kerala.Id, new(true), default));

        Assert.Empty(await f.Db.PlatformAuditEvents.ToListAsync());
    }

    // ---- platform console --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Platform_Writes_AreAllowedUnderTheDefaultFlag_AuditedWithActorAndNullCompany()
    {
        var f = await NewAsync(companiesMayEdit: false);

        var country = Ok(await f.Platform.CreateCountry(new("ZZ", null, "Zedland"), default));
        var updated = Ok(await f.Platform.UpdateCountry(country.Id, new("ZZ", "ZZZ", "Zedland 2"), default));
        var state = Ok(await f.Platform.CreateState(new(country.Id, "Z1", "Zed North", StateKind.Region, null), default));
        Ok(await f.Platform.UpdateState(state.Id, new("Z1", "Zed North 2", StateKind.Region, null), default));
        Assert.IsType<NoContentResult>(await f.Platform.SetCountryActive(country.Id, new(false), default));
        Assert.IsType<NoContentResult>(await f.Platform.SetStateActive(state.Id, new(false), default));

        Assert.Equal("ZZZ", updated.Iso3Code);
        var events = await f.Db.PlatformAuditEvents.OrderBy(e => e.OccurredAt).ToListAsync();
        Assert.Equal(["country.create", "country.update", "state.create", "state.update", "country.deactivate", "state.deactivate"], events.Select(e => e.Action));
        Assert.All(events, e => Assert.Equal((UserId, (Guid?)null), (e.ActorUserId, e.CompanyId)));
    }

    [Fact]
    public async Task Platform_Reads_AllIncludesInactive_LookupsDoNot_StateLookupNeedsCountry()
    {
        var f = await NewAsync();
        f.Uae.IsActive = false;
        await f.Db.SaveChangesAsync();

        Assert.Equal(["AE", "IN"], Ok(await f.Platform.Countries(default)).Select(c => c.Code));
        Assert.Equal(["IN"], Ok(await f.Platform.CountryLookup(default)).Select(c => c.Code));
        Assert.Equal(2, Ok(await f.Platform.States(null, default)).Count);
        Assert.Equal(["Kerala"], Ok(await f.Platform.States(f.India.Id, default)).Select(s => s.Name));
        Assert.Equal(["Kerala"], Ok(await f.Platform.StateLookup(f.India.Id, default)).Select(s => s.Name));
        Assert.Empty(Ok(await f.Platform.StateLookup(f.Uae.Id, default)));
        Assert.Equal(["countryId"], Fields((await f.Platform.StateLookup(null, default)).Result));
    }

    [Fact]
    public async Task Platform_Writes_MapValidationAnd404()
    {
        var f = await NewAsync();

        Assert.Equal(["code"], Fields((await f.Platform.UpdateCountry(f.India.Id, new("XX", "IND", "India"), default)).Result));
        Assert.Equal(["code"], Fields((await f.Platform.CreateCountry(new("IN", null, "Other"), default)).Result));
        Assert.Equal(["gstStateCode"], Fields((await f.Platform.UpdateState(f.Kerala.Id, new("KL", "Kerala", StateKind.State, "01"), default)).Result));
        Assert.Equal(["isActive"], Fields(await f.Platform.SetStateActive(await InactiveUnderInactiveCountryAsync(f), new(true), default)));
        Assert.IsType<NotFoundResult>((await f.Platform.UpdateCountry(Guid.NewGuid(), new("ZZ", null, "Zed"), default)).Result);
        Assert.IsType<NotFoundResult>((await f.Platform.UpdateState(Guid.NewGuid(), new("ZZ", "Zed", StateKind.State, null), default)).Result);
        Assert.IsType<NotFoundResult>(await f.Platform.SetCountryActive(Guid.NewGuid(), new(false), default));
        Assert.IsType<NotFoundResult>(await f.Platform.SetStateActive(Guid.NewGuid(), new(false), default));
    }

    private static async Task<Guid> InactiveUnderInactiveCountryAsync(Fixture f)
    {
        f.Uae.IsActive = false;
        var dubai = await f.Db.States.SingleAsync(s => s.Code == "DU");
        dubai.IsActive = false;
        await f.Db.SaveChangesAsync();
        return dubai.Id;
    }

    [Fact]
    public async Task StateCountryIdIsImmutable_NoCountryIdInTheUpdateContract_AndABodyCountryIdChangesNothing()
    {
        var f = await NewAsync(companiesMayEdit: true);

        Assert.DoesNotContain(typeof(UpdateStateRequest).GetProperties(), p => p.Name == nameof(CreateStateRequest.CountryId));
        var body = System.Text.Json.JsonSerializer.Deserialize<UpdateStateRequest>(
            $$"""{"code":"KL","name":"Kerala","kind":"State","gstStateCode":"32","countryId":"{{f.Uae.Id}}"}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;

        var updated = Ok(await f.States.Update(f.Kerala.Id, body, default));

        Assert.Equal(f.India.Id, updated.CountryId);
        Assert.Equal(f.India.Id, (await f.Db.States.AsNoTracking().SingleAsync(s => s.Id == f.Kerala.Id)).CountryId);
    }
}
