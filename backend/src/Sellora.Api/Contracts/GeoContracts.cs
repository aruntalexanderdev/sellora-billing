using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Data;

namespace Sellora.Api.Contracts;

public record CountryDto(Guid Id, string Code, string? Iso3Code, string Name, bool IsActive, bool IsSystem);

/// <summary>Company list: <see cref="CanEdit"/> = the platform lets companies edit reference data (never derived from permissions).</summary>
public record CountryListDto(bool CanEdit, IReadOnlyList<CountryDto> Items);

public record CountryLookupDto(Guid Id, string Code, string Name);

public record StateDto(
    Guid Id, Guid CountryId, string CountryCode, string CountryName, string Code, string Name, StateKind Kind,
    string? GstStateCode, bool IsActive, bool IsSystem);

public record StateListDto(bool CanEdit, IReadOnlyList<StateDto> Items);

public record StateLookupDto(Guid Id, Guid CountryId, string Code, string Name, string? GstStateCode);

public record CreateCountryRequest(
    [Required, MaxLength(2), RegularExpression("^[A-Za-z]{2}$")] string Code,
    [MaxLength(3), RegularExpression("^[A-Za-z]{3}$")] string? Iso3Code,
    [Required, MaxLength(100)] string Name,
    bool IsActive = true);

/// <summary>Activation goes through the status endpoint.</summary>
public record UpdateCountryRequest(
    [Required, MaxLength(2), RegularExpression("^[A-Za-z]{2}$")] string Code,
    [MaxLength(3), RegularExpression("^[A-Za-z]{3}$")] string? Iso3Code,
    [Required, MaxLength(100)] string Name);

public record CreateStateRequest(
    [Required] Guid? CountryId,
    [Required, MaxLength(3), RegularExpression("^[A-Za-z0-9]{1,3}$")] string Code,
    [Required, MaxLength(100)] string Name,
    [Required] StateKind? Kind,
    [MaxLength(2), RegularExpression("^[0-9]{2}$")] string? GstStateCode,
    bool IsActive = true);

/// <summary>No CountryId: a state never moves between countries.</summary>
public record UpdateStateRequest(
    [Required, MaxLength(3), RegularExpression("^[A-Za-z0-9]{1,3}$")] string Code,
    [Required, MaxLength(100)] string Name,
    [Required] StateKind? Kind,
    [MaxLength(2), RegularExpression("^[0-9]{2}$")] string? GstStateCode);

public static class GeoMapping
{
    public static CountryDto ToDto(Country c) => new(c.Id, c.Code, c.Iso3Code, c.Name, c.IsActive, c.IsSystem);

    public static StateDto ToDto(State s, Country c) => new(s.Id, s.CountryId, c.Code, c.Name, s.Code, s.Name, s.Kind, s.GstStateCode, s.IsActive, s.IsSystem);
}

/// <summary>Names of the countries/states an address-owner list refers to (global tables, no company filter), resolved with two queries.</summary>
public sealed class GeoNames
{
    private readonly Dictionary<Guid, string> _countries;
    private readonly Dictionary<Guid, string> _states;

    private GeoNames(Dictionary<Guid, string> countries, Dictionary<Guid, string> states) { _countries = countries; _states = states; }

    public string? Country(Guid? id) => id is { } g && _countries.TryGetValue(g, out var n) ? n : null;
    public string? State(Guid? id) => id is { } g && _states.TryGetValue(g, out var n) ? n : null;

    public static async Task<GeoNames> LoadAsync(SelloraDbContext db, IEnumerable<Guid?> countryIds, IEnumerable<Guid?> stateIds, CancellationToken ct)
    {
        var cIds = countryIds.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        var sIds = stateIds.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        var countries = cIds.Count == 0 ? [] : await db.Countries.AsNoTracking().Where(c => cIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var states = sIds.Count == 0 ? [] : await db.States.AsNoTracking().Where(s => sIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return new GeoNames(countries, states);
    }
}
