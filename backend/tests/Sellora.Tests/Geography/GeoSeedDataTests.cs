using System.Security.Cryptography;
using System.Text;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Data.Migrations;

namespace Sellora.Tests.Geography;

public class GeoSeedDataTests
{
    [Fact]
    public void Seed_has_the_designed_shape()
    {
        Assert.Equal(7, GeoSeedData.Countries.Count);
        Assert.Equal(85, GeoSeedData.States.Count);
        Assert.Equal(36, GeoSeedData.States.Count(s => s.CountryCode == "IN"));
        Assert.Equal(36, GeoSeedData.States.Count(s => s.GstStateCode != null));
        Assert.Equal(8, GeoSeedData.States.Count(s => s.Kind == StateKind.UnionTerritory));
        Assert.Equal(GeoSeedData.States.Count, GeoSeedData.States.Select(s => (s.CountryCode, s.Code)).Distinct().Count());
        Assert.Equal(GeoSeedData.States.Count, GeoSeedData.States.Select(s => GeoSeedData.StateId(s.CountryCode, s.Code)).Distinct().Count());
    }

    [Fact]
    public void Aliases_are_unique_after_normalisation_and_never_collide_with_names_or_codes()
    {
        var states = GeoSeedData.States.ToDictionary(s => (s.CountryCode, s.Code));
        var taken = new HashSet<string?>(GeoSeedData.States.Select(s => GeoSeedData.Normalize(s.Name)));
        foreach (var s in GeoSeedData.States.Where(s => s.CountryCode == "IN")) taken.Add(GeoSeedData.Normalize(s.Code));
        var seen = new HashSet<string>();
        foreach (var a in GeoSeedData.Aliases)
        {
            Assert.True(states.ContainsKey((a.CountryCode, a.StateCode)), $"alias target {a.CountryCode}-{a.StateCode}");
            var n = GeoSeedData.Normalize(a.Alias);
            Assert.NotNull(n);
            Assert.True(seen.Add(n!), $"duplicate normalised alias '{n}'");
            Assert.DoesNotContain(n, taken);
        }
    }

    [Theory]
    [InlineData("Ras Al-Khaimah", "ras al khaimah")]
    [InlineData("32-Kerala", "32 kerala")]
    [InlineData("J&K", "j and k")]
    [InlineData("Ha'il", "hail")]
    [InlineData("Kerala\u00A0\u3000\uFEFFState", "kerala state")]
    [InlineData("  \t ", null)]
    public void Normalize_matches_the_documented_rules(string input, string? expected)
        => Assert.Equal(expected, GeoSeedData.Normalize(input));

    [Fact]
    public void Blank_means_whitespace_only_and_punctuation_is_not_blank()
    {
        Assert.True(GeoSeedData.IsBlank("\t\u00A0\u3000"));
        Assert.False(GeoSeedData.IsBlank("-"));
    }

    // Frozen embedded migration SQL: an edit to an applied migration's SQL must fail CI (add a new migration instead).
    [Theory]
    [InlineData("20261119103652_AddCountryStateMasters.seed.sql", "7456e0aa18a7507721369234129c9b3d3a0b5d80f163f2efcb0674b331d0ca58")]
    [InlineData("20261119103652_AddCountryStateMasters.backfill.sql", "4937990c2bf89272153c36582a0321ac4073c892d8245439acd070aad8d0e8ad")]
    public void Embedded_migration_sql_is_frozen_and_lf_only(string resource, string sha256)
    {
        var text = MigrationSql.Read(resource);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain("$EF$", text);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant());
    }
}
