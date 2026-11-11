import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from '../../../../core/auth/auth.service';
import { ContextService } from '../../../../core/context/context.service';
import { LocationSelect } from '../../../../shared/locations/location-select';
import { StockLocationsService } from '../../../../shared/locations/stock-locations.service';
import { splitLocationError } from '../../../../shared/locations/location-errors';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { quantityStep, quantityValidators } from '../../../../shared/forms/quantity-input';
import { convert, costPerBaseUnit, formatQuantity, round10 } from '../../../../shared/units/unit-conversion';
import { StockAdjustmentsService } from '../stock-adjustments.service';
import { StockService } from '../../stock/stock.service';
import type { StockDetail } from '../../stock/stock.models';
import { ProductsService } from '../../products/products.service';
import type { ProductUnit } from '../../products/products.models';
import type { AdjustmentDirection, AdjustmentType } from '../stock-adjustments.models';
import { IdempotentSubmission } from '../../../../core/idempotency';
import type { ApiProblem } from '../../../../core/auth/auth.models';

const REQUIRED_DIRECTION: Record<AdjustmentType, AdjustmentDirection | null> = {
  Damage: 'Decrease',
  Loss: 'Decrease',
  Expiry: 'Decrease',
  OpeningStock: 'Increase',
  Correction: null,
};

export interface AdjustableProduct {
  id: string;
  name: string;
  sku: string;
  isActive: boolean;
}

const money = (n: number, maxDecimals = 2) =>
  n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: maxDecimals });

@Component({
  selector: 'app-stock-adjustment-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton, LocationSelect],
  templateUrl: './stock-adjustment-form.html',
  styleUrl: './stock-adjustment-form.scss',
})
export class StockAdjustmentForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  // One key per adjustment, so a double-click can't post it twice.
  private readonly submission = new IdempotentSubmission();
  private readonly adjustmentsService = inject(StockAdjustmentsService);
  private readonly stockService = inject(StockService);
  private readonly productsService = inject(ProductsService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  private readonly context = inject(ContextService);
  private readonly stockLocations = inject(StockLocationsService);

  readonly types: AdjustmentType[] = ['Damage', 'Loss', 'Correction', 'Expiry', 'OpeningStock'];
  readonly products = signal<AdjustableProduct[]>([]);
  /** Active units of the selected product, in API order (base first). */
  readonly units = signal<ProductUnit[]>([]);
  readonly currentQty = signal<number | null>(null);
  /** True when the default-store preview cannot be resolved because no working/primary branch is known. */
  readonly needsBranch = signal(false);
  /** Fallback base label from the stock detail, used until the product's units have loaded. */
  readonly baseUnitLabel = signal('');

  readonly form = this.fb.nonNullable.group({
    productId: ['', Validators.required],
    productUnitId: ['', Validators.required],
    type: ['Correction' as AdjustmentType, Validators.required],
    direction: ['Decrease' as AdjustmentDirection, Validators.required],
    quantity: [null as number | null, quantityValidators(0)],
    unitCost: [null as number | null, Validators.min(0)],
    reason: [''],
  });

  private readonly formValue = toSignal(this.form.valueChanges, { initialValue: this.form.value });

  readonly requiredDirection = computed(() => REQUIRED_DIRECTION[this.formValue().type ?? 'Correction']);
  /** `form.value` omits the disabled (type-locked) direction control, so derive it here. */
  readonly direction = computed<AdjustmentDirection>(() => this.requiredDirection() ?? this.formValue().direction ?? 'Decrease');
  readonly isIncrease = computed(() => this.direction() === 'Increase');

  readonly baseUnit = computed(() => this.units().find((u) => u.isBaseUnit) ?? null);
  readonly baseCode = computed(() => this.baseUnit()?.unitCode ?? this.baseUnitLabel());
  readonly baseDecimals = computed(() => this.baseUnit()?.decimalPlaces ?? 3);
  readonly selectedUnit = computed(() => {
    const id = this.formValue().productUnitId;
    return this.units().find((u) => u.id === id) ?? null;
  });
  readonly selectedDecimals = computed(() => this.selectedUnit()?.decimalPlaces ?? 0);
  readonly step = computed(() => quantityStep(this.selectedDecimals()));
  readonly quantityLabel = computed(() => this.selectedUnit()?.displayName ?? (this.baseUnitLabel() || 'base unit'));

  /** Entered quantity converted to base units; null when nothing valid is entered. */
  readonly baseQty = computed(() => {
    const qty = this.formValue().quantity;
    const unit = this.selectedUnit();
    if (qty == null || !unit || !(qty > 0)) return null;
    return convert(qty, unit.conversionFactorToBase, 1);
  });

  /** "= 300 pc" — shown only when the entered unit is not the base. */
  readonly basePreview = computed(() => {
    const unit = this.selectedUnit();
    const base = this.baseQty();
    if (!unit || base === null || unit.conversionFactorToBase === 1) return null;
    return `= ${formatQuantity(base, this.baseDecimals())} ${this.baseCode()}`;
  });

  readonly costLabel = computed(() => `Unit cost per ${this.selectedUnit()?.displayName ?? this.quantityLabel()} (₹)`);
  readonly costHint = computed(() => {
    const unit = this.selectedUnit();
    const cost = this.formValue().unitCost;
    if (!unit || cost == null || !(cost >= 0) || unit.conversionFactorToBase === 1) return null;
    return `= ₹${money(costPerBaseUnit(cost, unit.conversionFactorToBase), 4)} per ${this.baseCode()}`;
  });

  readonly resultingQty = computed(() => {
    const current = this.currentQty();
    const base = this.baseQty();
    if (current == null || base == null) return null;
    return this.isIncrease()
      ? round10(current + base)
      : round10(current - base);
  });

  readonly saving = signal(false);
  /** Adjusting location (null = server default). The picker shows only for StockLocations.View holders with > 1 eligible location. */
  readonly locationId = signal<string | null>(null);
  readonly locationError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }
  readonly serverError = signal<string | null>(null);

  readonly fmtBase = (n: number) => formatQuantity(n, this.baseDecimals());

  // Reactive forms ignore a template [disabled] binding on a formControlName element and keep
  // resyncing it to the control's own enable/disable state — so the lock has to be applied here.
  private readonly directionLockEffect = effect(() => {
    const required = this.requiredDirection();
    const direction = this.form.controls.direction;
    if (required) {
      if (direction.value !== required) direction.setValue(required, { emitEvent: false });
      if (direction.enabled) direction.disable({ emitEvent: false });
    } else if (direction.disabled) {
      direction.enable({ emitEvent: false });
    }
  });

  constructor() {
    // Quantity validators follow the selected unit's decimal places (1.5 Box is invalid when Box has 0 dp).
    this.form.controls.productUnitId.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.applyUnitValidators());
    // The before/after preview is per location, so a different location reloads the current quantity.
    effect(() => {
      const location = this.locationId();
      const productId = untracked(() => this.form.controls.productId.value);
      this.locationError.set(null);
      if (productId) untracked(() => this.loadCurrentQty(productId, location));
    });
  }

  ngOnInit(): void {
    const preselect = this.route.snapshot.queryParamMap.get('productId');
    this.productsService.list().subscribe({
      next: (res) => {
        // Inactive products stay adjustable (lead decision); the picker lists active ones and keeps a
        // preselected inactive product so the stock-detail "Adjust stock" link always works.
        this.products.set(
          res.items
            .filter((p) => p.isActive || p.id === preselect)
            .map((p) => ({ id: p.id, name: p.name, sku: p.sku, isActive: p.isActive })),
        );
        if (preselect) {
          this.form.controls.productId.setValue(preselect);
          this.onProductChange(preselect);
        }
      },
    });
  }

  onProductChange(productId: string): void {
    this.units.set([]);
    this.currentQty.set(null);
    this.form.controls.productUnitId.setValue('');
    if (!productId) return;

    this.productsService.get(productId).subscribe({
      next: (p) => {
        if (this.form.controls.productId.value !== productId) return;
        // A preselected product outside the first list page still needs a picker entry.
        if (!this.products().some((x) => x.id === p.id)) {
          this.products.update((list) => [...list, { id: p.id, name: p.name, sku: p.sku, isActive: p.isActive }]);
        }
        const active = p.units.filter((u) => u.isActive);
        this.units.set(active);
        const base = active.find((u) => u.isBaseUnit) ?? active[0];
        this.form.controls.productUnitId.setValue(base?.id ?? '');
      },
    });
    this.loadCurrentQty(productId, this.locationId());
  }

  private loadCurrentQty(productId: string, locationId: string | null): void {
    this.stockService.detail(productId, locationId).subscribe({
      next: (d) => {
        if (this.form.controls.productId.value !== productId || this.locationId() !== locationId) return;
        this.baseUnitLabel.set(d.baseUnitLabel);
        if (locationId) {
          this.currentQty.set(d.quantityOnHand);
          return;
        }
        // No explicit location: the server posts to the working branch's store, so preview that store, not the visible total.
        this.currentQty.set(null);
        this.needsBranch.set(false);
        this.resolveDefaultStoreQty(d, (qty) => {
          if (this.form.controls.productId.value !== productId || this.locationId() !== null) return;
          this.currentQty.set(qty);
          this.needsBranch.set(qty === null);
        });
      },
    });
  }

  /** Quantity at the working branch's store from the stock detail's `locations[]` (falls back to the total when the server sends none). */
  private resolveDefaultStoreQty(d: StockDetail, done: (qty: number | null) => void): void {
    if (d.locations == null) {
      done(d.quantityOnHand);
      return;
    }
    // D13: the working branch (the route requires one); defensively the primary branch; otherwise no preview rather than a guess.
    const branchId = this.context.workingBranch()?.id ?? this.context.primaryBranchId();
    if (!branchId) {
      done(null);
      return;
    }
    // The balance DTO carries no branch, so the branch's store can only be identified via the location list (StockLocations.View).
    // Fail closed: without it, or when the lookup fails or finds no store, there is no preview (needsBranch / no-preview hint).
    if (!this.auth.hasPermission('StockLocations.View')) {
      done(null);
      return;
    }
    this.stockLocations.list({ branchId, type: 'BranchStore' }).subscribe({
      next: (all) => {
        const store = all.find((l) => l.isActive && l.locationType === 'BranchStore' && l.branchId === branchId);
        done(store ? (d.locations!.find((s) => s.type === 'BranchStore' && s.locationId === store.id)?.qty ?? 0) : null);
      },
      error: () => done(null),
    });
  }

  private applyUnitValidators(): void {
    const quantity = this.form.controls.quantity;
    quantity.setValidators(quantityValidators(this.selectedUnitFor(this.form.controls.productUnitId.value)?.decimalPlaces ?? 0));
    quantity.updateValueAndValidity();
  }

  private selectedUnitFor(id: string): ProductUnit | null {
    return this.units().find((u) => u.id === id) ?? null;
  }

  quantityError(): string | null {
    const c = this.form.controls.quantity;
    const errors = c.errors;
    if (!errors) return null;
    if (errors['server']) return errors['server'];
    if (!c.touched && !c.dirty) return null;
    if (errors['maxDecimalPlaces']) {
      const max = errors['maxDecimalPlaces'].max as number;
      return max === 0
        ? `${this.quantityLabel()} takes whole numbers only.`
        : `${this.quantityLabel()} allows at most ${max} decimal place${max === 1 ? '' : 's'}.`;
    }
    if (errors['required'] || errors['min']) return 'Enter a quantity greater than zero.';
    return null;
  }

  async submit(): Promise<void> {
    this.serverError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const unitCost = this.form.controls.unitCost.value;
    if (this.isIncrease() && (unitCost === null || unitCost < 0)) {
      this.form.controls.unitCost.setErrors({ required: true });
      this.form.controls.unitCost.markAsTouched();
      return;
    }

    const v = this.form.getRawValue();
    // The type-locked direction is applied by an effect; read the derived value so it is never stale.
    const direction = this.direction();
    const unit = this.selectedUnit();
    const productName = this.products().find((p) => p.id === v.productId)?.name ?? 'this item';
    const entered = `${formatQuantity(v.quantity!, unit?.decimalPlaces ?? 0)} ${unit?.unitCode ?? ''}`.trim();
    const current = this.currentQty();
    const resulting = this.resultingQty();
    const movement = current !== null && resulting !== null ? `${this.fmtBase(current)} → ${this.fmtBase(resulting)} ${this.baseCode()}` : '';
    const confirmed = await this.confirm.ask({
      title: `Post this adjustment?`,
      body: `${productName}: ${direction === 'Increase' ? '+' : '−'}${entered}${this.basePreview() ? ` (${this.basePreview()})` : ''}${movement ? `, ${movement}` : ''}. This is a permanent, traceable stock entry.`,
      confirmLabel: 'Post adjustment',
      destructive: direction === 'Decrease',
    });
    if (!confirmed) return;

    this.saving.set(true);
    const body = {
          productId: v.productId,
          productUnitId: v.productUnitId,
          type: v.type,
          direction,
          quantity: v.quantity!,
          unitCost: direction === 'Increase' ? v.unitCost : null,
          reason: v.reason || null,
          stockLocationId: this.locationId(),
};
    this.adjustmentsService
      .create(body, this.submission.keyFor(body))
      .subscribe({
        next: (result) => {
          this.saving.set(false);
          this.saved = true;
          const fmt = (n: number) => formatQuantity(n, result.baseDecimalPlaces);
          this.toast.show(`${result.adjustmentNo} posted · ${result.productName}: ${fmt(result.beforeQty)} → ${fmt(result.afterQty)} ${result.baseUnitCode}.`);
          this.router.navigate(['/inventory/stock', result.productId]);
        },
        error: (err: HttpErrorResponse) => {
          this.saving.set(false);
          const { locationError, rest: problem } = splitLocationError(err.error as ApiProblem | undefined);
          this.locationError.set(locationError);
          if (locationError && Object.keys(problem?.errors ?? {}).length === 0) return;
          if (problem?.errors) {
            let matched = false;
            for (const [field, messages] of Object.entries(problem.errors)) {
              const control = this.form.get(field.charAt(0).toLowerCase() + field.slice(1));
              if (control) {
                control.setErrors({ server: messages[0] });
                control.markAsTouched();
                matched = true;
              }
            }
            if (matched) return;
          }
          this.serverError.set(problem?.title ?? Object.values(problem?.errors ?? {})[0]?.[0] ?? 'Something went wrong.');
        },
      });
  }
}
