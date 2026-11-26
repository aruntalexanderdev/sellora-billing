using Sellora.Domain.Entities;

namespace Sellora.Domain.Taxes;

// Inputs and results of the tax services (docs/features/tax-profiles.md, TP-1 design). Field names in validation errors are the request
// names (camelCase): code, name, countryId, regime, treatment, method, kind, effectiveFrom, components, components[i].ratePercent, ...

public sealed record UpsertTaxComponentInput(Guid CountryId, string Code, string Name, TaxComponentKind Kind, TaxCalculationMethod Method);

/// <summary>Country is fixed after creation (profiles and versions are checked against it, BR10).</summary>
public sealed record UpdateTaxComponentInput(string Code, string Name, TaxComponentKind Kind, TaxCalculationMethod Method);

/// <summary>One component line of a version. Percent components carry <see cref="RatePercent"/>, per-unit components <see cref="AmountPerBaseUnit"/>.</summary>
public sealed record TaxVersionComponentInput(Guid TaxComponentId, TaxApplicability Applicability, decimal? RatePercent, decimal? AmountPerBaseUnit);

/// <summary>A new version (BR3, BR4). <see cref="EffectiveFrom"/> may be in the future since TP-4 (T19 lifted): documents resolve the version valid on their date.</summary>
public sealed record AddTaxVersionInput(DateOnly EffectiveFrom, IReadOnlyList<TaxVersionComponentInput> Components);

/// <summary>A profile for the company. <paramref name="InitialVersion"/> creates the first version in the same transaction (a profile with no version cannot be resolved).</summary>
public sealed record CreateTaxProfileInput(Guid CountryId, string Code, string Name, TaxRegime Regime, TaxTreatment Treatment, AddTaxVersionInput? InitialVersion = null);

/// <summary>Only code and name can change; country, regime and treatment are fixed, rates change through versions.</summary>
public sealed record UpdateTaxProfileInput(string Code, string Name);

/// <summary>Sets the profiles of a scope. <paramref name="ScopeId"/> is null for <see cref="TaxScope.Company"/>, else the category, subcategory or product id.
/// A <paramref name="PurchaseProfileId"/> equal to the sales profile is stored as NULL (BR7).</summary>
public sealed record UpsertTaxAssignmentInput(TaxScope Scope, Guid? ScopeId, Guid CountryId, Guid SalesProfileId, Guid? PurchaseProfileId);

/// <summary>What a standard seed did. <see cref="CountrySupported"/> false: no standard set exists for the country (nothing added).</summary>
public sealed record TaxSeedResult(bool CountrySupported, int ComponentsAdded, int ProfilesAdded, int ProfilesSkipped, bool CompanyDefaultSet)
{
    public static TaxSeedResult None { get; } = new(false, 0, 0, 0, false);
}

/// <summary>Where the HSN/SAC code of a product comes from (BR9).</summary>
public enum TaxHsnSource
{
    None,
    Product,
    Subcategory,
}

public sealed record ResolvedTaxProfile(
    Guid Id, string Code, string Name, TaxRegime Regime, TaxTreatment Treatment, bool IsActive, Guid CountryId, Guid MirrorTaxId);

/// <summary>The tax setup of a product for one country (BR8). <see cref="Source"/> is the level the assignment was found at and
/// <see cref="SourceName"/> its target ("Soft Drinks", the product name, "Company default"). <see cref="HsnWarning"/> is the T11 warning:
/// a GST taxable product without an HSN/SAC code (the save and the sale are still allowed).</summary>
public sealed record TaxResolution(
    Guid? ProductId, Guid CountryId,
    ResolvedTaxProfile SalesProfile, ResolvedTaxProfile PurchaseProfile, bool PurchaseSameAsSales,
    TaxScope Source, string SourceName, Guid? AssignmentId,
    string? HsnCode, TaxHsnSource HsnSource, bool HsnWarning, string? HsnWarningMessage);

/// <summary>Input of <see cref="Abstractions.ITaxResolver.PreviewAsync"/>: a product as it would be saved. With override ids the product level is
/// taken from them (a purchase override needs a sales override); without them the stored product assignment (when <see cref="ProductId"/> is set) applies.</summary>
public sealed record TaxPreviewInput(
    Guid CountryId, Guid? ProductId, string? ProductName, Guid? CategoryId, Guid? SubcategoryId, string? HsnCode,
    Guid? SalesOverrideProfileId = null, Guid? PurchaseOverrideProfileId = null);

/// <summary>The version of a profile valid on a document date (BR5), with its component lines.</summary>
public sealed record TaxVersionResolution(TaxProfile Profile, TaxProfileVersion Version);

/// <summary>A product's sales profile for display (F7): the profile id and name, and the total percent of the version valid on the requested date (null when none is valid).</summary>
public sealed record CurrentSalesTax(Guid ProfileId, string ProfileName, decimal? Percent);
