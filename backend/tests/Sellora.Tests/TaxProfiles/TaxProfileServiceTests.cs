using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Services;

namespace Sellora.Tests.TaxProfiles;

/// <summary>Profiles and versions: BR2–BR6, TP-1 design (T19, D3 mirror, D5 append-only), AC10, AC11.</summary>
public class TaxProfileServiceTests
{
    private static CreateTaxProfileInput NewGst(TaxWorld w, string code = "GST-12", string name = "GST 12%", TaxTreatment treatment = TaxTreatment.Taxable, AddTaxVersionInput? v = null) =>
        new(w.India.Id, code, name, TaxRegime.Gst, treatment, v);

    private static bool HasField(IReadOnlyList<FieldError> errors, string field) => errors.Any(e => e.Field == field);

    // ------------------------------------------------------------------ create

    [Fact]
    public async Task Create_StoresTheProfileWithAMirrorTaxAtZeroUntilItHasAVersion()
    {
        var w = await TaxWorld.CreateAsync("IN");

        var p = await w.Profiles.CreateAsync(NewGst(w, " gst-12 ", " GST 12% "), null);

        Assert.Equal("GST-12", p.Code);
        Assert.Equal("GST 12%", p.Name);
        Assert.True(p.IsActive);
        var mirror = await w.MirrorAsync(p);
        Assert.Equal("GST 12%", mirror.Name);
        Assert.Equal(0m, mirror.RatePercent);
        Assert.Empty(await w.Db.TaxProfileVersions.Where(v => v.TaxProfileId == p.Id).ToListAsync());
    }

    [Fact]
    public async Task Create_WithInitialVersion_CreatesTheVersionAndSyncsTheMirror()
    {
        var w = await TaxWorld.CreateAsync("IN");

        var p = await w.Profiles.CreateAsync(NewGst(w, v: TaxWorld.Version(new DateOnly(2026, 1, 1), await w.GstSetAsync(12m))), Guid.NewGuid());

        var v = await w.Db.TaxProfileVersions.Include(x => x.Components).SingleAsync(x => x.TaxProfileId == p.Id);
        Assert.Equal(12m, v.TotalPercent);
        Assert.Equal(3, v.Components.Count);
        Assert.NotNull(v.CreatedByUserId);
        Assert.Equal(12m, (await w.MirrorAsync(p)).RatePercent);
    }

    [Fact]
    public async Task Create_InvalidInitialVersion_SavesNothing()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var before = await w.Db.TaxProfiles.CountAsync();

        await TaxWorld.FailsAsync(() => w.Profiles.CreateAsync(NewGst(w, v: TaxWorld.Version(new DateOnly(2026, 1, 1))), null));

        Assert.Equal(before, await w.Db.TaxProfiles.CountAsync());
    }

    [Fact]
    public async Task Create_RegimeMustMatchTheCountry()
    {
        var w = await TaxWorld.CreateAsync("IN");

        Assert.Equal("regime", await TaxWorld.FieldOfAsync(() => w.Profiles.CreateAsync(new(w.India.Id, "VAT-5", "VAT 5%", TaxRegime.Vat, TaxTreatment.Taxable), null)));
        Assert.Equal("regime", await TaxWorld.FieldOfAsync(() => w.Profiles.CreateAsync(new(w.Uae.Id, "GST-5", "GST 5%", TaxRegime.Gst, TaxTreatment.Taxable), null)));
        var qatar = await w.Profiles.CreateAsync(new(w.Qatar.Id, "VAT-0", "Qatar VAT", TaxRegime.Vat, TaxTreatment.Exempt), null); // any other country: VAT
        Assert.Equal(TaxRegime.Vat, qatar.Regime);
    }

    [Fact]
    public async Task Create_DuplicateCodeOrNameForTheCountry_Refused_AnotherCountryMayReuse()
    {
        var w = await TaxWorld.CreateAsync("AE");

        Assert.Equal("code", await TaxWorld.FieldOfAsync(() => w.Profiles.CreateAsync(new(w.Uae.Id, "vat-5", "Another name", TaxRegime.Vat, TaxTreatment.Taxable), null)));
        Assert.Equal("name", await TaxWorld.FieldOfAsync(() => w.Profiles.CreateAsync(new(w.Uae.Id, "VAT-X", "vat 5%", TaxRegime.Vat, TaxTreatment.Taxable), null)));
        var oman = await w.Profiles.CreateAsync(new(w.Oman.Id, "VAT-5", "VAT 5%", TaxRegime.Vat, TaxTreatment.Taxable), null);
        Assert.NotEqual((await w.ProfileAsync("VAT-5", w.Uae)).Id, oman.Id);
        // Taxes.Name is unique per company: the second mirror gets the country code.
        Assert.Equal("VAT 5% (OM)", (await w.MirrorAsync(oman)).Name);
    }

    [Theory]
    [InlineData("", "name")]
    [InlineData("bad code", "name")]
    public async Task Create_InvalidCode_Refused(string code, string name)
    {
        var w = await TaxWorld.CreateAsync("IN");

        Assert.Equal("code", await TaxWorld.FieldOfAsync(() => w.Profiles.CreateAsync(NewGst(w, code, name), null)));
    }

    [Fact]
    public async Task Update_ChangesCodeAndNameOnly_AndRenamesTheMirror()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await w.ProfileAsync("GST-5");

        var updated = await w.Profiles.UpdateAsync(p.Id, new UpdateTaxProfileInput("gst-five", "GST five percent"));

        Assert.Equal("GST-FIVE", updated!.Code);
        Assert.Equal("GST five percent", (await w.MirrorAsync(updated)).Name);
        Assert.Equal(TaxTreatment.Taxable, updated.Treatment);
        Assert.Equal(w.India.Id, updated.CountryId);
        Assert.Equal("code", await TaxWorld.FieldOfAsync(() => w.Profiles.UpdateAsync(p.Id, new("GST-18", "x"))));
        Assert.Equal("name", await TaxWorld.FieldOfAsync(() => w.Profiles.UpdateAsync(p.Id, new("GST-FIVE", "gst 18%"))));
        Assert.Null(await w.Profiles.UpdateAsync(Guid.NewGuid(), new("A", "B")));
    }

    // ------------------------------------------------------------------ BR3 set rules

    private static async Task<TaxProfileVersion?> AddGst(TaxWorld w, TaxProfile p, DateOnly from, decimal rate) =>
        await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(from, await w.GstSetAsync(rate)), null);

    private static async Task<TaxProfile> NewProfileAsync(TaxWorld w, TaxTreatment treatment = TaxTreatment.Taxable) =>
        await w.Profiles.CreateAsync(NewGst(w, "NEW-GST", "New GST", treatment), null);

    [Fact]
    public async Task AddVersion_CompleteGstSet_StoresTotalAndComponentsAndSyncsTheMirror()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await NewProfileAsync(w);

        var v = await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2026, 1, 1), await w.GstSetAsync(12m)), Guid.NewGuid());

        Assert.Equal(12m, v!.TotalPercent);
        Assert.Null(v.EffectiveTo);
        Assert.Equal(3, v.Components.Count);
        Assert.All(v.Components, c => Assert.NotNull(c.TaxComponent));
        Assert.Equal(12m, (await w.MirrorAsync(p)).RatePercent);
    }

    [Fact]
    public async Task AddVersion_UtgstInsteadOfSgst_IsAccepted()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await NewProfileAsync(w);

        var v = await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2026, 1, 1),
            TaxWorld.Line(await w.ComponentAsync("CGST"), TaxApplicability.IntraState, 6),
            TaxWorld.Line(await w.ComponentAsync("UTGST"), TaxApplicability.IntraState, 6),
            TaxWorld.Line(await w.ComponentAsync("IGST"), TaxApplicability.InterState, 12)), null);

        Assert.Equal(12m, v!.TotalPercent);
    }

    [Fact]
    public async Task AddVersion_CessPercentAndPerUnit_PercentCountsTowardTotalPerUnitDoesNot()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await NewProfileAsync(w);
        var set = await w.GstSetAsync(28m);

        var v = await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2026, 1, 1),
            [.. set,
             TaxWorld.Line(await w.ComponentAsync("CESS"), TaxApplicability.Always, 5),
             TaxWorld.Line(await w.ComponentAsync("CESS-UNIT"), TaxApplicability.Always, amount: 2.5m)]), null);

        Assert.Equal(33m, v!.TotalPercent); // 14 + 14 + 5; the per-unit amount is not a percentage
        Assert.Equal(5, v.Components.Count);
        Assert.Equal(2.5m, v.Components.Single(c => c.AmountPerBaseUnit != null).AmountPerBaseUnit);
        Assert.Equal(33m, (await w.MirrorAsync(p)).RatePercent);
    }

    [Fact]
    public async Task AddVersion_IncompleteOrInconsistentGstSets_Refused()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await NewProfileAsync(w);
        var cgst = await w.ComponentAsync("CGST");
        var sgst = await w.ComponentAsync("SGST");
        var utgst = await w.ComponentAsync("UTGST");
        var igst = await w.ComponentAsync("IGST");
        var from = new DateOnly(2026, 1, 1);

        // missing IGST
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(from,
            TaxWorld.Line(cgst, TaxApplicability.IntraState, 9), TaxWorld.Line(sgst, TaxApplicability.IntraState, 9)), null)), "components"));
        // missing SGST
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(from,
            TaxWorld.Line(cgst, TaxApplicability.IntraState, 9), TaxWorld.Line(igst, TaxApplicability.InterState, 9)), null)), "components"));
        // CGST != SGST
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(from,
            TaxWorld.Line(cgst, TaxApplicability.IntraState, 9), TaxWorld.Line(sgst, TaxApplicability.IntraState, 8), TaxWorld.Line(igst, TaxApplicability.InterState, 17)), null)), "components"));
        // IGST != CGST + SGST
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(from,
            TaxWorld.Line(cgst, TaxApplicability.IntraState, 9), TaxWorld.Line(sgst, TaxApplicability.IntraState, 9), TaxWorld.Line(igst, TaxApplicability.InterState, 17)), null)), "components"));
        // SGST and UTGST together
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(from,
            TaxWorld.Line(cgst, TaxApplicability.IntraState, 9), TaxWorld.Line(sgst, TaxApplicability.IntraState, 9), TaxWorld.Line(utgst, TaxApplicability.IntraState, 9),
            TaxWorld.Line(igst, TaxApplicability.InterState, 18)), null)), "components"));
        Assert.Empty(await w.Db.TaxProfileVersions.Where(v => v.TaxProfileId == p.Id).ToListAsync());
    }

    [Fact]
    public async Task AddVersion_WrongApplicabilityOrRegimeForAComponent_RefusedOnThatLine()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await NewProfileAsync(w);
        var from = new DateOnly(2026, 1, 1);

        var igst = await w.ComponentAsync("IGST");
        var cess = await w.ComponentAsync("CESS");
        var igstIntra = await TaxWorld.ErrorsOfAsync(() => w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(from, TaxWorld.Line(igst, TaxApplicability.IntraState, 18)), null));
        Assert.True(HasField(igstIntra, "components[0].applicability"));
        var cessIntra = await TaxWorld.ErrorsOfAsync(() => w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(from, TaxWorld.Line(cess, TaxApplicability.IntraState, 1)), null));
        Assert.True(HasField(cessIntra, "components[0].applicability"));
        // VAT in a GST profile
        var oman = await w.Components.CreateAsync(new(w.India.Id, "VAT", "Value added tax", TaxComponentKind.Vat, TaxCalculationMethod.Percent));
        var vat = await TaxWorld.ErrorsOfAsync(() => w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(from, TaxWorld.Line(oman, TaxApplicability.Always, 5)), null));
        Assert.True(HasField(vat, "components[0].applicability"));
    }

    [Fact]
    public async Task AddVersion_RateAndAmountMustMatchTheComponentMethod_AndBeInRange()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await NewProfileAsync(w);
        var from = new DateOnly(2026, 1, 1);
        var set = await w.GstSetAsync(18m);
        var cess = await w.ComponentAsync("CESS");
        var unit = await w.ComponentAsync("CESS-UNIT");

        Task Add(params TaxVersionComponentInput[] extra) => w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(from, [.. set, .. extra]), null);

        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => Add(TaxWorld.Line(cess, TaxApplicability.Always))), "components[3].ratePercent")); // percent without rate
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => Add(TaxWorld.Line(cess, TaxApplicability.Always, 1, 2))), "components[3].amountPerBaseUnit"));
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => Add(TaxWorld.Line(unit, TaxApplicability.Always))), "components[3].amountPerBaseUnit"));
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => Add(TaxWorld.Line(unit, TaxApplicability.Always, 1, 2))), "components[3].ratePercent"));
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => Add(TaxWorld.Line(cess, TaxApplicability.Always, 101))), "components[3].ratePercent"));
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => Add(TaxWorld.Line(cess, TaxApplicability.Always, -1))), "components[3].ratePercent"));
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => Add(TaxWorld.Line(cess, TaxApplicability.Always, 1.00001m))), "components[3].ratePercent")); // 4 dp
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => Add(TaxWorld.Line(unit, TaxApplicability.Always, amount: 1.0000001m))), "components[3].amountPerBaseUnit")); // 6 dp
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => Add(TaxWorld.Line(unit, TaxApplicability.Always, amount: -1m))), "components[3].amountPerBaseUnit"));
        // listed twice
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => Add(TaxWorld.Line(cess, TaxApplicability.Always, 1), TaxWorld.Line(cess, TaxApplicability.Always, 2))), "components[4].taxComponentId"));
    }

    [Fact]
    public async Task AddVersion_UnknownInactiveOrForeignCountryComponent_Refused()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await NewProfileAsync(w);
        var from = new DateOnly(2026, 1, 1);
        var cess = await w.ComponentAsync("CESS");
        await w.Components.SetActiveAsync(cess.Id, false);
        var uaeVat = await w.Components.CreateAsync(new(w.Uae.Id, "VAT", "Value added tax", TaxComponentKind.Vat, TaxCalculationMethod.Percent));
        var set = await w.GstSetAsync(18m);

        Task Add(TaxVersionComponentInput extra) => w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(from, [.. set, extra]), null);

        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => Add(new(Guid.NewGuid(), TaxApplicability.Always, 1, null))), "components[3].taxComponentId"));
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => Add(TaxWorld.Line(cess, TaxApplicability.Always, 1))), "components[3].taxComponentId")); // inactive
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => Add(TaxWorld.Line(uaeVat, TaxApplicability.Always, 5))), "components[3].taxComponentId")); // other country
    }

    [Fact]
    public async Task AddVersion_VatProfile_NeedsExactlyOneVatLine()
    {
        var w = await TaxWorld.CreateAsync("AE");
        var vat = await w.ComponentAsync("VAT", w.Uae);
        var p = await w.Profiles.CreateAsync(new(w.Uae.Id, "VAT-X", "VAT custom", TaxRegime.Vat, TaxTreatment.Taxable), null);
        var from = new DateOnly(2026, 1, 1);

        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(from), null)), "components"));
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(from,
            TaxWorld.Line(vat, TaxApplicability.Always, 5), TaxWorld.Line(vat, TaxApplicability.IntraState, 5)), null)), "components[1].applicability"));
        var ok = await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(from, TaxWorld.Line(vat, TaxApplicability.Always, 7.5m)), null);
        Assert.Equal(7.5m, ok!.TotalPercent);
    }

    [Fact]
    public async Task AddVersion_NonTaxableTreatment_CarriesNoComponents()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var nil = await NewProfileAsync(w, TaxTreatment.NilRated);

        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => w.Profiles.AddVersionAsync(nil.Id, TaxWorld.Version(new DateOnly(2026, 1, 1), TaxWorld.Line(
            w.Db.TaxComponents.Single(c => c.Code == "CESS"), TaxApplicability.Always, 1)), null)), "components"));
        var v = await w.Profiles.AddVersionAsync(nil.Id, TaxWorld.Version(new DateOnly(2026, 1, 1)), null);
        Assert.Equal(0m, v!.TotalPercent);
        Assert.Empty(v.Components);
    }

    [Fact]
    public async Task AddVersion_TaxableWithZeroTotal_OrOver100_Refused()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await NewProfileAsync(w);
        var from = new DateOnly(2026, 1, 1);
        var gst100 = await w.GstSetAsync(100m);
        var cess = await w.ComponentAsync("CESS");

        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => AddGst(w, p, from, 0m)), "components")); // never "0% taxable" (BR2)
        var tooMuch = await TaxWorld.ErrorsOfAsync(() => w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(from, [.. gst100, TaxWorld.Line(cess, TaxApplicability.Always, 5)]), null));
        Assert.Contains(tooMuch, e => e.Field == "components" && e.Message.Contains("100"));
        Assert.True(HasField(await TaxWorld.ErrorsOfAsync(() => AddGst(w, p, from, 101m)), "components[2].ratePercent"));
    }

    [Fact]
    public async Task AddVersion_TotalWithMoreThanThreeDecimals_Refused()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await NewProfileAsync(w);
        var cgst = await w.ComponentAsync("CGST");
        var sgst = await w.ComponentAsync("SGST");
        var igst = await w.ComponentAsync("IGST");

        // 0.0001 + 0.0001 = 0.0002: each rate fits numeric(9,4), the total (the mirror tax is numeric(6,3)) does not fit 3 decimals.
        var errors = await TaxWorld.ErrorsOfAsync(() => w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2026, 1, 1),
            TaxWorld.Line(cgst, TaxApplicability.IntraState, 0.0001m), TaxWorld.Line(sgst, TaxApplicability.IntraState, 0.0001m), TaxWorld.Line(igst, TaxApplicability.InterState, 0.0002m)), null));
        Assert.Contains(errors, e => e.Field == "components" && e.Message.Contains("3 decimal"));

        var ok = await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2026, 1, 1),
            TaxWorld.Line(cgst, TaxApplicability.IntraState, 0.125m), TaxWorld.Line(sgst, TaxApplicability.IntraState, 0.125m), TaxWorld.Line(igst, TaxApplicability.InterState, 0.25m)), null);
        Assert.Equal(0.25m, ok!.TotalPercent);
    }

    [Fact]
    public async Task AddVersion_UnknownProfile_ReturnsNull()
    {
        var w = await TaxWorld.CreateAsync("IN");

        Assert.Null(await w.Profiles.AddVersionAsync(Guid.NewGuid(), TaxWorld.Version(new DateOnly(2026, 1, 1)), null));
    }

    // ------------------------------------------------------------------ BR4 version lifecycle, T19, AC10, AC11

    [Fact]
    public async Task AddVersion_LaterDate_ClosesThePreviousVersionTheDayBefore()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await NewProfileAsync(w);
        await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2025, 1, 1), await w.GstSetAsync(12m)), null);

        var second = await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2026, 4, 1), await w.GstSetAsync(5m)), null);

        var versions = await w.Db.TaxProfileVersions.Where(v => v.TaxProfileId == p.Id).OrderBy(v => v.EffectiveFrom).ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal(new DateOnly(2026, 3, 31), versions[0].EffectiveTo);
        Assert.Null(versions[1].EffectiveTo);
        Assert.Equal(second!.Id, versions[1].Id);
        Assert.Equal(5m, (await w.MirrorAsync(p)).RatePercent); // the version valid today (2026-11-24)
    }

    [Fact]
    public async Task AddVersion_EarlierThanTheLatestVersion_RefusedOnEffectiveFrom()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await NewProfileAsync(w);
        await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2026, 4, 1), await w.GstSetAsync(12m)), null);

        Assert.Equal("effectiveFrom", await TaxWorld.FieldOfAsync(() => AddGst(w, p, new DateOnly(2026, 3, 31), 5m)));
        Assert.Single(await w.Db.TaxProfileVersions.Where(v => v.TaxProfileId == p.Id).ToListAsync());
    }

    [Fact]
    public async Task AddVersion_SameDateAsTheLatest_ReplacesItWhileNothingUsesIt()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await NewProfileAsync(w);
        var first = await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2026, 4, 1), await w.GstSetAsync(12m)), null);

        var corrected = await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2026, 4, 1), await w.GstSetAsync(18m)), null);

        Assert.Equal(first!.Id, corrected!.Id);
        Assert.Equal(18m, corrected.TotalPercent);
        var stored = await w.Db.TaxProfileVersions.Include(v => v.Components).SingleAsync(v => v.TaxProfileId == p.Id);
        Assert.Equal(3, stored.Components.Count);
        Assert.Equal(new[] { 9m, 9m, 18m }, stored.Components.OrderBy(c => c.SortOrder).Select(c => c.RatePercent!.Value).ToArray());
        Assert.Equal(3, await w.Db.TaxProfileVersionComponents.CountAsync(c => c.TaxProfileVersionId == first.Id)); // the old rows are gone
        Assert.Equal(18m, (await w.MirrorAsync(p)).RatePercent);
    }

    [Fact]
    public async Task AddVersion_Ac11_ReplacingAUsedVersion_RefusedOnEffectiveFrom_ButALaterDateIsFine()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await NewProfileAsync(w);
        await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2026, 4, 1), await w.GstSetAsync(12m)), null);
        await w.UseVersionOnSaleAsync(p, new DateOnly(2026, 5, 10)); // an invoice with this profile's tax inside the version's range

        var ex = await TaxWorld.FailsAsync(() => AddGst(w, p, new DateOnly(2026, 4, 1), 18m));

        Assert.Equal("effectiveFrom", ex.Field);
        Assert.Contains("used", ex.Message);
        Assert.Equal(12m, (await w.Db.TaxProfileVersions.SingleAsync(v => v.TaxProfileId == p.Id)).TotalPercent); // untouched
        var later = await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2026, 6, 1), await w.GstSetAsync(18m)), null);
        Assert.Equal(18m, later!.TotalPercent);
        Assert.Equal(new DateOnly(2026, 5, 31), (await w.Db.TaxProfileVersions.SingleAsync(v => v.TaxProfileId == p.Id && v.EffectiveFrom == new DateOnly(2026, 4, 1))).EffectiveTo);
    }

    [Fact]
    public async Task AddVersion_AVersionUsedOnlyOutsideItsRange_CountsAsUnused()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await NewProfileAsync(w);
        await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2026, 4, 1), await w.GstSetAsync(12m)), null);
        await w.UseVersionOnSaleAsync(p, new DateOnly(2026, 3, 1)); // before the version starts

        var replaced = await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2026, 4, 1), await w.GstSetAsync(18m)), null);

        Assert.Equal(18m, replaced!.TotalPercent);
    }

    [Fact]
    public async Task AddVersion_T19_StartingAfterToday_RefusedOnEffectiveFrom()
    {
        var w = await TaxWorld.CreateAsync("IN"); // today = 2026-11-24
        var p = await NewProfileAsync(w);

        var tomorrow = await TaxWorld.FailsAsync(() => AddGst(w, p, TaxWorld.Today.AddDays(1), 12m));
        Assert.Equal("effectiveFrom", tomorrow.Field);
        Assert.Empty(await w.Db.TaxProfileVersions.Where(v => v.TaxProfileId == p.Id).ToListAsync());

        var today = await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(TaxWorld.Today, await w.GstSetAsync(12m)), null); // today itself is allowed
        var future = await w.GstSetAsync(5m);
        Assert.Equal(TaxWorld.Today, today!.EffectiveFrom);
        Assert.Equal("effectiveFrom", await TaxWorld.FieldOfAsync(() => w.Profiles.CreateAsync(NewGst(w, "FUT", "Future", v: TaxWorld.Version(TaxWorld.Today.AddDays(30), future)), null)));
    }

    [Fact]
    public async Task AddVersion_Ac10_TwoVersionsAcrossAChangeDate_ResolveThePreviousRateTheDayBeforeAndTheNewRateOnTheDay()
    {
        // GST 12% until 2025-09-21, GST 5% from 2025-09-22 (the 22 Sep 2025 slab change).
        var w = await TaxWorld.CreateAsync("IN", today: new DateOnly(2025, 9, 22));
        var p = await NewProfileAsync(w);
        await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2017, 7, 1), await w.GstSetAsync(12m)), null);
        await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(new DateOnly(2025, 9, 22), await w.GstSetAsync(5m)), null);

        var before = await w.Resolver.ResolveVersionAsync(p.Id, new DateOnly(2025, 9, 21));
        var on = await w.Resolver.ResolveVersionAsync(p.Id, new DateOnly(2025, 9, 22));

        Assert.Equal(12m, before.Version.TotalPercent);
        Assert.Equal(5m, on.Version.TotalPercent);
        Assert.Equal(new DateOnly(2025, 9, 21), before.Version.EffectiveTo);
        Assert.Equal(5m, (await w.MirrorAsync(p)).RatePercent);
    }

    [Fact]
    public async Task AddVersion_NeverLeavesMoreThanOneOpenVersion()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var p = await NewProfileAsync(w);
        foreach (var (date, rate) in new[] { (new DateOnly(2024, 1, 1), 5m), (new DateOnly(2025, 1, 1), 12m), (new DateOnly(2026, 1, 1), 18m) })
        {
            await w.Profiles.AddVersionAsync(p.Id, TaxWorld.Version(date, await w.GstSetAsync(rate)), null);
        }

        var versions = await w.Db.TaxProfileVersions.Where(v => v.TaxProfileId == p.Id).OrderBy(v => v.EffectiveFrom).ToListAsync();
        Assert.Equal(1, versions.Count(v => v.EffectiveTo == null));
        Assert.Equal([new DateOnly(2024, 12, 31), new DateOnly(2025, 12, 31)], versions.Take(2).Select(v => v.EffectiveTo!.Value).ToArray());
    }

    // ------------------------------------------------------------------ set active

    [Fact]
    public async Task SetActive_BlockedWhileAssigned_ThenAllowedAndTheMirrorFollows()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var gst5 = await w.ProfileAsync("GST-5");
        var gst18 = await w.ProfileAsync("GST-18"); // the company default

        Assert.Equal("isActive", await TaxWorld.FieldOfAsync(() => w.Profiles.SetActiveAsync(gst18.Id, false)));
        Assert.True((await w.Db.TaxProfiles.SingleAsync(p => p.Id == gst18.Id)).IsActive);

        var off = await w.Profiles.SetActiveAsync(gst5.Id, false); // not assigned
        Assert.False(off!.IsActive);
        Assert.False((await w.MirrorAsync(gst5)).IsActive);
        var on = await w.Profiles.SetActiveAsync(gst5.Id, true);
        Assert.True(on!.IsActive);
        Assert.True((await w.MirrorAsync(gst5)).IsActive);
        Assert.Null(await w.Profiles.SetActiveAsync(Guid.NewGuid(), false));
    }

    [Fact]
    public async Task SetActive_ProfileAssignedOnlyAsPurchaseProfile_IsAlsoBlocked()
    {
        var w = await TaxWorld.CreateAsync("IN");
        var gst5 = await w.ProfileAsync("GST-5");
        var category = await w.AddCategoryAsync("GR", "Grains");
        await w.Assignments.UpsertAsync(new(TaxScope.Category, category.Id, w.India.Id, (await w.ProfileAsync("GST-18")).Id, gst5.Id));

        Assert.Equal("isActive", await TaxWorld.FieldOfAsync(() => w.Profiles.SetActiveAsync(gst5.Id, false)));
    }

    // ------------------------------------------------------------------ seed standard

    [Fact]
    public async Task SeedStandard_SeedsTheCompanyCountryOnce_AndIsIdempotent()
    {
        var w = await TaxWorld.CreateAsync("AE", seed: false);

        var first = await w.Profiles.SeedStandardAsync();
        var again = await w.Profiles.SeedStandardAsync();

        Assert.Equal(3, first.ProfilesAdded);
        Assert.True(first.CompanyDefaultSet);
        Assert.Equal(0, again.ProfilesAdded);
        Assert.Equal(3, again.ProfilesSkipped);
        Assert.Equal(3, await w.Db.TaxProfiles.CountAsync());
        Assert.Single(await w.Db.TaxAssignments.ToListAsync());
    }

    [Fact]
    public async Task SeedStandard_SetsTheMirrorOfProductsThatInheritTheNewDefault()
    {
        var w = await TaxWorld.CreateAsync("IN", seed: false);
        var legacy = new Tax { Id = Guid.NewGuid(), Name = "Old", RatePercent = 3m };
        w.Db.Taxes.Add(legacy);
        await w.Db.SaveChangesAsync();
        var product = await w.AddProductAsync("P1", taxId: legacy.Id);

        await w.Profiles.SeedStandardAsync();

        Assert.Equal((await w.ProfileAsync("GST-18")).LegacyTaxId, (await w.Db.Products.SingleAsync(p => p.Id == product.Id)).TaxId);
    }

    [Fact]
    public async Task SeedStandard_CompanyWithoutCountry_Refused()
    {
        var w = await TaxWorld.CreateAsync("IN", seed: false);
        w.Settings.CountryId = null;
        await w.Db.SaveChangesAsync();

        Assert.Equal("taxProfile", await TaxWorld.FieldOfAsync(() => w.Profiles.SeedStandardAsync()));
    }

    [Fact]
    public async Task SeedStandard_CountryWithoutAStandardSet_AddsNothing()
    {
        var w = await TaxWorld.CreateAsync("QA", seed: false);

        var result = await w.Profiles.SeedStandardAsync();

        Assert.False(result.CountrySupported);
        Assert.Empty(await w.Db.TaxProfiles.ToListAsync());
    }
}
