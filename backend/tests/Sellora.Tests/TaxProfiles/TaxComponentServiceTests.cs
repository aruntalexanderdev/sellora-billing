using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;

namespace Sellora.Tests.TaxProfiles;

/// <summary>BR1, BR6: components are created, edited and deactivated, never deleted; the method is fixed once a version uses it.</summary>
public class TaxComponentServiceTests
{
    [Fact]
    public async Task Create_NormalisesTheCodeAndStoresTheComponent()
    {
        var w = await TaxWorld.CreateAsync("IN", seed: false);

        var c = await w.Components.CreateAsync(new UpsertTaxComponentInput(w.India.Id, " cgst ", " Central GST ", TaxComponentKind.Cgst, TaxCalculationMethod.Percent));

        Assert.Equal("CGST", c.Code);
        Assert.Equal("Central GST", c.Name);
        Assert.True(c.IsActive);
        Assert.Equal(w.India.Id, c.CountryId);
    }

    [Fact]
    public async Task Create_DuplicateCodeOrNameInTheSameCountry_Refused_ButAnotherCountryMayReuseThem()
    {
        var w = await TaxWorld.CreateAsync("AE");
        // VAT / Value added tax exist for the UAE from the seed.

        Assert.Equal("code", await TaxWorld.FieldOfAsync(() => w.Components.CreateAsync(new(w.Uae.Id, "vat", "Other name", TaxComponentKind.Vat, TaxCalculationMethod.Percent))));
        Assert.Equal("name", await TaxWorld.FieldOfAsync(() => w.Components.CreateAsync(new(w.Uae.Id, "VAT2", "value ADDED tax", TaxComponentKind.Vat, TaxCalculationMethod.Percent))));
        var oman = await w.Components.CreateAsync(new(w.Oman.Id, "VAT", "Value added tax", TaxComponentKind.Vat, TaxCalculationMethod.Percent));
        Assert.Equal(w.Oman.Id, oman.CountryId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("-LEADING")]
    [InlineData("THIS-CODE-IS-WAY-TOO-LONG")]
    public async Task Create_InvalidCode_RefusedOnCode(string code)
    {
        var w = await TaxWorld.CreateAsync("IN", seed: false);

        Assert.Equal("code", await TaxWorld.FieldOfAsync(() => w.Components.CreateAsync(new(w.India.Id, code, "Name", TaxComponentKind.Cess, TaxCalculationMethod.Percent))));
    }

    [Fact]
    public async Task Create_UnknownCountry_RefusedOnCountryId()
    {
        var w = await TaxWorld.CreateAsync("IN", seed: false);

        Assert.Equal("countryId", await TaxWorld.FieldOfAsync(() => w.Components.CreateAsync(new(Guid.NewGuid(), "CESS", "Cess", TaxComponentKind.Cess, TaxCalculationMethod.Percent))));
    }

    [Fact]
    public async Task Update_UnusedComponent_MayChangeMethodAndKind()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var cess = await w.ComponentAsync("CESS");

        var updated = await w.Components.UpdateAsync(cess.Id, new UpdateTaxComponentInput("CESS", "Cess (compensation)", TaxComponentKind.Other, TaxCalculationMethod.AmountPerBaseUnit));

        Assert.Equal(TaxCalculationMethod.AmountPerBaseUnit, updated!.Method);
        Assert.NotEqual(cess.Id, updated.Id); // the method is part of a key: the row is replaced
        Assert.Equal(1, await w.Db.TaxComponents.CountAsync(c => c.Code == "CESS"));
        Assert.Equal(TaxComponentKind.Other, updated.Kind);
        Assert.Equal("Cess (compensation)", updated.Name);
    }

    [Fact]
    public async Task Update_ComponentUsedByAVersion_MethodAndKindAreFixed_ButNameMayChange()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var cgst = await w.ComponentAsync("CGST"); // used by GST 5/18/40

        Assert.Equal("method", await TaxWorld.FieldOfAsync(() => w.Components.UpdateAsync(cgst.Id, new("CGST", "Central GST", TaxComponentKind.Cgst, TaxCalculationMethod.AmountPerBaseUnit))));
        Assert.Equal("kind", await TaxWorld.FieldOfAsync(() => w.Components.UpdateAsync(cgst.Id, new("CGST", "Central GST", TaxComponentKind.Sgst, TaxCalculationMethod.Percent))));

        var renamed = await w.Components.UpdateAsync(cgst.Id, new("CGST", "Central goods and services tax", TaxComponentKind.Cgst, TaxCalculationMethod.Percent));
        Assert.Equal("Central goods and services tax", renamed!.Name);
    }

    [Fact]
    public async Task Update_DuplicateOfAnotherComponent_Refused()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var sgst = await w.ComponentAsync("SGST");

        Assert.Equal("code", await TaxWorld.FieldOfAsync(() => w.Components.UpdateAsync(sgst.Id, new("CGST", "State GST", TaxComponentKind.Sgst, TaxCalculationMethod.Percent))));
        Assert.Equal("name", await TaxWorld.FieldOfAsync(() => w.Components.UpdateAsync(sgst.Id, new("SGST", "central gst", TaxComponentKind.Sgst, TaxCalculationMethod.Percent))));
    }

    [Fact]
    public async Task Update_AndSetActive_UnknownComponent_ReturnNull()
    {
        var w = await TaxWorld.CreateAsync("IN", seed: false);

        Assert.Null(await w.Components.UpdateAsync(Guid.NewGuid(), new("X", "X", TaxComponentKind.Cess, TaxCalculationMethod.Percent)));
        Assert.Null(await w.Components.SetActiveAsync(Guid.NewGuid(), false));
    }

    [Fact]
    public async Task SetActive_DeactivatesAndReactivates_NeverDeletes()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var cess = await w.ComponentAsync("CESS");

        Assert.False((await w.Components.SetActiveAsync(cess.Id, false))!.IsActive);
        Assert.False((await w.Db.TaxComponents.SingleAsync(c => c.Id == cess.Id)).IsActive);
        Assert.True((await w.Components.SetActiveAsync(cess.Id, true))!.IsActive);
        Assert.Equal(6, await w.Db.TaxComponents.CountAsync());
    }
}
