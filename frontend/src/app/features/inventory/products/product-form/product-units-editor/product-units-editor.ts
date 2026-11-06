import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormArray,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { Subject, catchError, map, of, startWith, switchMap, timer } from 'rxjs';
import {
  INNER_QUANTITY_DECIMALS,
  computeFactors,
  equivalenceChain,
  formatChainQuantity,
  isCategoryAllowed,
  isFactorInRange,
  isValidInnerQuantity,
  isWholeForBase,
  outerKeys,
  suggestInnerQuantity,
  type ChainNode,
  type FactorResult,
} from '../../../../../shared/units/unit-conversion';
import { quantityStep } from '../../../../../shared/forms/quantity-input';
import type { UnitItem } from '../../../../masters/units/units.models';
import type {
  FieldError,
  ProductUnit,
  ProductUnitInput,
  ProductUnitLockReason,
  UnitPreviewRequest,
  UnitPreviewResponse,
} from '../../products.models';
import { ProductsService } from '../../products.service';

/** Debounce before asking the server to validate the hierarchy. */
export const PREVIEW_DEBOUNCE_MS = 400;

/**
 * One editable product unit row. Besides the request fields it carries read-only metadata from the
 * loaded DTO (`isBaseUnit`, `isLocked`, `lockReason`, `innerProductUnitId`), which is never submitted.
 */
export type UnitRowGroup = FormGroup<{
  id: FormControl<string | null>;
  isBaseUnit: FormControl<boolean>;
  unitId: FormControl<string>;
  innerUnitId: FormControl<string>;
  innerQuantity: FormControl<number | null>;
  label: FormControl<string>;
  barcode: FormControl<string>;
  isPurchasable: FormControl<boolean>;
  isSellable: FormControl<boolean>;
  isDefaultPurchaseUnit: FormControl<boolean>;
  isDefaultSalesUnit: FormControl<boolean>;
  isActive: FormControl<boolean>;
  isLocked: FormControl<boolean>;
  lockReason: FormControl<ProductUnitLockReason | null>;
  innerProductUnitId: FormControl<string | null>;
}>;

export type UnitRowValue = ReturnType<UnitRowGroup['getRawValue']>;

/**
 * R6 client mirror: `0 < qty ≤ 1e9`, at most 6 decimals. Empty is left to `required`.
 * Not `shared/forms/quantity-input` `maxDecimalPlaces`: that is for entered quantities (unit dp, clamped 0–3),
 * whereas InnerQuantity is numeric(18,6) and may legitimately be 0.001 of a 0-dp unit's parent chain.
 */
function innerQuantityValidator(control: AbstractControl<number | null>): ValidationErrors | null {
  const value = control.value;
  if (value === null || value === undefined || (value as unknown) === '') return null;
  return isValidInnerQuantity(Number(value)) ? null : { innerQuantity: true };
}

const DEFAULT_ROW: UnitRowValue = {
  id: null,
  isBaseUnit: false,
  unitId: '',
  innerUnitId: '',
  innerQuantity: null,
  label: '',
  barcode: '',
  isPurchasable: true,
  isSellable: true,
  isDefaultPurchaseUnit: false,
  isDefaultSalesUnit: false,
  isActive: true,
  isLocked: false,
  lockReason: null,
  innerProductUnitId: null,
};

/**
 * Builds a row group. Structural controls (unit, inner, quantity) are disabled for the base row and
 * for locked rows; the base row is also always active.
 */
export function createUnitRow(init: Partial<UnitRowValue> = {}): UnitRowGroup {
  const v: UnitRowValue = { ...DEFAULT_ROW, ...init };
  const structuralLocked = v.isBaseUnit || v.isLocked;
  const nn = { nonNullable: true } as const;
  return new FormGroup({
    id: new FormControl<string | null>(v.id),
    isBaseUnit: new FormControl(v.isBaseUnit, nn),
    unitId: new FormControl({ value: v.unitId, disabled: structuralLocked }, { ...nn, validators: Validators.required }),
    innerUnitId: new FormControl(
      { value: v.innerUnitId, disabled: structuralLocked },
      { ...nn, validators: v.isBaseUnit ? [] : Validators.required },
    ),
    innerQuantity: new FormControl<number | null>(
      { value: v.innerQuantity, disabled: structuralLocked },
      v.isBaseUnit ? [] : [Validators.required, innerQuantityValidator],
    ),
    label: new FormControl(v.label, { ...nn, validators: Validators.maxLength(60) }),
    barcode: new FormControl(v.barcode, { ...nn, validators: [Validators.maxLength(50), Validators.pattern(/^\S*$/)] }),
    isPurchasable: new FormControl(v.isPurchasable, nn),
    isSellable: new FormControl(v.isSellable, nn),
    isDefaultPurchaseUnit: new FormControl(v.isDefaultPurchaseUnit, nn),
    isDefaultSalesUnit: new FormControl(v.isDefaultSalesUnit, nn),
    isActive: new FormControl({ value: v.isBaseUnit ? true : v.isActive, disabled: v.isBaseUnit }, nn),
    isLocked: new FormControl(v.isLocked, nn),
    lockReason: new FormControl<ProductUnitLockReason | null>(v.lockReason),
    innerProductUnitId: new FormControl<string | null>(v.innerProductUnitId),
  });
}

/** The base row of a new product (bound to the product's base unit). */
export function createBaseUnitRow(baseUnitId: string): UnitRowGroup {
  return createUnitRow({ isBaseUnit: true, unitId: baseUnitId, innerQuantity: 1 });
}

/** Row group for a loaded `ProductUnitDto`. */
export function unitRowFromDto(dto: ProductUnit): UnitRowGroup {
  return createUnitRow({
    id: dto.id,
    isBaseUnit: dto.isBaseUnit,
    unitId: dto.unitId,
    innerUnitId: dto.innerUnitId ?? '',
    innerQuantity: dto.innerQuantity,
    label: dto.label ?? '',
    barcode: dto.barcode ?? '',
    isPurchasable: dto.isPurchasable,
    isSellable: dto.isSellable,
    isDefaultPurchaseUnit: dto.isDefaultPurchaseUnit,
    isDefaultSalesUnit: dto.isDefaultSalesUnit,
    isActive: dto.isActive,
    isLocked: dto.isLocked,
    lockReason: dto.lockReason,
    innerProductUnitId: dto.innerProductUnitId,
  });
}

/** Base row first, then the rest in server `sortOrder`. */
export function sortUnitsForEditor(units: readonly ProductUnit[]): ProductUnit[] {
  return [...units].sort((a, b) => Number(b.isBaseUnit) - Number(a.isBaseUnit) || a.sortOrder - b.sortOrder);
}

/** Maps raw row values to the request contract. The base row is sent with no inner and quantity 1. */
export function toProductUnitInputs(rows: readonly UnitRowValue[]): ProductUnitInput[] {
  return rows.map((r) => ({
    id: r.id,
    unitId: r.unitId,
    innerUnitId: r.isBaseUnit ? null : r.innerUnitId || null,
    innerQuantity: r.isBaseUnit ? 1 : r.innerQuantity === null || (r.innerQuantity as unknown) === '' ? null : Number(r.innerQuantity),
    label: r.label.trim() || null,
    barcode: r.barcode.trim() || null,
    isPurchasable: r.isPurchasable,
    isSellable: r.isSellable,
    isDefaultPurchaseUnit: r.isDefaultPurchaseUnit,
    isDefaultSalesUnit: r.isDefaultSalesUnit,
    isActive: r.isBaseUnit ? true : r.isActive,
  }));
}

/**
 * ProblemDetails / FieldError key → reactive-form path:
 * `Units[2].InnerQuantity` → `units.2.innerQuantity`, `BaseUnitId` → `baseUnitId`, `$.units[0].label` → `units.0.label`.
 */
export function problemKeyToFormPath(key: string): string {
  return key
    .replace(/^\$\.?/, '')
    .replace(/\[(\d+)\]/g, '.$1')
    .split('.')
    .filter((s) => s.length > 0)
    .map((s) => s.charAt(0).toLowerCase() + s.slice(1))
    .join('.');
}

/** Row index addressed by a field key such as `Units[2].InnerQuantity`; null for non-row keys. */
export function rowIndexOfField(field: string): number | null {
  const match = /^units\.(\d+)(\.|$)/.exec(problemKeyToFormPath(field));
  return match ? Number(match[1]) : null;
}

const LOCK_REASON_TEXT: Record<ProductUnitLockReason, string> = {
  Transactions: 'Used in purchases, sales or stock adjustments',
  Prices: 'Has selling prices',
  ContainedInLockedUnit: 'Contained in a locked unit',
};

export function lockReasonText(reason: ProductUnitLockReason | null): string {
  return (reason && LOCK_REASON_TEXT[reason]) || 'In use';
}

export interface InnerOption {
  unitId: string;
  name: string;
}

export interface ChainLine {
  index: number;
  name: string;
  factor: number;
  chain: string;
}

export interface PreviewMessage {
  index: number | null;
  text: string;
}

export type PreviewStatus = 'idle' | 'incomplete' | 'pending' | 'ok' | 'failed';

const FACTOR_ERROR_TEXT: Record<NonNullable<FactorResult['error']>, string> = {
  cycle: 'This unit contains itself through its inner units (cycle).',
  orphan: 'Choose what this unit contains; it must lead down to the base unit.',
  invalidQuantity: 'Enter how many inner units it contains.',
};

@Component({
  selector: 'app-product-units-editor',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './product-units-editor.html',
  styleUrl: './product-units-editor.scss',
})
export class ProductUnitsEditor {
  private readonly productsService = inject(ProductsService);
  private readonly destroyRef = inject(DestroyRef);

  /** Owned by the parent form; row 0 is the base row, bound to the product's base unit. */
  readonly units = input.required<FormArray<UnitRowGroup>>();
  /** All units from the master (active and inactive). */
  readonly allUnits = input<UnitItem[]>([]);
  /** Product being edited (null on create); lets the server apply lock state to the preview. */
  readonly productId = input<string | null>(null);

  /** Raw row values, refreshed on every FormArray change. */
  readonly rows = signal<UnitRowValue[]>([]);

  readonly previewStatus = signal<PreviewStatus>('idle');
  readonly preview = signal<UnitPreviewResponse | null>(null);
  private readonly previewRequests = new Subject<UnitPreviewRequest | null>();

  readonly unitById = computed(() => new Map(this.allUnits().map((u) => [u.id, u] as const)));
  readonly baseUnit = computed(() => this.unitById().get(this.rows()[0]?.unitId ?? '') ?? null);

  /** Graph nodes keyed by row index; inner resolved like the server (active row with that UnitId). */
  readonly nodes = computed<ChainNode[]>(() => {
    const rows = this.rows();
    return rows.map((r, i) => ({
      key: String(i),
      innerKey: r.isBaseUnit ? null : this.resolveInnerKey(rows, i),
      innerQuantity: r.isBaseUnit ? 1 : r.innerQuantity === null ? null : Number(r.innerQuantity),
      name: this.displayName(r),
    }));
  });

  readonly factors = computed(() => computeFactors(this.nodes(), '0'));

  /** Unit options per row: active (or the row's current unit), allowed for the base category, not the base, not used by another active row. */
  readonly unitOptions = computed<UnitItem[][]>(() => {
    const rows = this.rows();
    const base = this.baseUnit();
    return rows.map((row, i) => {
      if (row.isBaseUnit) return [];
      const usedElsewhere = new Set(rows.filter((r, j) => j !== i && r.isActive && r.unitId).map((r) => r.unitId));
      return this.allUnits().filter((u) => {
        if (u.id === row.unitId) return true;
        if (!u.isActive || u.id === base?.id || usedElsewhere.has(u.id)) return false;
        return base === null || isCategoryAllowed(base.measurementCategory, u.measurementCategory);
      });
    });
  });

  /** Inner options per row: other active rows with a unit, excluding rows whose chain contains this row. */
  readonly innerOptions = computed<InnerOption[][]>(() => {
    const rows = this.rows();
    const nodes = this.nodes();
    return rows.map((row, i) => {
      if (row.isBaseUnit) return [];
      const outers = outerKeys(nodes, String(i));
      const options: InnerOption[] = [];
      rows.forEach((r, j) => {
        if (j === i || !r.isActive || !r.unitId || outers.has(String(j)) || r.unitId === row.unitId) return;
        if (options.some((o) => o.unitId === r.unitId)) return;
        options.push({ unitId: r.unitId, name: this.displayName(r) });
      });
      return options;
    });
  });

  /** Quantity input step per row, from the inner unit's decimal places. */
  readonly quantitySteps = computed<string[]>(() =>
    this.rows().map((row) => {
      return quantityStep(this.unitById().get(row.innerUnitId)?.decimalPlaces ?? 0);
    }),
  );

  /** Factor to base per row (null when not computable). */
  readonly rowFactors = computed<(number | null)[]>(() => {
    const factors = this.factors();
    return this.rows().map((_, i) => factors.get(String(i))?.factor ?? null);
  });

  /** Client-side mirror of R1–R8b; hints only, the server preview is authoritative. */
  readonly rowIssues = computed<string[][]>(() => {
    const rows = this.rows();
    const factors = this.factors();
    const base = this.baseUnit();
    const byId = this.unitById();
    return rows.map((row, i) => {
      const issues: string[] = [];
      const unit = byId.get(row.unitId);
      if (row.isBaseUnit) {
        if (base?.measurementCategory === 'Packaging') issues.push('A packaging unit cannot be the base unit.');
        return issues;
      }
      if (!row.unitId) return issues;
      if (!row.isActive) return issues;
      if (base && row.unitId === base.id) issues.push('The base unit is already in the list.');
      if (rows.some((r, j) => j !== i && r.isActive && r.unitId === row.unitId)) {
        issues.push('This unit is listed twice; keep only one active row per unit.');
      }
      if (unit && !unit.isActive && !row.isLocked) issues.push(`${unit.name} is inactive.`);

      const result = factors.get(String(i));
      if (result?.error) {
        if (result.error !== 'orphan' || row.innerUnitId) issues.push(FACTOR_ERROR_TEXT[result.error]);
        return issues;
      }
      const inner = rows.find((r, j) => j !== i && r.isActive && r.unitId === row.innerUnitId);
      if (!row.isLocked && row.innerUnitId && !inner) issues.push('The unit it contains is inactive.');
      if (row.isLocked) return issues; // Legacy structure is reported by the server as warnings.

      const innerUnit = byId.get(row.innerUnitId);
      if (base && unit && !isCategoryAllowed(base.measurementCategory, unit.measurementCategory)) {
        issues.push(`A ${unit.measurementCategory} unit cannot be used with a ${base.measurementCategory} base unit.`);
      }
      if (unit && innerUnit && row.innerQuantity !== null) {
        const expected = suggestInnerQuantity(unit, innerUnit);
        if (expected !== null && Number(row.innerQuantity) !== expected) {
          issues.push(`1 ${unit.name} is ${formatChainQuantity(expected)} ${innerUnit.name}.`);
        }
      }
      const factor = result?.factor ?? null;
      if (factor !== null && base) {
        if (!isFactorInRange(factor)) issues.push('This unit is too large or too small relative to the base unit.');
        else if (!isWholeForBase(factor, base.decimalPlaces)) {
          issues.push(
            base.decimalPlaces === 0
              ? `Must equal a whole number of ${base.name} (currently ${formatChainQuantity(factor)}).`
              : `Must equal at most ${base.decimalPlaces} decimals of ${base.name} (currently ${formatChainQuantity(factor)}).`,
          );
        }
      }
      return issues;
    });
  });

  /** Equivalence chains of active rows with a valid factor, smallest factor first. */
  readonly chains = computed<ChainLine[]>(() => {
    const rows = this.rows();
    const nodes = this.nodes();
    const factors = this.factors();
    const lines: ChainLine[] = [];
    rows.forEach((row, i) => {
      const factor = factors.get(String(i))?.factor ?? null;
      if (!row.isActive || !row.unitId || factor === null) return;
      const chain = equivalenceChain(nodes, String(i));
      if (chain) lines.push({ index: i, name: nodes[i].name, factor, chain });
    });
    return lines.sort((a, b) => a.factor - b.factor || a.index - b.index);
  });

  readonly previewErrors = computed<PreviewMessage[]>(() => this.toMessages(this.preview()?.errors ?? []));
  readonly previewWarnings = computed<PreviewMessage[]>(() => this.toMessages(this.preview()?.warnings ?? []));

  /** Server preview messages per row index. */
  readonly rowPreviewErrors = computed<string[][]>(() => {
    const perRow = this.rows().map(() => [] as string[]);
    for (const e of this.preview()?.errors ?? []) {
      const index = rowIndexOfField(e.field);
      if (index !== null && index < perRow.length) perRow[index].push(e.message);
    }
    return perRow;
  });

  /** Rows another active row uses as its inner cannot be removed. */
  readonly removable = computed<boolean[]>(() => {
    const nodes = this.nodes();
    const rows = this.rows();
    return rows.map((row, i) => {
      if (row.isBaseUnit || row.isLocked) return false;
      return !nodes.some((n, j) => j !== i && rows[j].isActive && n.innerKey === String(i));
    });
  });

  constructor() {
    effect((onCleanup) => {
      const array = this.units();
      // untracked: onRowsChanged reads and writes signals; only the `units` input must re-run this effect.
      const sub = array.valueChanges.pipe(startWith(null)).subscribe(() => untracked(() => this.onRowsChanged(array)));
      onCleanup(() => sub.unsubscribe());
    });

    this.previewRequests
      .pipe(
        // Every change cancels both the pending debounce and any in-flight request, so a response can
        // never be applied to rows that changed (or shifted index) after it was requested.
        switchMap((request) =>
          timer(PREVIEW_DEBOUNCE_MS).pipe(
            switchMap(() => {
              if (request === null) return of({ status: 'incomplete' as const, response: null });
              return this.productsService.previewUnits(request).pipe(
                map((response) => ({ status: 'ok' as const, response })),
                catchError(() => of({ status: 'failed' as const, response: null })),
              );
            }),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(({ status, response }) => {
        this.previewStatus.set(status);
        // On failure the previous result is stale: clear it rather than show outdated errors.
        this.preview.set(response);
      });
  }

  private onRowsChanged(array: FormArray<UnitRowGroup>): void {
    const rows = array.getRawValue();
    // A row added or removed shifts `Units[i]` indices: the last server result no longer lines up.
    if (rows.length !== this.rows().length) this.preview.set(null);
    this.rows.set(rows);
    const request = this.buildPreviewRequest(rows);
    if (request !== null) this.previewStatus.set('pending');
    this.previewRequests.next(request);
  }

  /** Null while the configuration is incomplete (no base unit, or a row without unit / inner). */
  buildPreviewRequest(rows: readonly UnitRowValue[]): UnitPreviewRequest | null {
    if (rows.length === 0 || !rows[0].unitId) return null;
    if (rows.some((r) => !r.unitId || (!r.isBaseUnit && !r.innerUnitId))) return null;
    return { productId: this.productId(), baseUnitId: rows[0].unitId, units: toProductUnitInputs(rows) };
  }

  displayName(row: Pick<UnitRowValue, 'label' | 'unitId'>): string {
    return row.label.trim() || this.unitById().get(row.unitId)?.name || 'New unit';
  }

  readonly reasonText = lockReasonText;

  unitName(unitId: string): string {
    const unit = this.unitById().get(unitId);
    return unit ? `${unit.name} (${unit.shortCode})${unit.isActive ? '' : ' — inactive'}` : '—';
  }

  /** Inner row display name for read-only (base / locked) rows. */
  innerName(index: number): string {
    const key = this.nodes()[index]?.innerKey;
    return key === null || key === undefined ? '—' : (this.nodes()[Number(key)]?.name ?? '—');
  }

  formatFactor(value: number): string {
    return formatChainQuantity(value);
  }

  addRow(): void {
    const base = this.rows()[0];
    this.units().push(createUnitRow({ innerUnitId: base?.unitId ?? '' }));
    this.units().markAsDirty();
  }

  removeRow(index: number): void {
    if (!this.removable()[index]) return;
    this.units().removeAt(index);
    this.units().markAsDirty();
  }

  /** Sets the row's unit and suggests the inner quantity from standard factors. */
  selectUnit(index: number, unitId: string): void {
    const row = this.units().at(index);
    row.controls.unitId.setValue(unitId);
    this.applySuggestion(index);
    row.markAsDirty();
  }

  /** Sets what the row contains and suggests the inner quantity from standard factors. */
  selectInner(index: number, innerUnitId: string): void {
    const row = this.units().at(index);
    row.controls.innerUnitId.setValue(innerUnitId);
    this.applySuggestion(index);
    row.markAsDirty();
  }

  private applySuggestion(index: number): void {
    const row = this.units().at(index);
    if (row.controls.innerQuantity.disabled) return;
    const unit = this.unitById().get(row.controls.unitId.value);
    const inner = this.unitById().get(row.controls.innerUnitId.value);
    if (!unit || !inner) return;
    const suggested = suggestInnerQuantity(unit, inner);
    if (suggested !== null) row.controls.innerQuantity.setValue(suggested);
  }

  /** Sets a flag and clears default flags that no longer apply (inactive / not purchasable / not sellable). */
  setFlag(index: number, flag: 'isActive' | 'isPurchasable' | 'isSellable', value: boolean): void {
    const row = this.units().at(index);
    if (row.controls[flag].disabled) return;
    row.controls[flag].setValue(value);
    const v = row.getRawValue();
    if (row.controls.isDefaultPurchaseUnit.value && (!v.isActive || !v.isPurchasable)) {
      row.controls.isDefaultPurchaseUnit.setValue(false);
    }
    if (row.controls.isDefaultSalesUnit.value && (!v.isActive || !v.isSellable)) {
      row.controls.isDefaultSalesUnit.setValue(false);
    }
    row.markAsDirty();
  }

  /** Single-select default purchase / sales unit; unchecking leaves no default. */
  setDefault(index: number, kind: 'purchase' | 'sales', checked: boolean): void {
    const key = kind === 'purchase' ? 'isDefaultPurchaseUnit' : 'isDefaultSalesUnit';
    this.units().controls.forEach((row, i) => {
      const value = i === index ? checked : false;
      if (row.controls[key].value !== value) row.controls[key].setValue(value);
    });
    this.units().markAsDirty();
  }

  /** True when the row can be the default purchase / sales unit. */
  canBeDefault(index: number, kind: 'purchase' | 'sales'): boolean {
    const row = this.rows()[index];
    if (!row || !row.isActive) return false;
    return kind === 'purchase' ? row.isPurchasable : row.isSellable;
  }

  /**
   * Locked rows are immutable: deactivate the row and append an unlocked, editable copy
   * (same unit, inner, quantity, label, barcode and flags; defaults move to the copy).
   */
  replaceRow(index: number): void {
    const row = this.units().at(index);
    const v = row.getRawValue();
    if (!v.isLocked || v.isBaseUnit || !v.isActive) return;
    row.patchValue({ isActive: false, isDefaultPurchaseUnit: false, isDefaultSalesUnit: false });
    this.units().push(
      createUnitRow({
        unitId: v.unitId,
        innerUnitId: v.innerUnitId,
        innerQuantity: v.innerQuantity,
        label: v.label,
        barcode: v.barcode,
        isPurchasable: v.isPurchasable,
        isSellable: v.isSellable,
        isDefaultPurchaseUnit: v.isDefaultPurchaseUnit,
        isDefaultSalesUnit: v.isDefaultSalesUnit,
      }),
    );
    this.units().markAsDirty();
  }

  /** First error message for a row control: server error first, then client validators once touched. */
  controlError(index: number, name: keyof UnitRowGroup['controls']): string | null {
    const control = this.units().at(index)?.controls[name] as AbstractControl | undefined;
    const errors = control?.errors;
    if (!control || !errors) return null;
    if (errors['server']) return errors['server'] as string;
    if (!control.touched) return null;
    if (errors['required']) return name === 'innerUnitId' ? 'Choose what it contains.' : 'Required.';
    if (errors['innerQuantity']) return `Enter a quantity above 0 with at most ${INNER_QUANTITY_DECIMALS} decimals.`;
    if (errors['maxlength']) return `At most ${errors['maxlength'].requiredLength} characters.`;
    if (errors['pattern']) return 'No spaces allowed.';
    return null;
  }

  /** Server error set on a whole row (`Units[2]`). */
  rowError(index: number): string | null {
    return (this.units().at(index)?.errors?.['server'] as string | undefined) ?? null;
  }

  /** Server error set on the collection (`Units`). */
  arrayError(): string | null {
    return (this.units().errors?.['server'] as string | undefined) ?? null;
  }

  private toMessages(errors: readonly FieldError[]): PreviewMessage[] {
    const rows = this.rows();
    return errors.map((e) => {
      const index = rowIndexOfField(e.field);
      const row = index !== null ? rows[index] : undefined;
      const prefix = row ? `${this.displayName(row)}: ` : '';
      return { index, text: prefix + e.message };
    });
  }

  /** Server rule: a locked row keeps its stored inner; otherwise the active row with that UnitId. */
  private resolveInnerKey(rows: readonly UnitRowValue[], index: number): string | null {
    const row = rows[index];
    if (row.isLocked && row.innerProductUnitId) {
      const stored = rows.findIndex((r) => r.id === row.innerProductUnitId);
      if (stored >= 0) return String(stored);
    }
    if (!row.innerUnitId) return null;
    const found = rows.findIndex((r, j) => j !== index && r.isActive && r.unitId === row.innerUnitId);
    return found >= 0 ? String(found) : `missing:${index}`;
  }
}
