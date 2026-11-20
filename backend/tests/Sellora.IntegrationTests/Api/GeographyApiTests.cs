using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sellora.Api.Contracts;
using Sellora.Domain.Entities;
using Sellora.IntegrationTests.Infrastructure;
using static Sellora.IntegrationTests.Api.Phase6Support;
using static Sellora.IntegrationTests.TestData.CompanyMigrations;

namespace Sellora.IntegrationTests.Api;

/// <summary>
/// country-state-masters over the real pipeline on PostgreSQL: seeded masters and lookups, the D8 guard in both modes with the canEdit signal, Masters.View/Edit
/// permissions, IsSystem identity lock, audit actors (company and platform), and the Country/State relink of Customer, Supplier, Branch, Business Settings and onboarding.
/// </summary>
[Collection(PostgresCollection.Name)]
public class GeographyApiTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string> CompaniesMayEdit = new() { ["Platform:ReferenceDataEditableByCompanies"] = "true" };
    private static readonly Dictionary<string, string> CreationOn = new() { ["Platform:CompanyCreationEnabled"] = "true" };

    private static JsonElement[] Items(JsonElement list) => list.GetProperty("items").EnumerateArray().ToArray();

    private static async Task<(ApiHost Host, HttpClient Admin, HttpClient Platform, Guid Ops)> StartAsync(PostgresFixture postgres, IReadOnlyDictionary<string, string>? settings = null)
    {
        var host = await ApiHost.StartAsync(postgres, settings);
        var ops = await AddUserAsync(host, "ops.geo", administrator: true, company: null);
        await GrantPlatformAdminAsync(host, ops);
        return (host, await host.LoginClientAsync(), await PlatformClientAsync(host, "ops.geo"), ops);
    }

    private static async Task<Guid> CountryIdAsync(HttpClient client, string code) =>
        (await client.GetFromJsonAsync<CountryLookupDto[]>("/api/v1/countries/lookup", ApiHost.Json))!.Single(c => c.Code == code).Id;

    private static async Task<StateLookupDto> StateAsync(HttpClient client, Guid countryId, string code) =>
        (await client.GetFromJsonAsync<StateLookupDto[]>($"/api/v1/states/lookup?countryId={countryId}", ApiHost.Json))!.Single(s => s.Code == code);

    private static async Task<string[]> ErrorFieldsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await JsonAsync(response)).GetProperty("errors");
        return errors.EnumerateObject().Select(p => p.Name).ToArray();
    }

    // ---- seeds and lookups ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SeededMasters_AreServedByTheLookups_ForAnySignedInUser()
    {
        var (host, admin, _, _) = await StartAsync(postgres);
        await using var disposeHost = host;
        using var limited = await host.LimitedClientAsync(admin, "Sales.View"); // no Masters.View: pickers must still work (D15)

        var countries = (await limited.GetFromJsonAsync<CountryLookupDto[]>("/api/v1/countries/lookup", ApiHost.Json))!;
        var india = countries.Single(c => c.Code == "IN");
        var states = (await limited.GetFromJsonAsync<StateLookupDto[]>($"/api/v1/states/lookup?countryId={india.Id}", ApiHost.Json))!;

        Assert.Superset(new HashSet<string> { "IN", "AE", "SA", "QA", "KW", "BH", "OM" }, countries.Select(c => c.Code).ToHashSet());
        Assert.Equal("32", states.Single(s => s.Code == "KL").GstStateCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await limited.GetAsync("/api/v1/states/lookup")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await limited.GetAsync("/api/v1/countries")).StatusCode); // admin list needs Masters.View
    }

    // ---- D8 guard, canEdit, permissions, audit ------------------------------------------------------------------------------------

    [Fact]
    public async Task DefaultMode_CompanyWritesAre403PlatformManaged_CanEditFalse_PlatformWritesSucceedAndAuditWithNullCompany()
    {
        var (host, admin, platform, ops) = await StartAsync(postgres);
        await using var disposeHost = host;
        var india = await CountryIdAsync(admin, "IN");

        var list = await JsonAsync(await admin.GetAsync("/api/v1/countries"));
        Assert.False(list.GetProperty("canEdit").GetBoolean());
        Assert.False((await JsonAsync(await admin.GetAsync("/api/v1/states"))).GetProperty("canEdit").GetBoolean());

        var refused = await admin.PostAsJsonAsync("/api/v1/countries", new CreateCountryRequest("ZZ", "ZZZ", "Zedland"), ApiHost.Json);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("reference_data_platform_managed", await refused.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync($"/api/v1/countries/{india}", new UpdateCountryRequest("IN", "IND", "Bharat"), ApiHost.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/v1/countries/{india}/status", new SetActiveRequest(false), ApiHost.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/v1/states", new CreateStateRequest(india, "ZZ", "Zed", StateKind.State, null), ApiHost.Json)).StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "Countries" WHERE "Code" = 'ZZ'"""));

        var created = await platform.PostAsJsonAsync("/api/v1/platform/countries", new CreateCountryRequest("ZZ", "ZZZ", "Zedland"), ApiHost.Json);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await platform.PostAsJsonAsync("/api/v1/platform/states", new CreateStateRequest(id, "Z1", "Zed North", StateKind.Region, null), ApiHost.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await platform.PostAsJsonAsync($"/api/v1/platform/countries/{id}/status", new SetActiveRequest(false), ApiHost.Json)).StatusCode);

        Assert.Equal(3L, await ScalarAsync<long>(host.Database,
            $"""SELECT COUNT(*) FROM "PlatformAuditEvents" WHERE "ActorUserId" = '{ops}' AND "CompanyId" IS NULL AND "Action" IN ('country.create', 'state.create', 'country.deactivate')"""));
        var platformList = await platform.GetFromJsonAsync<CountryDto[]>("/api/v1/platform/countries/all", ApiHost.Json);
        Assert.Contains(platformList!, c => c.Code == "ZZ" && !c.IsActive); // platform list includes inactive rows
        Assert.DoesNotContain(await platform.GetFromJsonAsync<CountryLookupDto[]>("/api/v1/platform/countries", ApiHost.Json) ?? [], c => c.Code == "ZZ");
    }

    [Fact]
    public async Task CompanyEditableMode_CompanyAdminWrites_AuditRealUserAndCompany_CanEditTrue()
    {
        var (host, admin, _, _) = await StartAsync(postgres, CompaniesMayEdit);
        await using var disposeHost = host;
        var adminId = await AdminUserIdAsync(host);

        Assert.True((await JsonAsync(await admin.GetAsync("/api/v1/countries"))).GetProperty("canEdit").GetBoolean());
        var created = await admin.PostAsJsonAsync("/api/v1/countries", new CreateCountryRequest("zz", "zzz", "Zedland"), ApiHost.Json);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/states", new CreateStateRequest(id, "z1", "Zed North", StateKind.Region, null), ApiHost.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/countries/{id}/status", new SetActiveRequest(false), ApiHost.Json)).StatusCode);

        Assert.Equal(3L, await ScalarAsync<long>(host.Database,
            $"""SELECT COUNT(*) FROM "PlatformAuditEvents" WHERE "ActorUserId" = '{adminId}' AND "CompanyId" = '{DefaultCompany}' AND "Action" IN ('country.create', 'state.create', 'country.deactivate')"""));
    }

    [Fact]
    public async Task MastersViewOnlyUser_ReadsButCannotWrite_EvenWhenCompaniesMayEdit()
    {
        var (host, admin, _, _) = await StartAsync(postgres, CompaniesMayEdit);
        await using var disposeHost = host;
        using var viewer = await host.LimitedClientAsync(admin, "Masters.View");

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/countries")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/states")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/countries", new CreateCountryRequest("ZZ", null, "Zedland"), ApiHost.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/v1/countries/{await CountryIdAsync(admin, "IN")}/status", new SetActiveRequest(false), ApiHost.Json)).StatusCode);
        // a company token never reaches the platform console: like every platform endpoint it is unauthenticated there (PlatformApiTests: 401)
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/v1/platform/countries/all")).StatusCode);
    }

    [Fact]
    public async Task SystemRows_KeepIdentity_DuplicatesAreFieldErrors_UnknownIdsAre404()
    {
        var (host, _, platform, _) = await StartAsync(postgres);
        await using var disposeHost = host;
        var countries = (await platform.GetFromJsonAsync<CountryDto[]>("/api/v1/platform/countries/all", ApiHost.Json))!;
        var india = countries.Single(c => c.Code == "IN");
        var kerala = (await platform.GetFromJsonAsync<StateDto[]>($"/api/v1/platform/states/all?countryId={india.Id}", ApiHost.Json))!.Single(s => s.Code == "KL");

        Assert.Equal(["code"], await ErrorFieldsAsync(await platform.PutAsJsonAsync($"/api/v1/platform/countries/{india.Id}", new UpdateCountryRequest("XX", "IND", "India"), ApiHost.Json)));
        Assert.Equal(["gstStateCode"], await ErrorFieldsAsync(await platform.PutAsJsonAsync($"/api/v1/platform/states/{kerala.Id}", new UpdateStateRequest("KL", "Kerala", StateKind.State, "33"), ApiHost.Json)));
        Assert.Equal(["code"], await ErrorFieldsAsync(await platform.PostAsJsonAsync("/api/v1/platform/countries", new CreateCountryRequest("in", null, "Other"), ApiHost.Json)));
        Assert.Equal(["name"], await ErrorFieldsAsync(await platform.PostAsJsonAsync("/api/v1/platform/states", new CreateStateRequest(india.Id, "ZZ", "kerala", StateKind.State, null), ApiHost.Json)));
        Assert.Equal(["gstStateCode"], await ErrorFieldsAsync(await platform.PostAsJsonAsync("/api/v1/platform/states", new CreateStateRequest(india.Id, "ZZ", "Zedstate", StateKind.State, "32"), ApiHost.Json)));
        var uae = countries.Single(c => c.Code == "AE");
        Assert.Equal(["gstStateCode"], await ErrorFieldsAsync(await platform.PostAsJsonAsync("/api/v1/platform/states", new CreateStateRequest(uae.Id, "ZZ", "Zedstate", StateKind.Emirate, "77"), ApiHost.Json)));

        var renamed = await platform.PutAsJsonAsync($"/api/v1/platform/states/{kerala.Id}", new UpdateStateRequest("KL", "Keralam", StateKind.State, "32"), ApiHost.Json);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(india.Id, (await JsonAsync(renamed)).GetProperty("countryId").GetGuid()); // CountryId immutable
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PutAsJsonAsync($"/api/v1/platform/countries/{Guid.NewGuid()}", new UpdateCountryRequest("ZZ", null, "Zed"), ApiHost.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PostAsJsonAsync($"/api/v1/platform/states/{Guid.NewGuid()}/status", new SetActiveRequest(false), ApiHost.Json)).StatusCode);
    }

    // ---- relink -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Customer_RelinkRules_StateOfAnotherCountry400_InactiveNew400_UnchangedInactiveSurvives_NullClears()
    {
        var (host, admin, platform, _) = await StartAsync(postgres);
        await using var disposeHost = host;
        var india = await CountryIdAsync(admin, "IN");
        var uae = await CountryIdAsync(admin, "AE");
        var kerala = await StateAsync(admin, india, "KL");
        var dubai = await StateAsync(admin, uae, "DU");

        Assert.Equal(["stateId"], await ErrorFieldsAsync(await admin.PostAsJsonAsync("/api/v1/customers", new UpsertCustomerRequest("Mismatch", null, null, india, dubai.Id), ApiHost.Json)));
        Assert.Equal(["countryId"], await ErrorFieldsAsync(await admin.PostAsJsonAsync("/api/v1/customers", new UpsertCustomerRequest("No country", null, null, null, kerala.Id), ApiHost.Json)));
        var created = await admin.PostAsJsonAsync("/api/v1/customers", new UpsertCustomerRequest("Linked", null, null, india, kerala.Id), ApiHost.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await JsonAsync(created);
        Assert.Equal(("India", "Kerala"), (body.GetProperty("countryName").GetString(), body.GetProperty("stateName").GetString()));
        var id = body.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await platform.PostAsJsonAsync($"/api/v1/platform/states/{kerala.Id}/status", new SetActiveRequest(false), ApiHost.Json)).StatusCode);
        var kept = await admin.PutAsJsonAsync($"/api/v1/customers/{id}", new UpsertCustomerRequest("Linked edited", null, null, india, kerala.Id), ApiHost.Json);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode); // unchanged inactive link survives (AC6)
        Assert.Equal(kerala.Id, (await JsonAsync(kept)).GetProperty("stateId").GetGuid());
        Assert.Equal(["stateId"], await ErrorFieldsAsync(await admin.PostAsJsonAsync("/api/v1/customers", new UpsertCustomerRequest("New inactive", null, null, india, kerala.Id), ApiHost.Json)));

        var cleared = await admin.PutAsJsonAsync($"/api/v1/customers/{id}", new UpsertCustomerRequest("Linked edited", null, null, null, null), ApiHost.Json);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(cleared)).GetProperty("stateId").ValueKind);
    }

    [Fact]
    public async Task Supplier_Branch_AndSettings_Relink_ValidatesAndStores_LegacyStateNeverWritten()
    {
        var (host, admin, _, _) = await StartAsync(postgres);
        await using var disposeHost = host;
        var india = await CountryIdAsync(admin, "IN");
        var uae = await CountryIdAsync(admin, "AE");
        var kerala = await StateAsync(admin, india, "KL");
        var dubai = await StateAsync(admin, uae, "DU");
        var inr = await InrIdAsync(host);

        // supplier
        Assert.Equal(["stateId"], await ErrorFieldsAsync(await admin.PostAsJsonAsync("/api/v1/suppliers", new UpsertSupplierRequest("Geo Supplier", null, null, null, inr, india, dubai.Id), ApiHost.Json)));
        var supplier = await admin.PostAsJsonAsync("/api/v1/suppliers", new UpsertSupplierRequest("Geo Supplier", null, null, null, inr, india, kerala.Id), ApiHost.Json);
        Assert.Equal(HttpStatusCode.Created, supplier.StatusCode);
        Assert.Equal("Kerala", (await JsonAsync(supplier)).GetProperty("stateName").GetString());

        // branch: legacy state text in the request is ignored
        Assert.Equal(["stateId"], await ErrorFieldsAsync(await admin.PostAsJsonAsync("/api/v1/branches",
            new CreateBranchRequest("GEO", "Geo branch", null, null, "Typed", null, null, null, null, india, dubai.Id), ApiHost.Json)));
        var branch = await admin.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest("GEO", "Geo branch", null, null, "Typed", null, null, null, null, india, kerala.Id), ApiHost.Json);
        Assert.Equal(HttpStatusCode.Created, branch.StatusCode);
        var branchBody = await JsonAsync(branch);
        Assert.Equal("Kerala", branchBody.GetProperty("stateName").GetString());
        Assert.Equal(JsonValueKind.Null, branchBody.GetProperty("state").ValueKind);
        Assert.Equal(0L, await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "Branches" WHERE "Code" = 'GEO' AND "State" IS NOT NULL"""));

        // settings: PUT carries the current values; a bad state is 400, a valid link is stored, the legacy text is untouched
        var settings = (await admin.GetFromJsonAsync<BusinessSettingsDto>("/api/v1/settings", ApiHost.Json))!;
        UpdateBusinessSettingsRequest Put(Guid? c, Guid? s) => new(settings.BusinessName, settings.AddressLine, settings.City, "Ignored text", settings.Gstin, settings.Phone,
            settings.Email, settings.InvoicePrefix, settings.BaseCurrencyId, null, null, c, s);
        Assert.Equal(["stateId"], await ErrorFieldsAsync(await admin.PutAsJsonAsync("/api/v1/settings", Put(india, dubai.Id), ApiHost.Json)));
        var ok = await admin.PutAsJsonAsync("/api/v1/settings", Put(india, kerala.Id), ApiHost.Json);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var updated = await JsonAsync(ok);
        Assert.Equal(("India", "Kerala"), (updated.GetProperty("countryName").GetString(), updated.GetProperty("stateName").GetString()));
        Assert.Equal(0L, await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "BusinessSettings" WHERE "State" = 'Ignored text'"""));
    }

    [Fact]
    public async Task Onboarding_StoresCountryAndStateOnTheNewCompanySettings_AndRejectsABadLinkBeforeAnyTenantRow()
    {
        var host = await ApiHost.StartAsync(postgres, CreationOn);
        await using var disposeHost = host;
        var ops = await AddUserAsync(host, "ops.geo2", administrator: true, company: null);
        await GrantPlatformAdminAsync(host, ops);
        using var platform = await PlatformClientAsync(host, "ops.geo2");
        var inr = await InrIdAsync(host);
        var india = (await platform.GetFromJsonAsync<CountryLookupDto[]>("/api/v1/platform/countries", ApiHost.Json))!.Single(c => c.Code == "IN");
        var states = (await platform.GetFromJsonAsync<StateLookupDto[]>($"/api/v1/platform/states?countryId={india.Id}", ApiHost.Json))!;
        var kerala = states.Single(s => s.Code == "KL");
        var uae = (await platform.GetFromJsonAsync<CountryLookupDto[]>("/api/v1/platform/countries", ApiHost.Json))!.Single(c => c.Code == "AE");
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.GetAsync("/api/v1/platform/states")).StatusCode);

        var bad = await platform.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("GEO-BAD", inr, "geo.bad") with { CountryId = uae.Id, StateId = kerala.Id }, ApiHost.Json);
        Assert.Equal(["stateId"], await ErrorFieldsAsync(bad));
        Assert.Equal(0L, await ScalarAsync<long>(host.Database, """SELECT COUNT(*) FROM "Companies" WHERE "Code" = 'GEO-BAD'"""));

        var good = await platform.PostAsJsonAsync("/api/v1/platform/companies", NewCompany("GEO-OK", inr, "geo.ok") with { CountryId = india.Id, StateId = kerala.Id }, ApiHost.Json);
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        var companyId = (await JsonAsync(good)).GetProperty("id").GetGuid();
        Assert.Equal(1L, await ScalarAsync<long>(host.Database,
            $"""SELECT COUNT(*) FROM "BusinessSettings" WHERE "CompanyId" = '{companyId}' AND "CountryId" = '{india.Id}' AND "StateId" = '{kerala.Id}' AND "State" IS NULL"""));
    }
}
