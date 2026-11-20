using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Auth;
using Sellora.Domain.Entities;
using Sellora.Domain.Permissions;
using Sellora.Domain.Platform;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Security;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Geography;

/// <summary>Company onboarding (D10/D26, AC21): Country/State validated with create semantics before any tenant row is written, stored on the new company's
/// BusinessSettings, legacy State text never written.</summary>
public class GeoOnboardingTests
{
    private sealed class Hasher : IPasswordHasher
    {
        public string Hash(string password) => "hash:" + password;
        public bool Verify(string password, string hash) => hash == "hash:" + password;
    }

    private sealed class World
    {
        public CompanyContext Context { get; } = new();
        public SelloraDbContext Db { get; }
        public Currency Inr { get; } = new() { Id = Guid.NewGuid(), Code = "INR", Name = "Rupee", Symbol = "R" };
        public Country India { get; } = new() { Id = Guid.NewGuid(), Code = "IN", Name = "India" };
        public Country Uae { get; } = new() { Id = Guid.NewGuid(), Code = "AE", Name = "UAE" };
        public Country Oman { get; } = new() { Id = Guid.NewGuid(), Code = "OM", Name = "Oman", IsActive = false };
        public State Kerala { get; }
        public State Dubai { get; }
        public State Goa { get; }
        public State Muscat { get; }
        public Guid OpsId { get; } = Guid.NewGuid();

        public World()
        {
            var options = new DbContextOptionsBuilder<SelloraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            Db = new SelloraDbContext(options, Context, TestCompany.Branch);
            Kerala = new State { Id = Guid.NewGuid(), CountryId = India.Id, Code = "KL", Name = "Kerala" };
            Goa = new State { Id = Guid.NewGuid(), CountryId = India.Id, Code = "GA", Name = "Goa", IsActive = false };
            Dubai = new State { Id = Guid.NewGuid(), CountryId = Uae.Id, Code = "DU", Name = "Dubai" };
            Muscat = new State { Id = Guid.NewGuid(), CountryId = Oman.Id, Code = "MC", Name = "Muscat" };
            Db.Currencies.Add(Inr);
            Db.Countries.AddRange(India, Uae, Oman);
            Db.States.AddRange(Kerala, Goa, Dubai, Muscat);
            foreach (var def in GovernancePermissionCatalog.All)
            {
                Db.GovernancePermissions.Add(new GovernancePermission { Id = GovernancePermissionCatalog.IdFor(def.Code), Code = def.Code, Description = def.Description });
            }
            Db.Users.Add(new User { Id = OpsId, FirstName = "ops", LastName = "T", Username = "ops", Email = "ops@x.io", PasswordHash = "x" });
            Db.PlatformAdministrators.Add(new PlatformAdministrator { UserId = OpsId, Status = PlatformAdminStatus.Active, GrantedAt = DateTimeOffset.UtcNow });
            Db.SaveChanges();
        }

        public PlatformService Service() => new(Db, Context, new Hasher(), new JwtTokenService(Microsoft.Extensions.Options.Options.Create(new JwtOptions
        {
            Issuer = "test", Audience = "test", SigningKey = Convert.ToBase64String(new byte[32]), AccessTokenMinutes = 15, RefreshTokenDays = 14,
        })), Microsoft.Extensions.Options.Options.Create(new PlatformOptions { CompanyCreationEnabled = true }));

        public CreateCompanyInput Input(Guid? country, Guid? state, string? legacyState = "Typed State") =>
            new("ACME", "Acme Retail", "Acme Retail Pvt Ltd", "AC", Inr.Id, null, "1 Road", "Pune", legacyState, null, null,
                new PlatformOwnerInput(null, new PlatformNewOwner("Asha", "Rao", "asha", "asha@acme.io", null, "Str0ng!Passw0rd")), "Onboarding", country, state);

        public async Task AssertNothingWrittenAsync()
        {
            Assert.Empty(await Db.Companies.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await Db.BusinessSettings.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await Db.Branches.IgnoreQueryFilters().ToListAsync());
            Assert.Equal(1, await Db.Users.CountAsync()); // only the operator
        }
    }

    private static async Task<string> FieldAsync(Func<Task> act) => (await Assert.ThrowsAsync<DomainValidationException>(act)).Field;

    [Fact]
    public async Task CreateCompany_WithCountryAndState_StoresThemOnTheNewBusinessSettings_AndNeverWritesTheLegacyText()
    {
        var w = new World();

        var created = await w.Service().CreateCompanyAsync(w.Input(w.India.Id, w.Kerala.Id), w.OpsId, "trace");

        var settings = await w.Db.BusinessSettings.IgnoreQueryFilters().SingleAsync(s => s.CompanyId == created.Id);
        Assert.Equal((w.India.Id, w.Kerala.Id), (settings.CountryId, settings.StateId));
        Assert.Null(settings.State);
        Assert.Equal("Pune", settings.City);
    }

    [Fact]
    public async Task CreateCompany_NeitherOrCountryOnly_IsValid()
    {
        var w = new World();

        var created = await w.Service().CreateCompanyAsync(w.Input(w.Uae.Id, null, legacyState: null), w.OpsId, null);

        var settings = await w.Db.BusinessSettings.IgnoreQueryFilters().SingleAsync(s => s.CompanyId == created.Id);
        Assert.Equal((w.Uae.Id, (Guid?)null), (settings.CountryId, settings.StateId));
    }

    [Fact]
    public async Task CreateCompany_NoLink_StoresNullLinks()
    {
        var w = new World();

        var created = await w.Service().CreateCompanyAsync(w.Input(null, null), w.OpsId, null);

        var settings = await w.Db.BusinessSettings.IgnoreQueryFilters().SingleAsync(s => s.CompanyId == created.Id);
        Assert.Equal(((Guid?)null, (Guid?)null, (string?)null), (settings.CountryId, settings.StateId, settings.State));
    }

    // A request provisions at most one company (the company context is set once), so every invalid case gets a new request over a new store.
    [Theory]
    [InlineData("kerala-no-country", "countryId")]
    [InlineData("india-dubai", "stateId")]
    [InlineData("india-goa-inactive", "stateId")]
    [InlineData("oman-inactive", "countryId")]
    [InlineData("unknown-country", "countryId")]
    [InlineData("unknown-state", "stateId")]
    public async Task CreateCompany_InvalidLinks_Are400_AndNoTenantRowIsWritten(string scenario, string field)
    {
        var w = new World();
        var input = scenario switch
        {
            "kerala-no-country" => w.Input(null, w.Kerala.Id),
            "india-dubai" => w.Input(w.India.Id, w.Dubai.Id),
            "india-goa-inactive" => w.Input(w.India.Id, w.Goa.Id),
            "oman-inactive" => w.Input(w.Oman.Id, null),
            "unknown-country" => w.Input(Guid.NewGuid(), null),
            _ => w.Input(w.India.Id, Guid.NewGuid()),
        };

        Assert.Equal(field, await FieldAsync(() => w.Service().CreateCompanyAsync(input, w.OpsId, null)));

        await w.AssertNothingWrittenAsync();
    }
}
