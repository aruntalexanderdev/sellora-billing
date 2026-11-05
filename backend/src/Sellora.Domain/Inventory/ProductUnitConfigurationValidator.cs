using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;

namespace Sellora.Domain.Inventory;

/// <summary>The Unit Master attributes the hierarchy rules need.</summary>
public sealed record UnitInfo(Guid Id, string Code, string Name, MeasurementCategory Category, short DecimalPlaces, decimal? StandardFactor, bool IsActive)
{
    public static UnitInfo From(Unit unit) =>
        new(unit.Id, unit.ShortCode, unit.Name, unit.MeasurementCategory, unit.DecimalPlaces, unit.StandardFactor, unit.IsActive);
}

/// <summary>
/// One row of a product's unit hierarchy as it would be saved: a submitted row (with its position
/// <see cref="Index"/>), or a stored row that was omitted but has to be kept inactive (<see cref="Index"/> = -1).
/// Built by the product service from the request plus the stored rows; the validator itself is pure.
/// </summary>
public sealed record ProductUnitDraft
{
    /// <summary>Position in the submitted list; -1 for a stored row kept (inactive) although not submitted.</summary>
    public required int Index { get; init; }

    /// <summary>The row id: the stored id, or a new id for a new row.</summary>
    public required Guid Id { get; init; }

    /// <summary>The submitted unit; null when <see cref="UnitId"/> is not a known unit.</summary>
    public UnitInfo? Unit { get; init; }
    public required Guid UnitId { get; init; }

    /// <summary>UnitId of the contained unit as submitted; null marks the base row.</summary>
    public Guid? InnerUnitId { get; init; }

    /// <summary>The draft <see cref="Id"/> the inner resolved to; null if it could not be resolved.</summary>
    public Guid? InnerRef { get; init; }

    /// <summary>Set by the draft builder when the inner names a unit that is being removed from the product.</summary>
    public string? InnerResolutionError { get; init; }

    public decimal? InnerQuantity { get; init; }
    public string? Label { get; init; }
    public string? Barcode { get; init; }
    public bool IsPurchasable { get; init; }
    public bool IsSellable { get; init; }
    public bool IsDefaultPurchaseUnit { get; init; }
    public bool IsDefaultSalesUnit { get; init; }
    public bool IsActive { get; init; }

    public bool IsNew { get; init; }

    /// <summary>Why the stored row is immutable (see ProductUnitUsage); null when it is not locked.</summary>
    public string? LockReason { get; init; }
    public bool IsLocked => LockReason is not null;

    public bool StoredIsBase { get; init; }
    public Guid? StoredUnitId { get; init; }
    public Guid? StoredInnerId { get; init; }
    public Guid? StoredInnerUnitId { get; init; }
    public decimal? StoredInnerQuantity { get; init; }
    public decimal? StoredFactor { get; init; }
    public bool StoredIsActive { get; init; }

    public bool IsBaseCandidate => InnerUnitId is null;
    public string DisplayName => Label ?? Unit?.Name ?? "This unit";
}

/// <summary>Product-level facts the rules need.</summary>
/// <param name="BaseUnit">The requested base unit; null when it does not exist.</param>
/// <param name="StoredBaseUnitId">The product's current base unit on update; null on create.</param>
/// <param name="HasStockHistory">Any stock movement or batch exists for the product (only needed when the base changes).</param>
/// <param name="BarcodesUsedElsewhere">Barcodes held by active rows of other products.</param>
public sealed record ProductUnitValidationContext(
    Guid BaseUnitId,
    UnitInfo? BaseUnit,
    Guid? StoredBaseUnitId,
    bool HasStockHistory,
    IReadOnlySet<string> BarcodesUsedElsewhere);

/// <summary>Computed values for one draft.</summary>
public sealed record ProductUnitComputedRow(Guid Id, int Index, decimal? Factor, int? Depth, short SortOrder, string? Chain, bool IsStructuralChange);

public sealed record ProductUnitConfigurationResult(
    IReadOnlyList<ProductUnitComputedRow> Rows,
    IReadOnlyList<FieldError> Errors,
    IReadOnlyList<FieldError> Warnings)
{
    public bool IsValid => Errors.Count == 0;
    public ProductUnitComputedRow Row(Guid id) => Rows.Single(r => r.Id == id);
}

/// <summary>
/// Rules R1–R9 for a product's unit hierarchy (Phase 2 design §5.1). Errors and warnings are keyed by request field:
/// <c>Units[i].Field</c> for the i-th submitted row, <c>Units</c> for the list (or a kept, not-submitted row),
/// <c>BaseUnitId</c> for the product's base unit. Category, standard-factor and whole-number rules apply to rows whose
/// structure changes (new, or unit/inner/quantity/factor changed); on unchanged legacy rows they are warnings, so a
/// grandfathered row never blocks an unrelated edit.
/// </summary>
public static class ProductUnitConfigurationValidator
{
    /// <summary>Most unit rows one product may submit (also enforced on the request contracts).</summary>
    public const int MaxUnits = 50;
    public const string TooManyUnitsMessage = "A product can have at most 50 units.";
    public const int MaxLabelLength = 60;
    public const int MaxBarcodeLength = 50;

    public const string LockedMessage = "This unit is used in transactions or prices; deactivate it and add a new one.";
    public const string InvalidBaseUnitMessage = "Select a valid base unit.";
    public const string PackagingBaseUnitMessage = "A packaging unit cannot be a base unit; choose the unit stock is counted in (e.g. Piece).";
    public const string InactiveBaseUnitMessage = "Select an active base unit.";
    public const string BaseChangeWithHistoryMessage = "The base unit cannot be changed once this product has stock history.";
    public const string BaseChangeWithLockedUnitsMessage = "The base unit cannot be changed once any of this product's units is used in transactions or prices.";

    public static string Key(ProductUnitDraft draft, string field) => draft.Index >= 0 ? $"Units[{draft.Index}].{field}" : "Units";

    public static ProductUnitConfigurationResult Validate(ProductUnitValidationContext context, IReadOnlyList<ProductUnitDraft> drafts)
    {
        var errors = new List<FieldError>();
        var warnings = new List<FieldError>();
        void Error(string field, string message) => errors.Add(new FieldError(field, message));
        void Warn(string field, string message) => warnings.Add(new FieldError(field, message));

        // ---- Product base unit (also checked by the service before it gets here) and R9 ----
        var baseUnit = context.BaseUnit;
        if (baseUnit is null) Error("BaseUnitId", InvalidBaseUnitMessage);
        else if (baseUnit.Category == MeasurementCategory.Packaging) Error("BaseUnitId", PackagingBaseUnitMessage);
        else if (!baseUnit.IsActive && context.BaseUnitId != context.StoredBaseUnitId) Error("BaseUnitId", InactiveBaseUnitMessage);

        if (context.StoredBaseUnitId is { } storedBase && storedBase != context.BaseUnitId)
        {
            if (context.HasStockHistory) Error("BaseUnitId", BaseChangeWithHistoryMessage);
            else if (drafts.Any(d => d.IsLocked)) Error("BaseUnitId", BaseChangeWithLockedUnitsMessage);
        }

        if (drafts.All(d => d.Index < 0)) Error("Units", "Add at least the base unit.");

        // ---- R1: exactly one base row ----
        var submittedBases = drafts.Where(d => d.Index >= 0 && d.IsBaseCandidate).ToList();
        var root = submittedBases.FirstOrDefault() ?? drafts.FirstOrDefault(d => d.IsBaseCandidate);
        if (submittedBases.Count == 0 && drafts.Any(d => d.Index >= 0))
        {
            Error("Units", "Add the base unit row (the unit that contains no other unit).");
        }
        foreach (var extra in submittedBases.Skip(1))
        {
            Error(Key(extra, "InnerUnitId"), "Only the base unit can contain no other unit; choose the unit this one contains.");
        }
        foreach (var d in submittedBases.Take(1))
        {
            if (d.UnitId != context.BaseUnitId) Error(Key(d, "UnitId"), "The base unit row must use the product's base unit.");
            if (!d.IsNew && !d.StoredIsBase) Error(Key(d, "InnerUnitId"), "This unit is not the base unit; choose the unit it contains.");
            if (d.InnerQuantity is { } q && q != 1m) Error(Key(d, "InnerQuantity"), "The base unit always has quantity 1.");
            if (!d.IsActive) Error(Key(d, "IsActive"), "The base unit cannot be deactivated.");
        }
        foreach (var d in drafts.Where(d => d.StoredIsBase && !d.IsBaseCandidate))
        {
            Error(Key(d, "InnerUnitId"), "The base unit cannot contain another unit.");
        }

        // ---- R2: graph (factor, depth, cycles, orphans) ----
        var byId = drafts.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        var nodes = drafts.Select(d => new ProductUnitGraph.Node(
            d.Id,
            d == root ? null : d.IsBaseCandidate ? Guid.Empty : d.InnerRef ?? Guid.Empty,
            d == root ? 1m : d.InnerQuantity ?? 0m)).ToList();
        var graph = ProductUnitGraph.Compute(nodes);

        foreach (var d in drafts.Where(d => d != root && !d.IsBaseCandidate))
        {
            var result = graph[d.Id];
            switch (result.Problem)
            {
                case ProductUnitGraph.Problem.Cycle:
                    Error(Key(d, "InnerUnitId"), d.InnerRef == d.Id || d.InnerUnitId == d.UnitId
                        ? "A unit cannot contain itself."
                        : "These units contain each other in a loop; one of them must contain a smaller unit.");
                    break;
                case ProductUnitGraph.Problem.MissingInner:
                    Error(Key(d, "InnerUnitId"), d.InnerResolutionError ?? "Choose the unit this one contains from this product's units.");
                    break;
                case ProductUnitGraph.Problem.InvalidQuantity:
                    Error(Key(d, "InnerQuantity"), d.InnerQuantity is null
                        ? "Enter how many of the contained unit this unit holds."
                        : "Quantity must be greater than 0 and at most 1,000,000,000, with at most 6 decimal places.");
                    break;
                case ProductUnitGraph.Problem.OutOfRange when result.Factor is null:
                    Error(Key(d, "InnerQuantity"), "This unit would hold more than 1,000,000,000 base units.");
                    break;
            }
            if (d.InnerResolutionError is { } resolution && result.Problem != ProductUnitGraph.Problem.MissingInner && d.IsActive && !d.IsLocked)
            {
                Error(Key(d, "InnerUnitId"), resolution);
            }
        }

        decimal? FactorOf(ProductUnitDraft d) => graph.TryGetValue(d.Id, out var r) ? r.Factor : null;
        var baseDp = baseUnit?.DecimalPlaces ?? root?.Unit?.DecimalPlaces ?? 0;
        var baseName = root?.Unit?.Name ?? baseUnit?.Name ?? "base unit";

        // ---- Structural change ----
        // Own change: new, reactivated (an inactive legacy row brought back into use is checked like a new one), or
        // unit / inner / quantity / factor differs from the stored row. Locked rows stay off the strict path below
        // (checkStrictly), so reactivating a locked row keeps its legacy problems as warnings.
        var ownChange = new Dictionary<Guid, bool>();
        foreach (var d in drafts)
        {
            var factor = FactorOf(d);
            ownChange[d.Id] = d.IsNew
                || (d.IsActive && !d.StoredIsActive)
                || d.UnitId != d.StoredUnitId
                || (d.IsBaseCandidate ? d.StoredInnerId is not null : d.InnerRef != d.StoredInnerId)
                || (d.IsBaseCandidate ? 1m : d.InnerQuantity) != d.StoredInnerQuantity
                || (factor is { } f && f != d.StoredFactor);
        }

        // R3/R4/R5 depend on the base unit and on the contained unit, so a row is checked strictly when it changed itself,
        // when any row on its inner chain changed (e.g. the inner's unit), or when the product's base unit changed (the
        // same stored quantities would now mean another unit). Locked rows cannot be on such a chain without an error:
        // R8 rejects a change to a locked row and its inners are locked too, and R9 rejects a base change once any row
        // is locked.
        var baseChanged = context.StoredBaseUnitId is { } previousBase && previousBase != context.BaseUnitId;
        var structural = new Dictionary<Guid, bool>();
        bool ChainChanged(ProductUnitDraft start)
        {
            var path = new List<Guid>();
            var seen = new HashSet<Guid>();
            var current = start;
            var changed = false;
            while (true)
            {
                if (structural.TryGetValue(current.Id, out var known)) { changed = known; break; }
                if (!seen.Add(current.Id)) break; // cycle: reported by R2
                path.Add(current.Id);
                if (ownChange[current.Id]) { changed = true; break; }
                if (current == root || current.IsBaseCandidate || current.InnerRef is not { } next || !byId.TryGetValue(next, out var nextDraft)) break;
                current = nextDraft;
            }
            foreach (var id in path) structural[id] = changed || ownChange[id];
            return structural[start.Id];
        }
        foreach (var d in drafts) structural[d.Id] = baseChanged || ChainChanged(d);

        // ---- Per-row rules ----
        foreach (var d in drafts)
        {
            var factor = FactorOf(d);
            var effectiveInnerQty = d.IsBaseCandidate ? 1m : d.InnerQuantity;
            var isStructural = structural[d.Id];
            var reactivated = d.IsActive && !d.StoredIsActive && !d.IsNew;
            var name = d.DisplayName;

            if (d.Unit is null)
            {
                Error(Key(d, "UnitId"), "Select a valid unit.");
                continue;
            }

            // R7: unit active for changed or reactivated rows (legacy active rows on an inactive unit: warning).
            if (!d.Unit.IsActive && d.IsActive)
            {
                if ((ownChange[d.Id] || reactivated) && d != root) Error(Key(d, "UnitId"), $"{d.Unit.Name} is inactive in the Unit Master; activate it or choose another unit.");
                else Warn(Key(d, "UnitId"), $"{d.Unit.Name} is inactive in the Unit Master.");
            }

            // R8: locked rows keep their structure.
            if (d.IsLocked)
            {
                if (d.UnitId != d.StoredUnitId) Error(Key(d, "UnitId"), LockedMessage);
                else if (d.InnerUnitId != d.StoredInnerUnitId) Error(Key(d, "InnerUnitId"), LockedMessage);
                else if (effectiveInnerQty != d.StoredInnerQuantity || (factor is { } lf && lf != d.StoredFactor)) Error(Key(d, "InnerQuantity"), LockedMessage);
            }

            var inner = d.InnerRef is { } innerId && byId.TryGetValue(innerId, out var innerDraft) && innerDraft != d ? innerDraft : null;

            // R8b: an active row contains an active row (locked rows may keep an inactive inner).
            if (inner is not null && d.IsActive && !inner.IsActive && !d.IsBaseCandidate && d.InnerResolutionError is null)
            {
                if (d.IsLocked) Warn(Key(d, "InnerUnitId"), $"{name} contains {inner.DisplayName}, which is inactive.");
                else Error(Key(d, "InnerUnitId"), $"{inner.DisplayName} is inactive; an active unit can only contain an active unit.");
            }

            if (d == root || d.IsBaseCandidate) continue;

            var checkStrictly = isStructural && !d.IsLocked;
            var reportLegacy = !checkStrictly && d.IsActive;

            // R3: category compatibility with the base unit.
            if (baseUnit is not null && !IsCategoryAllowed(baseUnit.Category, d.Unit.Category))
            {
                var message = d.Unit.Category == MeasurementCategory.Count
                    ? $"{d.Unit.Name} is a count unit; it can only be used over a count base unit."
                    : $"{d.Unit.Name} is a {d.Unit.Category.ToString().ToLowerInvariant()} unit and cannot be used with a {baseUnit.Category.ToString().ToLowerInvariant()} base unit.";
                if (checkStrictly) Error(Key(d, "UnitId"), message);
                else if (reportLegacy) Warn(Key(d, "UnitId"), message);
            }

            // R4: standard factors fix the quantity (1 kg contains 1000 g).
            if (inner?.Unit is { StandardFactor: { } innerSf } && d.Unit.StandardFactor is { } unitSf && innerSf > 0 && d.InnerQuantity is { } qty)
            {
                var ratio = unitSf / innerSf;
                var expected = Math.Round(ratio, ProductUnitGraph.InnerQuantityScale);
                string? message = null;
                if (expected != ratio || expected <= 0)
                {
                    message = $"{d.Unit.Name} cannot contain {inner.Unit.Name}: their standard sizes do not divide exactly.";
                }
                else if (qty != expected)
                {
                    message = $"1 {d.Unit.Name} is {ProductUnitGraph.FormatQuantity(expected)} {inner.Unit.Name}; enter {ProductUnitGraph.FormatQuantity(expected)}.";
                }
                if (message is not null)
                {
                    if (checkStrictly) Error(Key(d, "InnerQuantity"), message);
                    else if (reportLegacy) Warn(Key(d, "InnerQuantity"), message);
                }
            }

            if (factor is { } rowFactor)
            {
                // R5: whole number of the base unit's smallest step (generalised integer-factor rule).
                if (!ProductUnitGraph.IsWholeForBase(rowFactor, baseDp))
                {
                    var message = baseDp == 0
                        ? $"1 {name} would be {ProductUnitGraph.FormatQuantity(rowFactor)} {baseName}; it must be a whole number of {baseName}."
                        : $"1 {name} would be {ProductUnitGraph.FormatQuantity(rowFactor)} {baseName}; {baseName} allows at most {baseDp} decimal places.";
                    if (checkStrictly) Error(Key(d, "InnerQuantity"), message);
                    else if (reportLegacy) Warn(Key(d, "InnerQuantity"), message);
                }

                // R6: factor bounds.
                if (rowFactor < ProductUnitGraph.MinFactor || rowFactor > ProductUnitGraph.MaxFactor)
                {
                    var message = $"1 {name} would be {ProductUnitGraph.FormatQuantity(rowFactor)} {baseName}; it must be between 0.000001 and 1,000,000,000 {baseName}.";
                    if (checkStrictly) Error(Key(d, "InnerQuantity"), message);
                    else if (reportLegacy) Warn(Key(d, "InnerQuantity"), message);
                }
            }
        }

        // ---- R7: list-level uniqueness, defaults, label, barcode ----
        var active = drafts.Where(d => d.IsActive).ToList();
        foreach (var group in active.GroupBy(d => d.UnitId).Where(g => g.Count() > 1))
        {
            foreach (var d in group.Skip(1))
            {
                Error(Key(d, "UnitId"), $"{d.Unit?.Name ?? "This unit"} is already used by another active row of this product.");
            }
        }

        CheckDefault(drafts, d => d.IsDefaultPurchaseUnit, d => d.IsPurchasable, "IsDefaultPurchaseUnit", "purchase", Error);
        CheckDefault(drafts, d => d.IsDefaultSalesUnit, d => d.IsSellable, "IsDefaultSalesUnit", "sales", Error);

        var seenBarcodes = new Dictionary<string, ProductUnitDraft>(StringComparer.Ordinal);
        foreach (var d in drafts.Where(d => d.Index >= 0))
        {
            if (d.Label is { Length: > MaxLabelLength }) Error(Key(d, "Label"), $"Label can be at most {MaxLabelLength} characters.");

            if (d.Barcode is not { } barcode) continue;
            if (barcode.Length > MaxBarcodeLength) Error(Key(d, "Barcode"), $"Barcode can be at most {MaxBarcodeLength} characters.");
            else if (barcode.Any(char.IsWhiteSpace)) Error(Key(d, "Barcode"), "Barcode cannot contain spaces.");
            else if (d.IsActive && seenBarcodes.ContainsKey(barcode)) Error(Key(d, "Barcode"), "This barcode is already used by another unit of this product.");
            else if (d.IsActive && context.BarcodesUsedElsewhere.Contains(barcode)) Error(Key(d, "Barcode"), "This barcode is already used by another product.");
            if (d.IsActive) seenBarcodes.TryAdd(barcode, d);
        }

        // ---- Warning: two active units with the same factor ----
        foreach (var group in active.Where(d => FactorOf(d) is not null).GroupBy(d => FactorOf(d)!.Value).Where(g => g.Count() > 1))
        {
            var first = group.First();
            foreach (var d in group.Skip(1))
            {
                Warn(Key(d, "InnerQuantity"), $"1 {d.DisplayName} and 1 {first.DisplayName} are both {ProductUnitGraph.FormatQuantity(group.Key)} {baseName}.");
            }
        }

        // Every row must resolve to a factor before anything is saved; the rules above normally name the cause.
        if (errors.Count == 0)
        {
            foreach (var d in drafts.Where(d => FactorOf(d) is null))
            {
                Error(Key(d, "InnerUnitId"), "The size of this unit cannot be worked out; check the unit it contains.");
            }
        }

        // ---- Computed rows ----
        var sortOrders = ProductUnitGraph.SortOrders(drafts.Select(d => (d.Id, d == root, FactorOf(d), graph.TryGetValue(d.Id, out var r) ? r.Depth : null, d.Unit?.Name ?? string.Empty)));
        string NameOf(Guid id) => byId.TryGetValue(id, out var x) ? x.Unit?.Name ?? x.DisplayName : string.Empty;
        Guid? InnerOf(Guid id) => byId.TryGetValue(id, out var x) && x != root && !x.IsBaseCandidate ? x.InnerRef : null;
        decimal? FactorById(Guid id) => graph.TryGetValue(id, out var x) ? x.Factor : null;

        var rows = drafts.Select(d => new ProductUnitComputedRow(
            d.Id, d.Index, FactorOf(d), graph.TryGetValue(d.Id, out var r) ? r.Depth : null, sortOrders[d.Id],
            ProductUnitGraph.Chain(d.Id, InnerOf, FactorById, NameOf), structural[d.Id])).ToList();

        return new ProductUnitConfigurationResult(rows, Distinct(errors), Distinct(warnings));
    }

    /// <summary>Packaging may wrap anything; Weight/Volume/Length only over the same category; Count only over a Count
    /// base (D9, e.g. Dozen over Piece). The base unit itself is never Packaging.</summary>
    public static bool IsCategoryAllowed(MeasurementCategory baseCategory, MeasurementCategory unitCategory) => unitCategory switch
    {
        MeasurementCategory.Packaging => true,
        MeasurementCategory.Count => baseCategory == MeasurementCategory.Count,
        _ => unitCategory == baseCategory,
    };

    private static void CheckDefault(IReadOnlyList<ProductUnitDraft> drafts, Func<ProductUnitDraft, bool> isDefault, Func<ProductUnitDraft, bool> isAllowed,
        string field, string kind, Action<string, string> error)
    {
        var seen = false;
        foreach (var d in drafts.Where(isDefault))
        {
            if (!d.IsActive || !isAllowed(d))
            {
                error(Key(d, field), kind == "purchase"
                    ? "The default purchase unit must be active and purchasable."
                    : "The default sales unit must be active and sellable.");
            }
            else if (seen)
            {
                error(Key(d, field), $"Only one unit can be the default {kind} unit.");
            }
            seen |= d.IsActive && isAllowed(d);
        }
    }

    private static List<FieldError> Distinct(List<FieldError> items) => items.Distinct().ToList();
}
