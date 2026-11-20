using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.Branches;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Geography;

/// <summary>
/// Country/State relink on Branch, Business Settings, Customer and Supplier through their controllers (country-state-masters AC5, AC6, AC13-AC16):
/// state of another country 400 stateId, state without country 400 countryId, inactive link rejected when new but kept when unchanged, null clears (D17),
/// legacy State text is never written and still returned as the hint. Onboarding is in GeoOnboardingTests.
/// </summary>
public class GeoAddressLinkTests
{
    private sealed record Geo(Guid India, Guid Kerala, Guid GoaInactive, Guid Uae, Guid Dubai, Guid OmanInactive, Guid Muscat);

    private sealed record Result(string[] Errors, Guid? CountryId, Guid? StateId, string? CountryName, string? StateName, string? LegacyState)
    {
        public bool Ok => Errors.Length == 0;
    }

    /// <summary>One address owner behind its real controller. Every call uses a new DbContext and controller, like one HTTP request.</summary>
    private sealed class Owner(BranchWorld w, Geo geo, string kind)
    {
        private Guid _id;
        private int _n;

        public Geo Geo => geo;

        public static async Task<(BranchWorld W, Geo G)> NewWorldAsync()
        {
            var w = await BranchWorld.CreateAsync();
            await using var db = w.Db();
            var india = new Country { Id = Guid.NewGuid(), Code = "IN", Name = "India" };
            var uae = new Country { Id = Guid.NewGuid(), Code = "AE", Name = "UAE" };
            var oman = new Country { Id = Guid.NewGuid(), Code = "OM", Name = "Oman", IsActive = false };
            var kerala = new State { Id = Guid.NewGuid(), CountryId = india.Id, Code = "KL", Name = "Kerala", GstStateCode = "32" };
            var goa = new State { Id = Guid.NewGuid(), CountryId = india.Id, Code = "GA", Name = "Goa", GstStateCode = "30", IsActive = false };
            var dubai = new State { Id = Guid.NewGuid(), CountryId = uae.Id, Code = "DU", Name = "Dubai", Kind = StateKind.Emirate };
            var muscat = new State { Id = Guid.NewGuid(), CountryId = oman.Id, Code = "MC", Name = "Muscat", Kind = StateKind.Governorate };
            db.Companies.Add(new Company { Id = Sellora.Domain.Companies.DefaultTenant.CompanyId, Code = "DEF", Name = "Sellora Mart" });
            db.Countries.AddRange(india, uae, oman);
            db.States.AddRange(kerala, goa, dubai, muscat);
            await db.SaveChangesAsync();
            return (w, new Geo(india.Id, kerala.Id, goa.Id, uae.Id, dubai.Id, oman.Id, muscat.Id));
        }

        public async Task InitAsync()
        {
            await using var db = w.Db();
            _id = kind switch
            {
                "customer" => await AddAsync(db, new Customer { Id = Guid.NewGuid(), Name = "Existing" }, c => c.Id),
                "supplier" => await AddAsync(db, new Supplier { Id = Guid.NewGuid(), Name = "Existing Supplier", CurrencyId = w.CurrencyId }, s => s.Id),
                "branch" => w.Blr,
                _ => Guid.Empty,
            };
        }

        private static async Task<Guid> AddAsync<T>(SelloraDbContext db, T entity, Func<T, Guid> id) where T : class
        {
            db.Add(entity);
            await db.SaveChangesAsync();
            return id(entity);
        }

        /// <summary>Sets the stored link directly (bypassing validation), e.g. a link that has since become inactive.</summary>
        public async Task SeedLinkAsync(Guid? country, Guid? state, string? legacy = null)
        {
            await using var db = w.Db();
            switch (kind)
            {
                case "customer": var c = await db.Customers.SingleAsync(x => x.Id == _id); (c.CountryId, c.StateId) = (country, state); break;
                case "supplier": var s = await db.Suppliers.SingleAsync(x => x.Id == _id); (s.CountryId, s.StateId) = (country, state); break;
                case "branch": var b = await db.Branches.SingleAsync(x => x.Id == _id); (b.CountryId, b.StateId, b.State) = (country, state, legacy); break;
                default: var t = await db.BusinessSettings.SingleAsync(); (t.CountryId, t.StateId, t.State) = (country, state, legacy); break;
            }
            await db.SaveChangesAsync();
        }

        public async Task<Result> UpdateAsync(Guid? country, Guid? state, string? legacyRequestState = null)
        {
            await using var db = w.Db();
            var ctx = Context();
            var branchService = new BranchService(db, new StockLocationUsageService(db));
            var n = ++_n;
            object? value = null; IActionResult? outcome;
            switch (kind)
            {
                case "customer":
                    var cr = await new CustomersController(db) { ControllerContext = ctx }.Update(_id, new UpsertCustomerRequest("Existing " + n, null, null, country, state), default);
                    outcome = cr.Result; break;
                case "supplier":
                    var sr = await new SuppliersController(db) { ControllerContext = ctx }.Update(_id, new UpsertSupplierRequest("Existing Supplier " + n, null, null, null, w.CurrencyId, country, state), default);
                    outcome = sr.Result; break;
                case "branch":
                    var br = await new BranchesController(db, branchService) { ControllerContext = ctx }.Update(_id,
                        new UpdateBranchRequest("Bengaluru " + n, null, null, legacyRequestState, null, null, null, null, country, state), default);
                    outcome = br.Result; break;
                default:
                    var tr = await new SettingsController(db, branchService) { ControllerContext = ctx }.Update(
                        new UpdateBusinessSettingsRequest("Sellora Mart " + n, null, null, legacyRequestState, null, null, null, "INV", w.CurrencyId, null, null, country, state), default);
                    outcome = tr.Result; break;
            }
            return Read(outcome, ref value);
        }

        public async Task<Result> CreateAsync(Guid? country, Guid? state)
        {
            await using var db = w.Db();
            var ctx = Context();
            var n = ++_n;
            object? value = null; IActionResult? outcome;
            switch (kind)
            {
                case "customer":
                    outcome = (await new CustomersController(db) { ControllerContext = ctx }.Create(new UpsertCustomerRequest("New " + n, null, null, country, state), default)).Result; break;
                case "supplier":
                    outcome = (await new SuppliersController(db) { ControllerContext = ctx }.Create(new UpsertSupplierRequest("New Supplier " + n, null, null, null, w.CurrencyId, country, state), default)).Result; break;
                default:
                    outcome = (await new BranchesController(db, new BranchService(db, new StockLocationUsageService(db))) { ControllerContext = ctx }.Create(
                        new CreateBranchRequest("N" + n, "New branch " + n, null, null, "Typed text", null, null, null, null, country, state), default)).Result; break;
            }
            return Read(outcome, ref value);
        }

        private Result Read(IActionResult? outcome, ref object? value)
        {
            var obj = Assert.IsAssignableFrom<ObjectResult>(outcome);
            if (obj.Value is ValidationProblemDetails problem) return new Result(problem.Errors.Keys.ToArray(), null, null, null, null, null);
            var dto = obj.Value!;
            T? Prop<T>(string name) => (T?)dto.GetType().GetProperty(name)!.GetValue(dto);
            return new Result([], Prop<Guid?>("CountryId"), Prop<Guid?>("StateId"), Prop<string>("CountryName"), Prop<string>("StateName"),
                dto.GetType().GetProperty("State")?.GetValue(dto) as string);
        }

        public async Task<(Guid? Country, Guid? State, string? Legacy)> StoredAsync(Func<Guid, bool>? exclude = null)
        {
            await using var db = w.Db();
            return kind switch
            {
                "customer" => await db.Customers.Where(x => x.Id == _id).Select(x => ValueTuple.Create(x.CountryId, x.StateId, (string?)null)).SingleAsync(),
                "supplier" => await db.Suppliers.Where(x => x.Id == _id).Select(x => ValueTuple.Create(x.CountryId, x.StateId, (string?)null)).SingleAsync(),
                "branch" => await db.Branches.Where(x => x.Id == _id).Select(x => ValueTuple.Create(x.CountryId, x.StateId, x.State)).SingleAsync(),
                _ => await db.BusinessSettings.Select(x => ValueTuple.Create(x.CountryId, x.StateId, x.State)).SingleAsync(),
            };
        }

        private ControllerContext Context()
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, w.UserId.ToString()) };
            var services = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider();
            return new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), RequestServices = services } };
        }
    }

    private static async Task<Owner> NewOwnerAsync(string kind)
    {
        var (w, g) = await Owner.NewWorldAsync();
        var owner = new Owner(w, g, kind);
        await owner.InitAsync();
        return owner;
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("supplier")]
    [InlineData("branch")]
    [InlineData("settings")]
    public async Task Update_ValidLink_IsStored_AndDtoCarriesNames(string kind)
    {
        var o = await NewOwnerAsync(kind);

        var r = await o.UpdateAsync(o.Geo.India, o.Geo.Kerala);

        Assert.True(r.Ok);
        Assert.Equal((o.Geo.India, o.Geo.Kerala, "India", "Kerala"), (r.CountryId, r.StateId, r.CountryName, r.StateName));
        var stored = await o.StoredAsync();
        Assert.Equal((o.Geo.India, o.Geo.Kerala), (stored.Country, stored.State));
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("supplier")]
    [InlineData("branch")]
    [InlineData("settings")]
    public async Task Update_StateOfAnotherCountry_Is400OnStateId_AndStateWithoutCountryIs400OnCountryId(string kind)
    {
        var o = await NewOwnerAsync(kind);

        Assert.Equal(["stateId"], (await o.UpdateAsync(o.Geo.India, o.Geo.Dubai)).Errors);
        Assert.Equal(["countryId"], (await o.UpdateAsync(null, o.Geo.Kerala)).Errors);
        Assert.Equal(["countryId"], (await o.UpdateAsync(Guid.NewGuid(), null)).Errors);
        Assert.Equal(["stateId"], (await o.UpdateAsync(o.Geo.India, Guid.NewGuid())).Errors);
        var stored = await o.StoredAsync();
        Assert.Equal((null, null), (stored.Country, stored.State));
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("supplier")]
    [InlineData("branch")]
    [InlineData("settings")]
    public async Task Update_InactiveNewlySelected_Is400_ButUnchangedInactiveLinkSurvivesAnEdit(string kind)
    {
        var o = await NewOwnerAsync(kind);

        Assert.Equal(["stateId"], (await o.UpdateAsync(o.Geo.India, o.Geo.GoaInactive)).Errors);
        Assert.Equal(["countryId"], (await o.UpdateAsync(o.Geo.OmanInactive, null)).Errors);

        // the link was valid when stored and has since become inactive: re-posting the same ids with another field changed is accepted (AC6)
        await o.SeedLinkAsync(o.Geo.India, o.Geo.GoaInactive);
        var kept = await o.UpdateAsync(o.Geo.India, o.Geo.GoaInactive);
        Assert.True(kept.Ok, string.Join(",", kept.Errors));
        Assert.Equal((o.Geo.India, o.Geo.GoaInactive, "Goa"), (kept.CountryId, kept.StateId, kept.StateName));

        await o.SeedLinkAsync(o.Geo.OmanInactive, o.Geo.Muscat);
        Assert.True((await o.UpdateAsync(o.Geo.OmanInactive, o.Geo.Muscat)).Ok); // inactive country and its state, both unchanged
        Assert.True((await o.UpdateAsync(o.Geo.OmanInactive, null)).Ok);

        // moving to a different inactive value is not "unchanged"
        await o.SeedLinkAsync(o.Geo.India, o.Geo.GoaInactive);
        Assert.Equal(["countryId"], (await o.UpdateAsync(o.Geo.OmanInactive, null)).Errors);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("supplier")]
    [InlineData("branch")]
    [InlineData("settings")]
    public async Task Update_NullClearsTheLink_D17(string kind)
    {
        var o = await NewOwnerAsync(kind);
        await o.SeedLinkAsync(o.Geo.India, o.Geo.Kerala);

        var r = await o.UpdateAsync(null, null);

        Assert.True(r.Ok);
        Assert.Equal((null, null), (r.CountryId, r.StateId));
        var stored = await o.StoredAsync();
        Assert.Equal((null, null), (stored.Country, stored.State));
        // country alone (no state) is valid too (BR8)
        Assert.True((await o.UpdateAsync(o.Geo.Uae, null)).Ok);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("supplier")]
    [InlineData("branch")]
    public async Task Create_ValidatesWithCreateSemantics_AnyInactiveRejected_ValidLinkStored(string kind)
    {
        var o = await NewOwnerAsync(kind);

        Assert.Equal(["stateId"], (await o.CreateAsync(o.Geo.India, o.Geo.GoaInactive)).Errors);
        Assert.Equal(["countryId"], (await o.CreateAsync(o.Geo.OmanInactive, null)).Errors);
        Assert.Equal(["stateId"], (await o.CreateAsync(o.Geo.India, o.Geo.Dubai)).Errors);
        Assert.Equal(["countryId"], (await o.CreateAsync(null, o.Geo.Kerala)).Errors);

        var ok = await o.CreateAsync(o.Geo.Uae, o.Geo.Dubai);
        Assert.True(ok.Ok);
        Assert.Equal(("UAE", "Dubai"), (ok.CountryName, ok.StateName));
        Assert.True((await o.CreateAsync(null, null)).Ok); // BR8: both null is valid
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("settings")]
    public async Task LegacyStateText_IsIgnoredOnWrite_AndReturnedAsTheReadOnlyHint(string kind)
    {
        var o = await NewOwnerAsync(kind);
        await o.SeedLinkAsync(null, null, legacy: "Kerla");

        var r = await o.UpdateAsync(o.Geo.India, o.Geo.Kerala, legacyRequestState: "Something else");

        Assert.True(r.Ok);
        Assert.Equal("Kerla", r.LegacyState); // hint returned untouched
        Assert.Equal("Kerla", (await o.StoredAsync()).Legacy); // never written (D6/D23)
    }

    [Fact]
    public async Task BranchCreate_LegacyStateText_IsNotWritten()
    {
        var o = await NewOwnerAsync("branch");

        var r = await o.CreateAsync(o.Geo.India, o.Geo.Kerala); // request carries State = "Typed text"

        Assert.True(r.Ok);
        Assert.Null(r.LegacyState);
    }
}
