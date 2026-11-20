using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Tests.Geography;

/// <summary>Country/State seed catalogue and normaliser (docs/features/country-state-masters.md Appendix A/B, sections 2 and 7).</summary>
public class GeoSeedCatalogTests
{
    private static string Md5Hex(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Theory]
    [InlineData("IN", 36)]
    [InlineData("AE", 7)]
    [InlineData("SA", 13)]
    [InlineData("QA", 8)]
    [InlineData("KW", 6)]
    [InlineData("BH", 4)]
    [InlineData("OM", 11)]
    public void States_PerCountry_MatchesAppendixA(string country, int expected)
        => Assert.Equal(expected, GeoSeedData.States.Count(s => s.CountryCode == country));

    [Fact]
    public void States_India_Has28StatesAnd8UnionTerritories()
    {
        var india = GeoSeedData.States.Where(s => s.CountryCode == "IN").ToList();
        Assert.Equal(8, india.Count(s => s.Kind == StateKind.UnionTerritory));
        Assert.Equal(28, india.Count(s => s.Kind == StateKind.State));
    }

    [Fact]
    public void States_EveryStateBelongsToASeededCountry_AndEveryCountryHasStates()
    {
        var codes = GeoSeedData.Countries.Select(c => c.Code).ToHashSet();
        Assert.All(GeoSeedData.States, s => Assert.Contains(s.CountryCode, codes));
        Assert.All(codes, c => Assert.Contains(GeoSeedData.States, s => s.CountryCode == c));
    }

    [Fact]
    public void Codes_AreWellFormed_AndUniqueWithinTheirScope()
    {
        Assert.All(GeoSeedData.Countries, c => Assert.Matches("^[A-Z]{2}$", c.Code));
        Assert.All(GeoSeedData.Countries, c => Assert.Matches("^[A-Z]{3}$", c.Iso3Code));
        Assert.All(GeoSeedData.States, s => Assert.Matches("^[A-Z0-9]{1,3}$", s.Code));
        Assert.Equal(GeoSeedData.Countries.Count, GeoSeedData.Countries.Select(c => c.Code).Distinct().Count());
        Assert.Equal(GeoSeedData.Countries.Count, GeoSeedData.Countries.Select(c => c.Iso3Code).Distinct().Count());
    }

    [Fact]
    public void Names_AreUniqueAfterNormalisation_GloballyForCountriesAndPerCountryForStates()
    {
        Assert.Equal(GeoSeedData.Countries.Count, GeoSeedData.Countries.Select(c => GeoSeedData.Normalize(c.Name)).Distinct().Count());
        foreach (var group in GeoSeedData.States.GroupBy(s => s.CountryCode))
            Assert.Equal(group.Count(), group.Select(s => GeoSeedData.Normalize(s.Name)).Distinct().Count());
        Assert.All(GeoSeedData.States, s => Assert.True(s.Name.Length is > 0 and <= 100));
    }

    [Fact]
    public void GstStateCodes_ExistOnlyForIndia_AreTwoDigitsAndUnique()
    {
        var withGst = GeoSeedData.States.Where(s => s.GstStateCode is not null).ToList();
        Assert.All(withGst, s => Assert.Equal("IN", s.CountryCode));
        Assert.All(withGst, s => Assert.Matches("^[0-9]{2}$", s.GstStateCode!));
        Assert.Equal(withGst.Count, withGst.Select(s => s.GstStateCode).Distinct().Count());
        Assert.Equal(36, withGst.Count);
        // every Indian state/UT carries one
        Assert.All(GeoSeedData.States.Where(s => s.CountryCode == "IN"), s => Assert.NotNull(s.GstStateCode));
    }

    [Theory]
    [InlineData("KL", "32")]
    [InlineData("TN", "33")]
    [InlineData("DL", "07")]
    [InlineData("DH", "26")]
    [InlineData("TS", "36")]
    public void GstStateCodes_KnownIndianStates_AreCorrect(string state, string gst)
        => Assert.Equal(gst, GeoSeedData.States.Single(s => s.CountryCode == "IN" && s.Code == state).GstStateCode);

    [Fact]
    public void Kinds_FollowTheCountry()
    {
        var expected = new Dictionary<string, StateKind[]>
        {
            ["AE"] = [StateKind.Emirate], ["QA"] = [StateKind.Municipality], ["SA"] = [StateKind.Region],
            ["KW"] = [StateKind.Governorate], ["BH"] = [StateKind.Governorate], ["OM"] = [StateKind.Governorate],
            ["IN"] = [StateKind.State, StateKind.UnionTerritory],
        };
        Assert.All(GeoSeedData.States, s => Assert.Contains(s.Kind, expected[s.CountryCode]));
    }

    [Fact]
    public void Ids_AreDeterministicMd5OfTheDocumentedKeys()
    {
        foreach (var c in GeoSeedData.Countries)
            Assert.Equal(Md5Hex("sellora:country:" + c.Code), GeoSeedData.CountryId(c.Code).ToString("N"));
        foreach (var s in GeoSeedData.States)
            Assert.Equal(Md5Hex("sellora:state:" + s.CountryCode + "-" + s.Code), GeoSeedData.StateId(s.CountryCode, s.Code).ToString("N"));
        // literal pins (md5 computed independently): a change of the id scheme would orphan every linked row
        Assert.Equal(Guid.Parse("7dbf9a52ae6255bd978ae4567d182dfc"), GeoSeedData.CountryId("IN"));
        Assert.Equal(Guid.Parse("a9004a1582565f6d3f67eb193790641d"), GeoSeedData.StateId("IN", "KL"));
    }

    [Fact]
    public void Ids_AreUniqueAcrossCountriesAndStates()
    {
        var ids = GeoSeedData.Countries.Select(c => GeoSeedData.CountryId(c.Code))
            .Concat(GeoSeedData.States.Select(s => GeoSeedData.StateId(s.CountryCode, s.Code))).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Aliases_NeverCollideWithAnotherStatesNameOrCode_InAnyCountry()
    {
        var names = GeoSeedData.States.GroupBy(s => GeoSeedData.Normalize(s.Name)!).ToDictionary(g => g.Key, g => g.Select(s => (s.CountryCode, s.Code)).ToList());
        foreach (var a in GeoSeedData.Aliases)
        {
            var n = GeoSeedData.Normalize(a.Alias)!;
            // an alias equal to some state's name would make the candidate set ambiguous or hijack it
            Assert.False(names.ContainsKey(n), $"alias '{a.Alias}' equals the name of {string.Join(",", names.GetValueOrDefault(n) ?? [])}");
        }
    }

    [Fact]
    public void Aliases_OnlyIndiaOnlyOnesAreAbbreviationsOfIndianStates_AndAllResolveToSeededStates()
    {
        var states = GeoSeedData.States.Select(s => (s.CountryCode, s.Code)).ToHashSet();
        Assert.All(GeoSeedData.Aliases, a => Assert.Contains((a.CountryCode, a.StateCode), states));
        Assert.All(GeoSeedData.Aliases.Where(a => a.IndiaOnly), a => Assert.Equal("IN", a.CountryCode));
        Assert.NotEmpty(GeoSeedData.Aliases);
    }

    [Fact]
    public void Aliases_DoNotMapTheDenyListedWords()
    {
        string[] deny = ["capital", "capital governorate", "al asimah", "asimah", "northern", "southern", "eastern", "western", "central",
            "sharqiyah", "ash sharqiyah", "batinah", "al batinah", "ncr", "delhi ncr", "ut", "or"];
        Assert.All(GeoSeedData.Aliases, a => Assert.DoesNotContain(GeoSeedData.Normalize(a.Alias)!, deny));
    }

    [Theory]
    [InlineData("Jammu-Kashmir", "jammu kashmir")]
    [InlineData("Jammu_Kashmir", "jammu kashmir")]
    [InlineData("Jammu/Kashmir", "jammu kashmir")]
    [InlineData("Jammu,Kashmir", "jammu kashmir")]
    [InlineData("Andaman & Nicobar", "andaman and nicobar")]
    [InlineData("A&N", "a and n")]
    [InlineData("St. Mary's", "st marys")]
    [InlineData("Ha\u2019il", "hail")]
    [InlineData("Ha\u2018il", "hail")]
    [InlineData("Ha`il", "hail")]
    [InlineData("KERALA ", "kerala")]
    [InlineData("32-Kerala", "32 kerala")]
    [InlineData("Kerala (32)", "kerala (32)")]
    [InlineData("-", null)]
    [InlineData(".", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_DesignCases_ProduceExpectedForm(string? input, string? expected)
    {
        // "-" and "." normalise to a space / nothing and so to NULL, which is why IsBlank (not Normalize) defines "blank" for D12.
        Assert.Equal(expected, GeoSeedData.Normalize(input));
    }

    [Theory]
    [InlineData('\u0085')]
    [InlineData('\u00A0')]
    [InlineData('\u1680')]
    [InlineData('\u2003')]
    [InlineData('\u2007')]
    [InlineData('\u202F')]
    [InlineData('\u3000')]
    [InlineData('\uFEFF')]
    [InlineData('\t')]
    [InlineData('\u000B')]
    [InlineData('\u2028')]
    public void Normalize_WhitespaceClass_CollapsesEveryMemberToASingleSpace(char ws)
    {
        Assert.Equal("kerala state", GeoSeedData.Normalize($"Kerala{ws}{ws}State"));
        Assert.Equal("kerala", GeoSeedData.Normalize($"{ws}Kerala{ws}"));
        Assert.Null(GeoSeedData.Normalize(ws.ToString()));
        Assert.True(GeoSeedData.IsBlank(ws.ToString()));
    }

    [Theory]
    [InlineData('\u200B')] // zero-width space is NOT in the class
    [InlineData('\u2060')]
    public void Normalize_CharactersOutsideTheClass_AreNotTreatedAsWhitespace(char c)
        => Assert.False(GeoSeedData.IsBlank(c.ToString()));

    [Fact]
    public void Normalize_IsIdempotent_ForEverySeedNameAndAlias()
    {
        foreach (var text in GeoSeedData.States.Select(s => s.Name).Concat(GeoSeedData.Aliases.Select(a => a.Alias)))
        {
            var once = GeoSeedData.Normalize(text);
            Assert.Equal(once, GeoSeedData.Normalize(once));
        }
    }

    [Theory]
    [InlineData("33AAAAA0000A1Z5", true)]
    [InlineData("33aaaaa0000a1z5", false)] // the regex is applied after upper(btrim()); lower-case alone does not match
    [InlineData("33AAAAA0000A1Z", false)]              // 14 characters
    [InlineData("33AAAAA0000A1X5", false)]             // Z missing at position 14
    [InlineData("33123456789012Z", false)]             // no PAN letters
    [InlineData("100234567800003", false)]             // UAE TRN
    [InlineData("300123456700003", false)]             // Saudi VAT
    [InlineData("200000898300002", false)]             // Bahrain VAT
    [InlineData("33AAAAA0000A0Z5", false)]             // entity digit must be 1-9 or letter
    public void GstinFormat_IsTheStrictD11Pattern(string gstin, bool valid)
        => Assert.Equal(valid, GeoSeedData.GstinFormat.IsMatch(gstin));
}
