import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { applyProblem, toProblem } from '../../core/http/api-errors';
import { UiButton } from '../../shared/ui/button/button';
import { UiDataTable } from '../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../shared/ui/toast/toast.service';
import { formatStandardFactor, statusAction } from '../masters/units/unit-list/unit-list';
import { MEASUREMENT_CATEGORIES, REFERENCE_UNIT, isMeasurableCategory, type MeasurementCategory } from '../masters/units/units.models';
import type { UnitItem, UpsertUnitRequest } from './platform.models';
import { PlatformService } from './platform.service';

const COLS: DataTableColumn[] = [
  { label: 'Name', align: 'left' },
  { label: 'Code', align: 'left' },
  { label: 'Category', align: 'left' },
  { label: 'Decimals', align: 'right' },
  { label: 'Standard factor', align: 'right' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

/** Request from raw form values: non-measurable units are always 0 decimals with no standard factor. */
export function buildUnitRequest(raw: {
  name: string; shortCode: string; measurementCategory: MeasurementCategory; decimalPlaces: number; standardFactor: number | null;
}): UpsertUnitRequest {
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

/** Platform-managed units (D12/O7): create, edit and activate/deactivate live only here. */
@Component({
  selector: 'app-platform-units',
  standalone: true,
  imports: [ReactiveFormsModule, UiButton, UiDataTable],
  template: `
    <div class="page-head">
      <div>
        <h1>Units</h1>
        <p>Shared units of measure for every company. A unit used by products is locked (category, decimals, factor).</p>
      </div>
      @if (!editing()) {
        <ui-button variant="primary" (onClick)="startCreate()">+ New unit</ui-button>
      }
    </div>

    @if (editing()) {
      <form class="panel" [formGroup]="form" (ngSubmit)="save()" [attr.aria-label]="editId() ? 'Edit unit' : 'New unit'">
        <h3>{{ editId() ? 'Edit unit' : 'New unit' }}</h3>
        @if (banner()) { <div class="server-error" role="alert">{{ banner() }}</div> }
        <div class="grid-2">
          <div>
            <label class="field-label" for="un-name">Name</label>
            <input id="un-name" class="field-input" formControlName="name" maxlength="60" />
            @if (form.controls.name.errors?.['server']) { <div class="field-error">{{ form.controls.name.errors?.['server'] }}</div> }
          </div>
          <div>
            <label class="field-label" for="un-code">Code</label>
            <input id="un-code" class="field-input" formControlName="shortCode" maxlength="10" />
            @if (form.controls.shortCode.errors?.['server']) { <div class="field-error">{{ form.controls.shortCode.errors?.['server'] }}</div> }
          </div>
          <div>
            <label class="field-label" for="un-cat">Category</label>
            <select id="un-cat" class="field-input" formControlName="measurementCategory" (change)="onCategory()">
              @for (c of categories; track c) { <option [value]="c">{{ c }}</option> }
            </select>
            @if (form.controls.measurementCategory.errors?.['server']) { <div class="field-error">{{ form.controls.measurementCategory.errors?.['server'] }}</div> }
          </div>
          <div>
            <label class="field-label" for="un-dec">Decimal places</label>
            <select id="un-dec" class="field-input" formControlName="decimalPlaces">
              @for (d of [0, 1, 2, 3]; track d) { <option [ngValue]="d">{{ d }}</option> }
            </select>
            @if (form.controls.decimalPlaces.errors?.['server']) { <div class="field-error">{{ form.controls.decimalPlaces.errors?.['server'] }}</div> }
          </div>
          @if (measurable()) {
            <div>
              <label class="field-label" for="un-factor">Standard factor{{ referenceUnit() ? ' (' + referenceUnit() + ')' : '' }}</label>
              <input id="un-factor" type="number" step="any" class="field-input" formControlName="standardFactor" />
              @if (form.controls.standardFactor.errors?.['server']) { <div class="field-error">{{ form.controls.standardFactor.errors?.['server'] }}</div> }
            </div>
          }
        </div>
        <div class="actions">
          <ui-button type="submit" variant="primary" [disabled]="saving()">{{ saving() ? 'Saving…' : 'Save unit' }}</ui-button>
          <ui-button (onClick)="cancel()">Cancel</ui-button>
        </div>
      </form>
    }

    <ui-data-table [cols]="cols" [rows]="rows()" [state]="state()" emptyTitle="No units yet" emptyBody="Add the units products are measured in." (retry)="load()" />
  `,
  styleUrl: './platform.scss',
})
export class PlatformUnits implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(PlatformService);
  private readonly toast = inject(ToastService);

  readonly cols = COLS;
  readonly categories = MEASUREMENT_CATEGORIES;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<UnitItem[]>([]);
  readonly editing = signal(false);
  readonly editId = signal<string | null>(null);
  readonly saving = signal(false);
  readonly banner = signal<string | null>(null);
  readonly category = signal<MeasurementCategory>('Count');

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(60)]],
    shortCode: ['', [Validators.required, Validators.maxLength(10)]],
    measurementCategory: ['Count' as MeasurementCategory, Validators.required],
    decimalPlaces: [0, [Validators.required, Validators.min(0), Validators.max(3)]],
    standardFactor: [null as number | null],
  });

  readonly measurable = computed(() => isMeasurableCategory(this.category()));
  readonly referenceUnit = computed(() => {
    const c = this.category();
    return isMeasurableCategory(c) ? REFERENCE_UNIT[c] : null;
  });

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((u) => ({
      cells: [
        { kind: 'text', text: u.name, weight: '600' },
        { kind: 'text', text: u.shortCode, color: 'var(--color-text-muted)' },
        { kind: 'text', text: u.measurementCategory, color: 'var(--color-text-muted)' },
        { kind: 'text', text: String(u.decimalPlaces), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: formatStandardFactor(u), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'badge', text: u.isActive ? 'Active' : 'Inactive', tone: u.isActive ? 'good' : 'mute' },
        { kind: 'actions', actions: [{ label: 'Edit', onClick: () => this.startEdit(u) }, statusAction(u, () => this.toggle(u))] },
      ],
    })),
  );

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.state.set('loading');
    this.service.units().subscribe({
      next: (items) => {
        this.items.set(items);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  startCreate(): void {
    this.editId.set(null);
    this.form.enable();
    this.form.reset({ name: '', shortCode: '', measurementCategory: 'Count', decimalPlaces: 0, standardFactor: null });
    this.category.set('Count');
    this.applyRules(false);
    this.banner.set(null);
    this.editing.set(true);
  }

  startEdit(u: UnitItem): void {
    this.editId.set(u.id);
    this.form.enable();
    this.form.reset({
      name: u.name, shortCode: u.shortCode, measurementCategory: u.measurementCategory, decimalPlaces: u.decimalPlaces, standardFactor: u.standardFactor,
    });
    this.category.set(u.measurementCategory);
    this.applyRules(u.isInUse);
    this.banner.set(null);
    this.editing.set(true);
  }

  cancel(): void {
    this.editing.set(false);
  }

  onCategory(): void {
    this.category.set(this.form.controls.measurementCategory.value);
    this.applyRules(false);
  }

  /** Non-measurable: 0 decimals, no factor. In-use units lock category / decimals / factor. */
  private applyRules(inUse: boolean): void {
    const { measurementCategory, decimalPlaces, standardFactor } = this.form.controls;
    if (!isMeasurableCategory(this.category())) {
      decimalPlaces.setValue(0);
      standardFactor.setValue(null);
      decimalPlaces.disable();
      standardFactor.disable();
    } else {
      decimalPlaces.enable();
      standardFactor.enable();
    }
    if (inUse) {
      measurementCategory.disable();
      decimalPlaces.disable();
      standardFactor.disable();
    }
  }

  save(): void {
    this.banner.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.saving.set(true);
    const request = buildUnitRequest(this.form.getRawValue());
    const id = this.editId();
    (id ? this.service.updateUnit(id, request) : this.service.createUnit(request)).subscribe({
      next: () => {
        this.saving.set(false);
        this.editing.set(false);
        this.toast.show(id ? 'Unit updated.' : 'Unit created.');
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.banner.set(applyProblem(this.form, toProblem(err)));
      },
    });
  }

  private toggle(u: UnitItem): void {
    this.service.setUnitActive(u.id, !u.isActive).subscribe({
      next: () => {
        this.toast.show(`${u.name} ${u.isActive ? 'deactivated' : 'activated'}.`);
        this.load();
      },
      error: (err: HttpErrorResponse) => this.toast.show(applyProblem(this.form, toProblem(err)) ?? 'Could not update this unit.'),
    });
  }
}
