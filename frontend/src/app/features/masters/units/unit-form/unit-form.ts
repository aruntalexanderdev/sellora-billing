import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { UnitsService } from '../units.service';
import {
  MEASUREMENT_CATEGORIES,
  REFERENCE_UNIT,
  isMeasurableCategory,
  type MeasurementCategory,
  type UnitItem,
  type UpsertUnitRequest,
} from '../units.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

/** Decimal places allowed for measurable (Weight/Volume/Length) units; mirrors backend 0–3. */
export const UNIT_DECIMAL_OPTIONS: readonly number[] = [0, 1, 2, 3];
/** Default decimal places for a new measurable unit. */
export const DEFAULT_MEASURABLE_DECIMALS = 3;

/** Optional, but when present must be strictly greater than zero. */
function positiveOrEmpty(control: AbstractControl<number | null>): ValidationErrors | null {
  const value = control.value;
  if (value === null || value === undefined || (value as unknown) === '') return null;
  return Number(value) > 0 ? null : { positive: true };
}

@Component({
  selector: 'app-unit-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './unit-form.html',
  styleUrl: '../../../administration/users/user-form/user-form.scss',
})
export class UnitForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly unitsService = inject(UnitsService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly categories = MEASUREMENT_CATEGORIES;
  readonly decimalOptions = UNIT_DECIMAL_OPTIONS;
  readonly unitId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.unitId !== null && this.unitId !== 'new');

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(60)]],
    shortCode: ['', [Validators.required, Validators.maxLength(10)]],
    measurementCategory: ['Count' as MeasurementCategory, Validators.required],
    decimalPlaces: [0, [Validators.required, Validators.min(0), Validators.max(3)]],
    standardFactor: [null as number | null, positiveOrEmpty],
  });

  /** True when the loaded unit is a base unit of some product: category/decimals/factor are locked. */
  readonly isInUse = signal(false);
  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  private readonly category = toSignal(this.form.controls.measurementCategory.valueChanges, {
    initialValue: this.form.controls.measurementCategory.value,
  });
  private readonly shortCode = toSignal(this.form.controls.shortCode.valueChanges, {
    initialValue: this.form.controls.shortCode.value,
  });

  /** Whether the standard-factor field is shown (Weight/Volume/Length only). */
  readonly isMeasurable = computed(() => isMeasurableCategory(this.category()));
  /** Reference unit for the standard-factor hint, e.g. 'kg'; null for non-measurable categories. */
  readonly referenceUnit = computed(() => {
    const c = this.category();
    return isMeasurableCategory(c) ? REFERENCE_UNIT[c] : null;
  });
  readonly unitLabel = computed(() => this.shortCode().trim() || 'unit');

  constructor() {
    this.applyCategoryRules(this.form.controls.measurementCategory.value, null);
    let previous = this.form.controls.measurementCategory.value;
    this.form.controls.measurementCategory.valueChanges.pipe(takeUntilDestroyed()).subscribe((category) => {
      this.applyCategoryRules(category, previous);
      previous = category;
    });
  }

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    if (this.isEdit()) {
      this.unitsService.get(this.unitId!).subscribe({
        next: (unit) => this.loadUnit(unit),
        error: (err: HttpErrorResponse) =>
          this.serverError.set(err.status === 404 ? 'Unit not found.' : 'Could not load this unit.'),
      });
    }
  }

  /** Populates the form from a loaded unit and applies category + in-use locking. */
  loadUnit(unit: UnitItem): void {
    // isInUse must be set first: patching the category re-runs applyCategoryRules, which applies the lock.
    this.isInUse.set(unit.isInUse);
    // Key order matters: category is patched before decimals/factor so the loaded values win over rule defaults.
    this.form.patchValue({
      name: unit.name,
      shortCode: unit.shortCode,
      measurementCategory: unit.measurementCategory,
      decimalPlaces: unit.decimalPlaces,
      standardFactor: unit.standardFactor,
    });
    this.form.markAsPristine();
  }

  /**
   * Enforces the category rules on decimals and standard factor (mirrors the backend):
   * - Count / Packaging: decimalPlaces 0 and standardFactor null, both disabled.
   * - Weight / Volume / Length: both enabled; a new unit coming from a non-measurable
   *   category defaults to 3 decimals.
   * - In-use units: category, decimals and factor are disabled regardless.
   *
   * `previous` is the category before the change (null on initial setup / load).
   */
  applyCategoryRules(category: MeasurementCategory, previous: MeasurementCategory | null): void {
    const { measurementCategory, decimalPlaces, standardFactor } = this.form.controls;
    const opts = { emitEvent: false };

    if (!isMeasurableCategory(category)) {
      decimalPlaces.setValue(0, opts);
      standardFactor.setValue(null, opts);
      decimalPlaces.disable(opts);
      standardFactor.disable(opts);
    } else {
      decimalPlaces.enable(opts);
      standardFactor.enable(opts);
      if (!this.isEdit() && previous !== null && !isMeasurableCategory(previous)) {
        decimalPlaces.setValue(DEFAULT_MEASURABLE_DECIMALS, opts);
      }
    }

    if (this.isInUse()) {
      measurementCategory.disable(opts);
      decimalPlaces.disable(opts);
      standardFactor.disable(opts);
    } else {
      measurementCategory.enable(opts);
    }
  }

  /** Builds the request from raw form values; standardFactor is null for non-measurable categories. */
  buildRequest(): UpsertUnitRequest {
    const raw = this.form.getRawValue();
    const measurable = isMeasurableCategory(raw.measurementCategory);
    const factor = raw.standardFactor;
    return {
      name: raw.name.trim(),
      shortCode: raw.shortCode.trim(),
      measurementCategory: raw.measurementCategory,
      decimalPlaces: measurable ? Number(raw.decimalPlaces) : 0,
      standardFactor: measurable && factor !== null && (factor as unknown) !== '' ? Number(factor) : null,
    };
  }

  submit(): void {
    this.serverError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    const request = this.buildRequest();
    const request$ = this.isEdit()
      ? this.unitsService.update(this.unitId!, request)
      : this.unitsService.create(request);

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(this.isEdit() ? 'Unit updated.' : 'Unit created.');
        this.router.navigateByUrl('/administration/units');
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.applyServerErrors(err.error as ApiProblem | undefined);
      },
    });
  }

  /** Maps ProblemDetails `errors` (PascalCase or camelCase keys) onto controls; the rest go to `serverError`. */
  applyServerErrors(problem: ApiProblem | undefined): void {
    const unmatched: string[] = [];
    for (const [field, messages] of Object.entries(problem?.errors ?? {})) {
      const control = this.form.get(field.charAt(0).toLowerCase() + field.slice(1));
      if (control && messages?.length) {
        control.setErrors({ server: messages[0] });
        control.markAsTouched();
      } else if (messages?.length) {
        unmatched.push(messages[0]);
      }
    }
    if (unmatched.length > 0) {
      this.serverError.set(unmatched[0]);
    } else if (!problem?.errors || Object.keys(problem.errors).length === 0) {
      this.serverError.set(problem?.title ?? 'Something went wrong.');
    }
  }
}
