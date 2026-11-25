using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Taxes;
using Sellora.Infrastructure.Data;
using Sellora.Infrastructure.Services;

namespace Sellora.Api.Controllers;

/// <summary>Tax profiles and their dated versions (BR2–BR6). Reads need <c>Tax.View</c>, writes <c>Tax.Manage</c>.</summary>
[ApiController]
[Route("api/v1/tax-profiles")]
[Authorize(Policy = "Tax.View")]
public class TaxProfilesController(SelloraDbContext db, ITaxProfileService service, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TaxProfileDto>>> List([FromQuery] Guid? countryId, [FromQuery] bool? active, CancellationToken ct)
    {
        var query = db.TaxProfiles.AsNoTracking().Include(p => p.Versions).ThenInclude(v => v.Components).ThenInclude(c => c.TaxComponent).AsQueryable();
        if (countryId is not null) query = query.Where(p => p.CountryId == countryId);
        if (active is not null) query = query.Where(p => p.IsActive == active);

        var profiles = await query.ToListAsync(ct);
        var countries = await db.Countries.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var assigned = await AssignedProfileIdsAsync(ct);
        var today = Today(time);

        var items = new List<TaxProfileDto>();
        foreach (var p in profiles.OrderBy(p => countries[p.CountryId].Code, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var current = p.Versions.FirstOrDefault(v => IsCurrent(v, today));
            items.Add(ToDto(p, countries[p.CountryId], assigned.Contains(p.Id), p.Versions.Count, current is null ? null : await ToVersionDtoAsync(p, current, today, ct)));
        }
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TaxProfileDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var detail = await LoadDetailAsync(id, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpPost]
    [Authorize(Policy = "Tax.Manage")]
    public async Task<ActionResult<TaxProfileDetailDto>> Create(CreateTaxProfileRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var profile = await service.CreateAsync(new CreateTaxProfileInput(request.CountryId!.Value, request.Code, request.Name, request.Regime!.Value, request.Treatment!.Value,
                request.InitialVersion is null ? null : ToInput(request.InitialVersion)), CurrentUserId(), ct);
            return CreatedAtAction(nameof(Get), new { id = profile.Id }, await LoadDetailAsync(profile.Id, ct));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Tax.Manage")]
    public async Task<ActionResult<TaxProfileDetailDto>> Update(Guid id, UpdateTaxProfileRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var profile = await service.UpdateAsync(id, new UpdateTaxProfileInput(request.Code, request.Name), ct);
            return profile is null ? NotFound() : Ok(await LoadDetailAsync(id, ct));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    /// <summary>Adds a version (BR3, BR4). 400 on <c>effectiveFrom</c> (earlier than the latest version, used version, after today) and on <c>components[i].*</c> / <c>components</c>.</summary>
    [HttpPost("{id:guid}/versions")]
    [Authorize(Policy = "Tax.Manage")]
    public async Task<ActionResult<TaxVersionDto>> AddVersion(Guid id, AddTaxVersionRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var version = await service.AddVersionAsync(id, ToInput(request), CurrentUserId(), ct);
            if (version is null) return NotFound();
            var profile = await db.TaxProfiles.AsNoTracking().SingleAsync(p => p.Id == id, ct);
            return Ok(await ToVersionDtoAsync(profile, version, Today(time), ct));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "Tax.Manage")]
    public async Task<ActionResult<TaxProfileDetailDto>> SetActive(Guid id, SetActiveRequest request, CancellationToken ct)
    {
        try
        {
            var profile = await service.SetActiveAsync(id, request.IsActive, ct);
            return profile is null ? NotFound() : Ok(await LoadDetailAsync(id, ct));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    /// <summary>Seeds the standard components and profiles of the company's country (T12); idempotent. 400 <c>taxProfile</c> without a company country.</summary>
    [HttpPost("seed-standard")]
    [Authorize(Policy = "Tax.Manage")]
    public async Task<ActionResult<TaxSeedResultDto>> SeedStandard(CancellationToken ct)
    {
        try
        {
            var r = await service.SeedStandardAsync(ct);
            return Ok(new TaxSeedResultDto(r.CountrySupported, r.ComponentsAdded, r.ProfilesAdded, r.ProfilesSkipped, r.CompanyDefaultSet));
        }
        catch (DomainValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    // ------------------------------------------------------------------ mapping

    private static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private static bool IsCurrent(TaxProfileVersion v, DateOnly today) => v.EffectiveFrom <= today && (v.EffectiveTo is null || v.EffectiveTo >= today);

    private static AddTaxVersionInput ToInput(AddTaxVersionRequest r) =>
        new(r.EffectiveFrom!.Value, r.Components.Select(c => new TaxVersionComponentInput(c.TaxComponentId!.Value, c.Applicability!.Value, c.RatePercent, c.AmountPerBaseUnit)).ToList());

    private async Task<HashSet<Guid>> AssignedProfileIdsAsync(CancellationToken ct)
    {
        var sales = await db.TaxAssignments.AsNoTracking().Select(a => a.SalesTaxProfileId).Distinct().ToListAsync(ct);
        var purchase = await db.TaxAssignments.AsNoTracking().Where(a => a.PurchaseTaxProfileId != null).Select(a => a.PurchaseTaxProfileId!.Value).Distinct().ToListAsync(ct);
        return sales.Concat(purchase).ToHashSet();
    }

    private async Task<TaxVersionDto> ToVersionDtoAsync(TaxProfile profile, TaxProfileVersion v, DateOnly today, CancellationToken ct)
    {
        var components = v.Components.OrderBy(c => c.SortOrder).ToList();
        if (components.Any(c => c.TaxComponent is null))
        {
            components = await db.TaxProfileVersionComponents.AsNoTracking().Include(c => c.TaxComponent).Where(c => c.TaxProfileVersionId == v.Id).OrderBy(c => c.SortOrder).ToListAsync(ct);
        }
        var used = await TaxVersionUsage.IsUsedAsync(db, profile.Id, profile.LegacyTaxId, v.EffectiveFrom, v.EffectiveTo, ct);
        return new TaxVersionDto(v.Id, v.EffectiveFrom, v.EffectiveTo, v.TotalPercent, IsCurrent(v, today), used, v.CreatedAt,
            components.Select(c => new TaxVersionComponentDto(c.Id, c.TaxComponentId, c.TaxComponent.Code, c.TaxComponent.Name, c.TaxComponent.Kind, c.Method,
                c.Applicability, c.RatePercent, c.AmountPerBaseUnit, c.SortOrder)).ToList());
    }

    private static TaxProfileDto ToDto(TaxProfile p, Country country, bool assigned, int versionCount, TaxVersionDto? current) =>
        new(p.Id, p.CountryId, country.Code, country.Name, p.Code, p.Name, p.Regime, p.Treatment, p.IsActive, p.LegacyTaxId, assigned, versionCount, current);

    private async Task<TaxProfileDetailDto?> LoadDetailAsync(Guid id, CancellationToken ct)
    {
        var p = await db.TaxProfiles.AsNoTracking().Include(x => x.Versions).ThenInclude(v => v.Components).ThenInclude(c => c.TaxComponent).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;

        var country = await db.Countries.AsNoTracking().SingleAsync(c => c.Id == p.CountryId, ct);
        var assigned = await db.TaxAssignments.AsNoTracking().AnyAsync(a => a.SalesTaxProfileId == id || a.PurchaseTaxProfileId == id, ct);
        var today = Today(time);

        var versions = new List<TaxVersionDto>();
        foreach (var v in p.Versions.OrderByDescending(v => v.EffectiveFrom)) versions.Add(await ToVersionDtoAsync(p, v, today, ct));
        var current = versions.FirstOrDefault(v => v.IsCurrent);
        return new TaxProfileDetailDto(p.Id, p.CountryId, country.Code, country.Name, p.Code, p.Name, p.Regime, p.Treatment, p.IsActive, p.LegacyTaxId, assigned,
            versions.Count, current, versions);
    }

    private Guid? CurrentUserId() =>
        Guid.TryParse(User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? User?.FindFirstValue("sub"), out var id) ? id : null;

    private ActionResult ToValidationProblem(DomainValidationException ex)
    {
        foreach (var error in ex.Errors) ModelState.AddModelError(error.Field, error.Message);
        return ValidationProblem(ModelState);
    }
}
