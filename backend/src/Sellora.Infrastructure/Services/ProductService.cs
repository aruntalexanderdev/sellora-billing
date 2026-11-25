using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Entities;
using Sellora.Domain.Inventory;
using Sellora.Infrastructure.Data;

namespace Sellora.Infrastructure.Services;

/// <summary>
/// Products and their unit hierarchy (Phase 2 design §5.3).
/// </summary>
/// <remarks>
/// <para>Locking (relational providers; §5.4): update takes <c>FOR UPDATE</c> on the product row first
/// (<see cref="StockLocks.LockProductForUpdateAsync"/>), then <c>FOR SHARE</c> on every unit it references (submitted,
/// stored and base units, ordered by id). Create takes only the unit locks. The product lock serialises concurrent
/// edits of the same hierarchy (two edits cannot both pass the cycle/uniqueness checks) and conflicts with the
/// <c>FOR KEY SHARE</c> that purchase/sale creates take, so a line is never built from a factor that is being changed.
/// The unit locks serialise against <see cref="UnitService"/>, which takes <c>FOR UPDATE</c> on a unit before its
/// in-use check. Nothing here waits on Products while holding a unit lock.</para>
/// <para>Rows referenced by lines or prices (and everything they contain) are locked, see <see cref="ProductUnitUsage"/>:
/// their structure cannot change. Omitted rows are kept inactive when locked or still contained in a kept row,
/// otherwise deleted.</para>
/// </remarks>
public class ProductService(SelloraDbContext db) : IProductService
{
    private const string BarcodeIndex = "IX_ProductUnits_CompanyId_Barcode";
    private static readonly IReadOnlyDictionary<Guid, string> NoLocks = new Dictionary<Guid, string>();

    public async Task<Product> CreateProductAsync(UpsertProductInput input, CancellationToken ct = default)
    {
        EnsureUnitCount(input.Units);
        await using var transaction = await StockLocks.BeginAsync(db, ct);
        await TaxLocks.LockAssignmentsAsync(db, ct); // before the product lock: the same order as the assignment writers (mirror TaxId)

        var units = await LoadUnitsAsync(ReferencedUnitIds(input.BaseUnitId, input.Units, []), lockForShare: true, ct);
        await ValidateReferencesAsync(input, units, currentBaseUnitId: null, ct);
        await EnsureUniqueSkuAsync(null, input.Sku, ct);
        var categoryName = await ValidateClassificationAsync(input, currentCategoryId: null, currentSubcategoryId: null, ct);

        var productId = Guid.NewGuid();
        var plan = BuildDrafts(input.Units, [], NoLocks, units);
        var result = await ValidateOrThrowAsync(productId, input.BaseUnitId, units, storedBaseUnitId: null, hasStockHistory: false, plan, ct);
        var hsnCode = TaxRules.NormaliseHsn(input.HsnCode);
        var mirrorTaxId = await ApplyTaxAssignmentAsync(productId, input, isNew: true, ct);

        var product = new Product
        {
            Id = productId,
            Sku = input.Sku.Trim(),
            Name = input.Name.Trim(),
            Category = categoryName,
            CategoryId = input.CategoryId,
            SubcategoryId = input.SubcategoryId,
            BaseUnitId = input.BaseUnitId,
            TaxId = mirrorTaxId,
            HsnCode = hsnCode,
            PriceIncludesTax = input.PriceIncludesTax,
            ReorderLevel = input.ReorderLevel,
        };
        foreach (var draft in plan.Drafts)
        {
            var row = new ProductUnit { Id = draft.Id, ProductId = productId };
            Apply(row, draft, result.Row(draft.Id), structure: true);
            product.Units.Add(row);
        }
        db.Products.Add(product);

        await SaveMappingBarcodeAsync(plan, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        await LoadForReturnAsync(product, ct);
        return product;
    }

    public async Task<Product?> UpdateProductAsync(Guid productId, UpsertProductInput input, CancellationToken ct = default)
    {
        EnsureUnitCount(input.Units);
        await using var transaction = await StockLocks.BeginAsync(db, ct);

        // 0. The assignment advisory lock (mirror TaxId), always before the product row lock.
        await TaxLocks.LockAssignmentsAsync(db, ct);

        // 1. Product row lock, then the rows it owns (no Unit includes: units are read below under their own lock).
        await StockLocks.LockProductForUpdateAsync(db, productId, ct);
        var product = await db.Products.Include(p => p.Units).SingleOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null) return null;

        // 2. Unit locks (FOR SHARE, by id) over everything the hierarchy references.
        var storedUnitIds = product.Units.Select(u => u.UnitId).Append(product.BaseUnitId);
        var units = await LoadUnitsAsync(ReferencedUnitIds(input.BaseUnitId, input.Units, storedUnitIds), lockForShare: true, ct);

        // 3. Reference checks (base unit, packaging base, inactive base, tax, SKU).
        await ValidateReferencesAsync(input, units, product.BaseUnitId, ct);
        await EnsureUniqueSkuAsync(productId, input.Sku, ct);
        var categoryName = await ValidateClassificationAsync(input, product.CategoryId, product.SubcategoryId, ct);

        // 4. Locks, drafts, rules.
        var locks = await ProductUnitUsage.GetLockReasonsAsync(db, product.Units.ToList(), ct);
        var hasStockHistory = input.BaseUnitId != product.BaseUnitId && await ProductUnitUsage.HasStockHistoryAsync(db, productId, ct);
        var plan = BuildDrafts(input.Units, product.Units.ToList(), locks, units);
        var result = await ValidateOrThrowAsync(productId, input.BaseUnitId, units, product.BaseUnitId, hasStockHistory, plan, ct);
        var hsnCode = TaxRules.NormaliseHsn(input.HsnCode);
        var mirrorTaxId = await ApplyTaxAssignmentAsync(productId, input, isNew: false, ct);

        product.Sku = input.Sku.Trim();
        product.Name = input.Name.Trim();
        product.Category = categoryName;
        product.CategoryId = input.CategoryId;
        product.SubcategoryId = input.SubcategoryId;
        product.BaseUnitId = input.BaseUnitId;
        product.TaxId = mirrorTaxId;
        product.HsnCode = hsnCode;
        product.PriceIncludesTax = input.PriceIncludesTax;
        product.ReorderLevel = input.ReorderLevel;

        await ApplyUpdateAsync(product, plan, result, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        await LoadForReturnAsync(product, ct);
        return product;
    }

    public async Task<bool> DeleteProductAsync(Guid productId, Guid userId, string? correlationId, CancellationToken ct = default)
    {
        await using var transaction = await StockLocks.BeginAsync(db, ct);

        // FOR UPDATE conflicts with the FOR KEY SHARE that purchase/sale/adjustment/transfer creates take on Products, so no
        // new line can reference the product between the history check below and the delete.
        await StockLocks.LockProductForUpdateAsync(db, productId, ct);
        var product = await db.Products.Include(p => p.Units).SingleOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null) return false;

        var usedIn = await ProductUnitUsage.GetProductHistoryAsync(db, productId, ct);
        if (usedIn.Count > 0)
        {
            throw new DomainValidationException("Product",
                $"This product has history ({string.Join(", ", usedIn)}) and cannot be deleted. Deactivate it instead.");
        }

        var unitIds = product.Units.Select(u => u.Id).ToList();
        db.SellingPrices.RemoveRange(await db.SellingPrices.Where(p => unitIds.Contains(p.ProductUnitId)).ToListAsync(ct));
        db.StockItems.RemoveRange(await db.StockItems.Where(s => s.ProductId == productId).ToListAsync(ct)); // all zero (checked)
        db.ProductUnits.RemoveRange(product.Units); // EF orders the self-referencing rows (outer packs before inner ones)
        db.Products.Remove(product);

        AuditLog.Append(db, userId, correlationId, AuditActions.ProductDeleted, AuditTargets.Product, product.Id, null,
            new
            {
                sku = product.Sku,
                name = product.Name,
                baseUnitId = product.BaseUnitId,
                isActive = product.IsActive,
                units = product.Units.Select(u => new { u.Id, u.UnitId, u.InnerProductUnitId, u.InnerQuantity, u.ConversionFactorToBase, u.Barcode }),
            },
            null);

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<ProductUnitPreview?> PreviewUnitsAsync(Guid? productId, Guid baseUnitId, IReadOnlyList<ProductUnitInputItem> units, CancellationToken ct = default)
    {
        if (units.Count > ProductUnitConfigurationValidator.MaxUnits)
        {
            if (productId is { } pid && !await db.Products.AnyAsync(p => p.Id == pid, ct)) return null;
            return new ProductUnitPreview(false, [], [new FieldError("Units", ProductUnitConfigurationValidator.TooManyUnitsMessage)], []);
        }

        List<ProductUnit> stored = [];
        var locks = NoLocks;
        Guid? storedBaseUnitId = null;
        var hasStockHistory = false;

        if (productId is { } id)
        {
            var product = await db.Products.AsNoTracking().Include(p => p.Units).SingleOrDefaultAsync(p => p.Id == id, ct);
            if (product is null) return null;
            stored = product.Units.ToList();
            locks = await ProductUnitUsage.GetLockReasonsAsync(db, stored, ct);
            storedBaseUnitId = product.BaseUnitId;
            hasStockHistory = baseUnitId != product.BaseUnitId && await ProductUnitUsage.HasStockHistoryAsync(db, id, ct);
        }

        var unitInfos = await LoadUnitsAsync(ReferencedUnitIds(baseUnitId, units, stored.Select(u => u.UnitId)), lockForShare: false, ct);
        var plan = BuildDrafts(units, stored, locks, unitInfos);
        var result = await ValidateAsync(productId ?? Guid.Empty, baseUnitId, unitInfos, storedBaseUnitId, hasStockHistory, plan, ct);
        var errors = plan.Errors.Concat(result.Errors).Distinct().ToList();

        var rows = plan.Drafts
            .Where(d => d.Index >= 0 && d.Unit is not null)
            .Select(d => (Draft: d, Row: result.Row(d.Id)))
            .Where(x => x.Row.Factor is not null && x.Row.Depth is not null && x.Row.Chain is not null)
            .OrderBy(x => x.Row.SortOrder)
            .Select(x => new ProductUnitPreviewRow(
                x.Draft.Index, x.Draft.UnitId, x.Draft.Unit!.Code, x.Draft.Label ?? x.Draft.Unit.Name, x.Row.Factor!.Value,
                x.Row.Depth!.Value, x.Row.SortOrder, x.Row.Chain!, x.Draft.IsLocked))
            .ToList();

        return new ProductUnitPreview(errors.Count == 0, rows, errors, result.Warnings);
    }

    // ------------------------------------------------------------------ drafts

    private sealed record DraftPlan(IReadOnlyList<ProductUnitDraft> Drafts, IReadOnlyList<FieldError> Errors, IReadOnlyList<ProductUnit> Deleted);

    private sealed record SubmittedRow(int Index, ProductUnitInputItem Input, Guid Id, ProductUnit? Stored);

    /// <summary>
    /// Turns the request plus the stored rows into drafts (shared by save and preview):
    /// <list type="bullet">
    /// <item>A submitted id must be a row of this product. The base row (no inner) without an id maps onto the stored
    /// base row: a product has exactly one base row for its lifetime (unique index), its unit follows the base unit.</item>
    /// <item><c>InnerUnitId</c> resolves to the active submitted row with that unit (unique among active rows). A locked
    /// row whose <c>InnerUnitId</c> is the stored inner's unit keeps the stored inner row.</item>
    /// <item>Omitted stored rows: kept and inactive when locked or contained in a kept row; otherwise deleted. An
    /// active unlocked row that still contains an omitted row is an error.</item>
    /// </list>
    /// </summary>
    private static DraftPlan BuildDrafts(IReadOnlyList<ProductUnitInputItem> inputs, IReadOnlyCollection<ProductUnit> stored,
        IReadOnlyDictionary<Guid, string> locks, IReadOnlyDictionary<Guid, UnitInfo> units)
    {
        var errors = new List<FieldError>();
        var storedById = stored.ToDictionary(u => u.Id);
        var storedBase = stored.FirstOrDefault(u => u.IsBaseUnit);
        var claimed = new HashSet<Guid>();

        // Pass 1: identities. Explicit ids are claimed first so a base row sent without id cannot steal the stored
        // base row from a later row that names it.
        foreach (var input in inputs)
        {
            if (input.Id is { } explicitId && storedById.ContainsKey(explicitId)) claimed.Add(explicitId);
        }

        var rows = new List<SubmittedRow>();
        var seenIds = new HashSet<Guid>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            ProductUnit? storedRow = null;
            if (input.Id is { } id)
            {
                if (!storedById.TryGetValue(id, out storedRow))
                {
                    errors.Add(new FieldError("Units", "One of the units no longer exists on this product. Reload and try again."));
                    continue;
                }
                if (!seenIds.Add(id))
                {
                    errors.Add(new FieldError("Units", "The same unit row was submitted twice."));
                    continue;
                }
            }
            else if (input.InnerUnitId is null && storedBase is not null && claimed.Add(storedBase.Id))
            {
                storedRow = storedBase;
                seenIds.Add(storedBase.Id);
            }
            rows.Add(new SubmittedRow(i, input, storedRow?.Id ?? Guid.NewGuid(), storedRow));
        }

        var submittedIds = rows.Select(r => r.Id).ToHashSet();
        var omitted = stored.Where(s => !submittedIds.Contains(s.Id)).ToDictionary(s => s.Id);
        var keep = new HashSet<Guid>(omitted.Keys.Where(locks.ContainsKey));
        var drafts = new List<ProductUnitDraft>();

        // Pass 2: inner resolution.
        foreach (var r in rows)
        {
            var lockReason = r.Stored is null ? null : locks.GetValueOrDefault(r.Id);
            var storedInnerId = r.Stored?.InnerProductUnitId;
            Guid? innerRef = null;
            string? resolutionError = null;

            if (r.Input.InnerUnitId is { } innerUnitId)
            {
                if (lockReason is not null && storedInnerId is { } si && storedById.TryGetValue(si, out var storedInner) && storedInner.UnitId == innerUnitId)
                {
                    innerRef = si;
                }
                else if (innerUnitId == r.Input.UnitId)
                {
                    innerRef = r.Id; // contains itself: reported as a cycle
                }
                else
                {
                    var candidates = rows.Where(o => o.Id != r.Id && o.Input.UnitId == innerUnitId).ToList();
                    var pick = candidates.FirstOrDefault(o => o.Input.IsActive)
                               ?? candidates.FirstOrDefault(o => o.Id == storedInnerId)
                               ?? (candidates.Count == 1 ? candidates[0] : null);
                    if (pick is not null)
                    {
                        innerRef = pick.Id;
                    }
                    else if (omitted.Values.Where(o => o.UnitId == innerUnitId).OrderByDescending(o => o.Id == storedInnerId).FirstOrDefault() is { } removed)
                    {
                        innerRef = removed.Id;
                        if (r.Input.IsActive && lockReason is null)
                        {
                            var innerName = removed.Label ?? units.GetValueOrDefault(removed.UnitId)?.Name ?? "The contained unit";
                            resolutionError = $"{innerName} is being removed, but this unit still contains it; keep it or choose another contained unit.";
                        }
                    }
                }
            }

            if (innerRef is { } ir && omitted.ContainsKey(ir)) keep.Add(ir);

            drafts.Add(new ProductUnitDraft
            {
                Index = r.Index,
                Id = r.Id,
                Unit = units.GetValueOrDefault(r.Input.UnitId),
                UnitId = r.Input.UnitId,
                InnerUnitId = r.Input.InnerUnitId,
                InnerRef = innerRef,
                InnerResolutionError = resolutionError,
                InnerQuantity = r.Input.InnerQuantity,
                Label = Clean(r.Input.Label),
                Barcode = Clean(r.Input.Barcode),
                IsPurchasable = r.Input.IsPurchasable,
                IsSellable = r.Input.IsSellable,
                IsDefaultPurchaseUnit = r.Input.IsDefaultPurchaseUnit,
                IsDefaultSalesUnit = r.Input.IsDefaultSalesUnit,
                IsActive = r.Input.IsActive,
                IsNew = r.Stored is null,
                LockReason = lockReason,
                StoredIsBase = r.Stored?.IsBaseUnit ?? false,
                StoredUnitId = r.Stored?.UnitId,
                StoredInnerId = storedInnerId,
                StoredInnerUnitId = storedInnerId is { } sid ? storedById.GetValueOrDefault(sid)?.UnitId : null,
                StoredInnerQuantity = r.Stored?.InnerQuantity,
                StoredFactor = r.Stored?.ConversionFactorToBase,
                StoredIsActive = r.Stored?.IsActive ?? false,
            });
        }

        // Lock/containment closure over omitted rows.
        var queue = new Queue<Guid>(keep);
        while (queue.TryDequeue(out var k))
        {
            if (omitted[k].InnerProductUnitId is { } inner && omitted.ContainsKey(inner) && keep.Add(inner)) queue.Enqueue(inner);
        }

        // Kept rows (and an omitted base row, which can never be deleted; R1 reports it) join the graph unsubmitted.
        foreach (var s in omitted.Values.Where(s => keep.Contains(s.Id) || s.IsBaseUnit))
        {
            drafts.Add(new ProductUnitDraft
            {
                Index = -1,
                Id = s.Id,
                Unit = units.GetValueOrDefault(s.UnitId),
                UnitId = s.UnitId,
                InnerUnitId = s.InnerProductUnitId is { } x ? storedById.GetValueOrDefault(x)?.UnitId ?? Guid.Empty : null,
                InnerRef = s.InnerProductUnitId,
                InnerQuantity = s.InnerQuantity,
                Label = s.Label,
                Barcode = s.Barcode,
                IsPurchasable = s.IsPurchasable,
                IsSellable = s.IsSellable,
                IsActive = s.IsBaseUnit,
                LockReason = locks.GetValueOrDefault(s.Id),
                StoredIsBase = s.IsBaseUnit,
                StoredUnitId = s.UnitId,
                StoredInnerId = s.InnerProductUnitId,
                StoredInnerUnitId = s.InnerProductUnitId is { } y ? storedById.GetValueOrDefault(y)?.UnitId : null,
                StoredInnerQuantity = s.InnerQuantity,
                StoredFactor = s.ConversionFactorToBase,
                StoredIsActive = s.IsActive,
            });
        }

        var deleted = omitted.Values.Where(s => !keep.Contains(s.Id) && !s.IsBaseUnit).ToList();
        return new DraftPlan(drafts, errors, deleted);
    }

    /// <summary>Bounds the work done (and locks taken) for one request; the HTTP contracts enforce the same limit.</summary>
    private static void EnsureUnitCount(IReadOnlyList<ProductUnitInputItem> units)
    {
        if (units.Count > ProductUnitConfigurationValidator.MaxUnits)
        {
            throw new DomainValidationException("Units", ProductUnitConfigurationValidator.TooManyUnitsMessage);
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IEnumerable<Guid> ReferencedUnitIds(Guid baseUnitId, IReadOnlyList<ProductUnitInputItem> inputs, IEnumerable<Guid> stored) =>
        inputs.Select(i => i.UnitId)
            .Concat(inputs.Where(i => i.InnerUnitId.HasValue).Select(i => i.InnerUnitId!.Value))
            .Concat(stored)
            .Append(baseUnitId);

    private async Task<ProductUnitConfigurationResult> ValidateAsync(Guid productId, Guid baseUnitId, IReadOnlyDictionary<Guid, UnitInfo> units,
        Guid? storedBaseUnitId, bool hasStockHistory, DraftPlan plan, CancellationToken ct)
    {
        var barcodes = await BarcodesUsedElsewhereAsync(productId, plan.Drafts, ct);
        var context = new ProductUnitValidationContext(baseUnitId, units.GetValueOrDefault(baseUnitId), storedBaseUnitId, hasStockHistory, barcodes);
        return ProductUnitConfigurationValidator.Validate(context, plan.Drafts);
    }

    private async Task<ProductUnitConfigurationResult> ValidateOrThrowAsync(Guid productId, Guid baseUnitId, IReadOnlyDictionary<Guid, UnitInfo> units,
        Guid? storedBaseUnitId, bool hasStockHistory, DraftPlan plan, CancellationToken ct)
    {
        var result = await ValidateAsync(productId, baseUnitId, units, storedBaseUnitId, hasStockHistory, plan, ct);
        // Product-level (BaseUnitId) errors first, so single-field callers see the most fundamental one.
        var errors = result.Errors.Where(e => e.Field == "BaseUnitId")
            .Concat(plan.Errors)
            .Concat(result.Errors.Where(e => e.Field != "BaseUnitId"))
            .Distinct().ToList();
        if (errors.Count > 0) throw new DomainValidationException(errors);
        return result;
    }

    private async Task<IReadOnlySet<string>> BarcodesUsedElsewhereAsync(Guid productId, IReadOnlyList<ProductUnitDraft> drafts, CancellationToken ct)
    {
        var codes = drafts.Where(d => d.IsActive && d.Barcode is not null).Select(d => d.Barcode!).Distinct().ToList();
        if (codes.Count == 0) return new HashSet<string>();
        var taken = await db.ProductUnits
            .Where(u => u.ProductId != productId && u.IsActive && u.Barcode != null && codes.Contains(u.Barcode))
            .Select(u => u.Barcode!)
            .ToListAsync(ct);
        return taken.ToHashSet(StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ apply

    /// <summary>Copies the draft onto the entity. Only the scalar inner id is set, never the navigation: the FK is
    /// (ProductId, InnerProductUnitId) and assigning the navigation to null could make EF null the shared ProductId.</summary>
    private static void Apply(ProductUnit row, ProductUnitDraft draft, ProductUnitComputedRow computed, bool structure)
    {
        if (structure)
        {
            row.UnitId = draft.UnitId;
            row.IsBaseUnit = draft.IsBaseCandidate;
            row.InnerProductUnitId = draft.IsBaseCandidate ? null : draft.InnerRef;
            row.InnerQuantity = draft.IsBaseCandidate ? 1m : draft.InnerQuantity!.Value;
            row.ConversionFactorToBase = draft.IsBaseCandidate ? 1m : computed.Factor!.Value;
        }
        row.Label = draft.Label;
        row.Barcode = draft.Barcode;
        row.IsPurchasable = draft.IsPurchasable;
        row.IsSellable = draft.IsSellable;
        row.IsDefaultPurchaseUnit = draft.IsDefaultPurchaseUnit;
        row.IsDefaultSalesUnit = draft.IsDefaultSalesUnit;
        row.IsActive = draft.IsActive;
        row.SortOrder = computed.SortOrder;
    }

    /// <summary>
    /// Applies the plan in up to three saves inside the caller's transaction, because PostgreSQL checks the partial
    /// unique indexes (active unit per product, default purchase/sales unit, barcode) per row and EF does not order
    /// statements by filtered indexes: (1) rows that give up a unique value release it (deactivate, clear defaults and
    /// barcode); (2) the base row takes its final unit (a base-unit change); (3) everything takes its final state, new
    /// rows are inserted and unreferenced omitted rows deleted (EF orders those by the inner-unit foreign key).
    /// </summary>
    private async Task ApplyUpdateAsync(Product product, DraftPlan plan, ProductUnitConfigurationResult result, CancellationToken ct)
    {
        var draftsById = plan.Drafts.ToDictionary(d => d.Id);
        var deletedIds = plan.Deleted.Select(d => d.Id).ToHashSet();

        // (1) release
        foreach (var row in product.Units)
        {
            var final = draftsById.GetValueOrDefault(row.Id);
            var deleted = final is null || deletedIds.Contains(row.Id);
            var givesUpActiveUnit = row.IsActive && !row.IsBaseUnit && (deleted || !final!.IsActive || final.UnitId != row.UnitId);
            if (givesUpActiveUnit)
            {
                row.IsActive = false;
                row.IsDefaultPurchaseUnit = false;
                row.IsDefaultSalesUnit = false;
            }
            if (row.IsDefaultPurchaseUnit && (deleted || !final!.IsDefaultPurchaseUnit)) row.IsDefaultPurchaseUnit = false;
            if (row.IsDefaultSalesUnit && (deleted || !final!.IsDefaultSalesUnit)) row.IsDefaultSalesUnit = false;
            if (row.Barcode is not null && (deleted || final!.Barcode != row.Barcode)) row.Barcode = null;
        }
        await SaveMappingBarcodeAsync(plan, ct);

        // (2) base row unit
        var baseRow = product.Units.SingleOrDefault(u => u.IsBaseUnit);
        if (baseRow is not null && draftsById.TryGetValue(baseRow.Id, out var baseDraft) && baseRow.UnitId != baseDraft.UnitId)
        {
            baseRow.UnitId = baseDraft.UnitId;
            await SaveMappingBarcodeAsync(plan, ct);
        }

        // (3) final state
        var existingById = product.Units.ToDictionary(u => u.Id);
        foreach (var draft in plan.Drafts)
        {
            if (existingById.TryGetValue(draft.Id, out var row))
            {
                Apply(row, draft, result.Row(draft.Id), structure: !draft.IsLocked);
            }
            else
            {
                row = new ProductUnit { Id = draft.Id, ProductId = product.Id };
                Apply(row, draft, result.Row(draft.Id), structure: true);
                // The product is tracked, so a pre-keyed new row must be added to the context explicitly.
                db.ProductUnits.Add(row);
            }
        }
        db.ChangeTracker.DetectChanges(); // re-point inner navigations before deleting the rows they used to point at
        foreach (var row in plan.Deleted)
        {
            db.ProductUnits.Remove(row);
        }
        await SaveMappingBarcodeAsync(plan, ct);
        foreach (var row in plan.Deleted) product.Units.Remove(row);
    }

    /// <summary>The barcode pre-check cannot see a concurrent uncommitted save of another product, so the partial unique
    /// index is the final guard; its violation is reported on the first submitted row whose barcode is new.</summary>
    private async Task SaveMappingBarcodeAsync(DraftPlan plan, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: BarcodeIndex,
        })
        {
            var row = plan.Drafts.FirstOrDefault(d => d.Index >= 0 && d.IsActive && d.Barcode is not null && (d.IsNew || !d.StoredIsActive))
                      ?? plan.Drafts.FirstOrDefault(d => d.Index >= 0 && d.IsActive && d.Barcode is not null);
            throw new DomainValidationException(row is null ? "Units" : ProductUnitConfigurationValidator.Key(row, "Barcode"),
                "This barcode is already used by another product.");
        }
    }

    private async Task LoadForReturnAsync(Product product, CancellationToken ct)
    {
        await db.Entry(product).Reference(p => p.BaseUnit).LoadAsync(ct);
        await db.Entry(product).Reference(p => p.Tax).LoadAsync(ct);
        await db.Entry(product).Collection(p => p.Units).Query().Include(u => u.Unit).LoadAsync(ct);
    }

    // ------------------------------------------------------------------ references

    /// <summary>Reads the units, on a relational provider <c>FOR SHARE</c> in id order (until the transaction ends), so
    /// a concurrent unit update/deactivation waits for this save and vice versa (see class remarks). The statement is
    /// not composed further, so the locking clause stays at the top level.</summary>
    private async Task<IReadOnlyDictionary<Guid, UnitInfo>> LoadUnitsAsync(IEnumerable<Guid> ids, bool lockForShare, CancellationToken ct)
    {
        var distinct = ids.Distinct().Order().ToArray();
        var rows = lockForShare && db.Database.IsRelational()
            ? await db.Units.FromSqlInterpolated($"SELECT * FROM \"Units\" WHERE \"Id\" = ANY({distinct}) ORDER BY \"Id\" FOR SHARE")
                .AsNoTracking().ToListAsync(ct)
            : await db.Units.AsNoTracking().Where(u => distinct.Contains(u.Id)).ToListAsync(ct);
        return rows.ToDictionary(u => u.Id, UnitInfo.From);
    }

    /// <param name="currentBaseUnitId">The product's stored base unit on update; null on create. An inactive
    /// base unit is only rejected when it is being newly assigned, so legacy products whose base unit has
    /// since been deactivated remain editable.</param>
    private async Task ValidateReferencesAsync(UpsertProductInput input, IReadOnlyDictionary<Guid, UnitInfo> units, Guid? currentBaseUnitId, CancellationToken ct)
    {
        if (!units.TryGetValue(input.BaseUnitId, out var baseUnit))
        {
            throw new DomainValidationException(nameof(input.BaseUnitId), ProductUnitConfigurationValidator.InvalidBaseUnitMessage);
        }
        if (baseUnit.Category == MeasurementCategory.Packaging)
        {
            throw new DomainValidationException(nameof(input.BaseUnitId), ProductUnitConfigurationValidator.PackagingBaseUnitMessage);
        }
        if (!baseUnit.IsActive && input.BaseUnitId != currentBaseUnitId)
        {
            throw new DomainValidationException(nameof(input.BaseUnitId), ProductUnitConfigurationValidator.InactiveBaseUnitMessage);
        }
    }

    /// <summary>
    /// TP-1: writes the product-level tax assignment of the company country (sales override, purchase profile empty = same as sales; no override deletes the row)
    /// and returns the mirror <c>Taxes</c> id the product must carry (D3): the mirror of its resolved sales profile. 400 <c>taxProfile</c> when the company has
    /// no country or nothing applies; <c>salesTaxProfileId</c> / <c>purchaseTaxProfileId</c> for a profile that is unknown, inactive or of another country (BR10).
    /// The caller saves the product and the assignment together.
    /// </summary>
    private async Task<Guid> ApplyTaxAssignmentAsync(Guid productId, UpsertProductInput input, bool isNew, CancellationToken ct)
    {
        var countryId = await db.BusinessSettings.AsNoTracking().Select(s => s.CountryId).FirstOrDefaultAsync(ct)
            ?? throw new DomainValidationException("taxProfile", "Set the company's country in the business settings before saving products: tax profiles are per country.");
        if (input.PurchaseTaxProfileId is not null && input.SalesTaxProfileId is null)
        {
            throw new DomainValidationException("salesTaxProfileId", "Choose a sales profile to override the purchase profile.");
        }

        var current = isNew ? null
            : await db.TaxAssignments.SingleOrDefaultAsync(a => a.Scope == TaxScope.Product && a.ProductId == productId && a.CountryId == countryId, ct);

        Guid salesProfileId;
        if (input.SalesTaxProfileId is { } sales)
        {
            await TaxAssignmentRules.EnsureAssignableAsync(db, sales, countryId, current?.SalesTaxProfileId, "salesTaxProfileId", ct);
            var purchase = input.PurchaseTaxProfileId is { } p && p != sales ? p : (Guid?)null;
            if (purchase is { } purchaseId) await TaxAssignmentRules.EnsureAssignableAsync(db, purchaseId, countryId, current?.PurchaseTaxProfileId, "purchaseTaxProfileId", ct);

            if (current is null)
            {
                db.TaxAssignments.Add(new TaxAssignment
                {
                    Id = Guid.NewGuid(), Scope = TaxScope.Product, CountryId = countryId, ProductId = productId,
                    SalesTaxProfileId = sales, PurchaseTaxProfileId = purchase, CreatedAt = DateTime.UtcNow,
                });
            }
            else if (current.SalesTaxProfileId != sales || current.PurchaseTaxProfileId != purchase)
            {
                current.SalesTaxProfileId = sales;
                current.PurchaseTaxProfileId = purchase;
                current.UpdatedAt = DateTime.UtcNow;
            }
            salesProfileId = sales;
        }
        else
        {
            if (current is not null) db.TaxAssignments.Remove(current);
            var index = await TaxAssignmentIndex.LoadAsync(db, countryId, ct);
            var inherited = index.Find(null, input.CategoryId, input.SubcategoryId, includeProduct: false)
                ?? throw new DomainValidationException("taxProfile",
                    $"No tax profile applies to {input.Name.Trim()}. Choose a tax profile for the product, assign one to its category or set a company default.");
            salesProfileId = inherited.SalesTaxProfileId;
        }

        return await db.TaxProfiles.AsNoTracking().Where(p => p.Id == salesProfileId).Select(p => p.LegacyTaxId).SingleAsync(ct);
    }

    /// <summary>Phase 08 (FI-D16): the category must exist in the company, the subcategory must belong to it and a subcategory needs
    /// a category. An inactive master cannot be newly assigned; the one the product already has is kept. Returns the category
    /// name to mirror into the legacy <c>Product.Category</c> (null when none).</summary>
    private async Task<string?> ValidateClassificationAsync(UpsertProductInput input, Guid? currentCategoryId, Guid? currentSubcategoryId, CancellationToken ct)
    {
        if (input.CategoryId is null)
        {
            if (input.SubcategoryId is not null)
            {
                throw new DomainValidationException("subcategoryId", "Select a category before choosing a subcategory.");
            }
            return null;
        }

        var categoryId = input.CategoryId.Value;
        var category = await db.ProductCategories.AsNoTracking().Where(c => c.Id == categoryId)
            .Select(c => new { c.Name, c.IsActive }).SingleOrDefaultAsync(ct);
        if (category is null) throw new DomainValidationException("categoryId", "Select a valid category.");
        if (!category.IsActive && categoryId != currentCategoryId)
        {
            throw new DomainValidationException("categoryId", "This category is inactive and cannot be assigned.");
        }

        if (input.SubcategoryId is { } subcategoryId)
        {
            var sub = await db.ProductSubcategories.AsNoTracking().Where(s => s.Id == subcategoryId && s.CategoryId == categoryId)
                .Select(s => new { s.IsActive }).SingleOrDefaultAsync(ct);
            if (sub is null) throw new DomainValidationException("subcategoryId", "Select a subcategory of the chosen category.");
            if (!sub.IsActive && subcategoryId != currentSubcategoryId)
            {
                throw new DomainValidationException("subcategoryId", "This subcategory is inactive and cannot be assigned.");
            }
        }
        return category.Name;
    }

    private async Task EnsureUniqueSkuAsync(Guid? selfId, string sku, CancellationToken ct)
    {
        var lower = sku.Trim().ToLower();
        if (await db.Products.AnyAsync(p => (selfId == null || p.Id != selfId) && p.Sku.ToLower() == lower, ct))
        {
            throw new DomainValidationException(nameof(UpsertProductInput.Sku), "A product with this SKU already exists.");
        }
    }
}
