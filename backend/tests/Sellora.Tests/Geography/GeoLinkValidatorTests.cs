using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Geography;

/// <summary>GeoLinkValidator: BR5 (unchanged inactive link survives), BR7, state-without-country, D17 clearing, inactive-country states.</summary>
public class GeoLinkValidatorTests
{
    private static async Task<(SelloraDbContext Db, Country In, State Kl, Country Ae, State Du)> NewAsync()
    {
        var db = TestDb.NewInMemory();
        var india = new Country { Id = Guid.NewGuid(), Code = "IN", Name = "India" };
        var uae = new Country { Id = Guid.NewGuid(), Code = "AE", Name = "UAE" };
        var kl = new State { Id = Guid.NewGuid(), CountryId = india.Id, Code = "KL", Name = "Kerala" };
        var du = new State { Id = Guid.NewGuid(), CountryId = uae.Id, Code = "DU", Name = "Dubai", Kind = StateKind.Emirate };
        db.Countries.AddRange(india, uae);
        db.States.AddRange(kl, du);
        await db.SaveChangesAsync();
        return (db, india, kl, uae, du);
    }

    private static async Task<string> FieldOf(Task act) => (await Assert.ThrowsAsync<DomainValidationException>(() => act)).Field;

    [Fact]
    public async Task BothNull_CountryOnly_AndMatchingPair_AreValid()
    {
        var (db, india, kl, _, _) = await NewAsync();

        await GeoLinkValidator.ValidateAsync(db, null, null, india.Id, kl.Id, default); // D17: clearing is valid
        await GeoLinkValidator.ValidateAsync(db, india.Id, null, null, null, default);
        await GeoLinkValidator.ValidateAsync(db, india.Id, kl.Id, null, null, default);
    }

    [Fact]
    public async Task StateWithoutCountry_Is400CountryId()
    {
        var (db, _, kl, _, _) = await NewAsync();
        Assert.Equal("countryId", await FieldOf(GeoLinkValidator.ValidateAsync(db, null, kl.Id, null, null, default)));
    }

    [Fact]
    public async Task UnknownIds_AndStateOfAnotherCountry_Are400()
    {
        var (db, india, _, _, du) = await NewAsync();

        Assert.Equal("countryId", await FieldOf(GeoLinkValidator.ValidateAsync(db, Guid.NewGuid(), null, null, null, default)));
        Assert.Equal("stateId", await FieldOf(GeoLinkValidator.ValidateAsync(db, india.Id, Guid.NewGuid(), null, null, default)));
        Assert.Equal("stateId", await FieldOf(GeoLinkValidator.ValidateAsync(db, india.Id, du.Id, null, null, default)));
    }

    [Fact]
    public async Task InactiveState_NewlySelectedIsRejected_ButUnchangedStoredLinkSurvives()
    {
        var (db, india, kl, _, _) = await NewAsync();
        kl.IsActive = false;
        await db.SaveChangesAsync();

        Assert.Equal("stateId", await FieldOf(GeoLinkValidator.ValidateAsync(db, india.Id, kl.Id, null, null, default)));
        await GeoLinkValidator.ValidateAsync(db, india.Id, kl.Id, india.Id, kl.Id, default);
    }

    [Fact]
    public async Task InactiveCountry_NewlySelectedIsRejected_UnchangedSurvives_AndItsStatesAreNotNewlySelectable()
    {
        var (db, india, kl, _, _) = await NewAsync();
        india.IsActive = false;
        await db.SaveChangesAsync();

        Assert.Equal("countryId", await FieldOf(GeoLinkValidator.ValidateAsync(db, india.Id, null, null, null, default)));
        // Country unchanged, state newly selected under it (state itself active): still rejected.
        Assert.Equal("stateId", await FieldOf(GeoLinkValidator.ValidateAsync(db, india.Id, kl.Id, india.Id, null, default)));
        await GeoLinkValidator.ValidateAsync(db, india.Id, kl.Id, india.Id, kl.Id, default);
    }

    [Fact]
    public async Task ChangingToAnotherInactiveValue_IsRejected()
    {
        var (db, india, kl, uae, du) = await NewAsync();
        uae.IsActive = false;
        du.IsActive = false;
        await db.SaveChangesAsync();

        Assert.Equal("countryId", await FieldOf(GeoLinkValidator.ValidateAsync(db, uae.Id, du.Id, india.Id, kl.Id, default)));
    }
}
