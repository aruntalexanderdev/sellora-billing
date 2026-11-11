import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { applyProblem, problemMessage, toProblem } from '../../core/http/api-errors';
import { UiButton } from '../../shared/ui/button/button';
import { UiDataTable } from '../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../shared/ui/toast/toast.service';
import type { CurrencyItem, UpsertCurrencyRequest } from './platform.models';
import { PlatformService } from './platform.service';

const COLS: DataTableColumn[] = [
  { label: 'Code', align: 'left' },
  { label: 'Name', align: 'left' },
  { label: 'Symbol', align: 'left' },
  { label: 'Decimals', align: 'right' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

export function buildCurrencyRequest(raw: { code: string; name: string; symbol: string; decimalPrecision: number }): UpsertCurrencyRequest {
  return { code: raw.code.trim().toUpperCase(), name: raw.name.trim(), symbol: raw.symbol.trim(), decimalPrecision: Number(raw.decimalPrecision) };
}

/** Platform-managed currencies (D12/O7): create, edit and activate/deactivate live only here. */
@Component({
  selector: 'app-platform-currencies',
  standalone: true,
  imports: [ReactiveFormsModule, UiButton, UiDataTable],
  template: `
    <div class="page-head">
      <div>
        <h1>Currencies</h1>
        <p>Shared currencies for every company. A currency that is a company's base currency cannot be deactivated.</p>
      </div>
      @if (!editing()) {
        <ui-button variant="primary" (onClick)="startCreate()">+ New currency</ui-button>
      }
    </div>

    @if (editing()) {
      <form class="panel" [formGroup]="form" (ngSubmit)="save()" [attr.aria-label]="editId() ? 'Edit currency' : 'New currency'">
        <h3>{{ editId() ? 'Edit currency' : 'New currency' }}</h3>
        @if (banner()) { <div class="server-error" role="alert">{{ banner() }}</div> }
        <div class="grid-2">
          <div>
            <label class="field-label" for="cu-code">Code (3 letters)</label>
            <input id="cu-code" class="field-input" formControlName="code" maxlength="3" />
            @if (form.controls.code.touched && form.controls.code.errors?.['pattern']) { <div class="field-error">Use a 3-letter code.</div> }
            @if (form.controls.code.errors?.['server']) { <div class="field-error">{{ form.controls.code.errors?.['server'] }}</div> }
          </div>
          <div>
            <label class="field-label" for="cu-name">Name</label>
            <input id="cu-name" class="field-input" formControlName="name" />
            @if (form.controls.name.errors?.['server']) { <div class="field-error">{{ form.controls.name.errors?.['server'] }}</div> }
          </div>
          <div>
            <label class="field-label" for="cu-sym">Symbol</label>
            <input id="cu-sym" class="field-input" formControlName="symbol" />
            @if (form.controls.symbol.errors?.['server']) { <div class="field-error">{{ form.controls.symbol.errors?.['server'] }}</div> }
          </div>
          <div>
            <label class="field-label" for="cu-dec">Decimal precision</label>
            <input id="cu-dec" type="number" min="0" max="6" class="field-input" formControlName="decimalPrecision" />
            @if (form.controls.decimalPrecision.errors?.['server']) { <div class="field-error">{{ form.controls.decimalPrecision.errors?.['server'] }}</div> }
          </div>
        </div>
        <div class="actions">
          <ui-button type="submit" variant="primary" [disabled]="saving()">{{ saving() ? 'Saving…' : 'Save currency' }}</ui-button>
          <ui-button (onClick)="cancel()">Cancel</ui-button>
        </div>
      </form>
    }

    <ui-data-table [cols]="cols" [rows]="rows()" [state]="state()" emptyTitle="No currencies yet" emptyBody="Add the currencies companies can transact in." (retry)="load()" />
  `,
  styleUrl: './platform.scss',
})
export class PlatformCurrencies implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(PlatformService);
  private readonly toast = inject(ToastService);

  readonly cols = COLS;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<CurrencyItem[]>([]);
  readonly editing = signal(false);
  readonly editId = signal<string | null>(null);
  readonly saving = signal(false);
  readonly banner = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(/^[A-Za-z]{3}$/)]],
    name: ['', Validators.required],
    symbol: ['', Validators.required],
    decimalPrecision: [2, [Validators.required, Validators.min(0), Validators.max(6)]],
  });

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((c) => ({
      cells: [
        { kind: 'text', text: c.code, weight: '650' },
        { kind: 'text', text: c.name, weight: '600' },
        { kind: 'text', text: c.symbol, color: 'var(--color-text-muted)' },
        { kind: 'text', text: String(c.decimalPrecision), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'badge', text: c.isBase ? 'Base' : c.isActive ? 'Active' : 'Inactive', tone: c.isBase || c.isActive ? 'good' : 'mute' },
        {
          kind: 'actions',
          actions: [
            { label: 'Edit', onClick: () => this.startEdit(c) },
            ...(c.isBase ? [] : [{ label: c.isActive ? 'Deactivate' : 'Activate', onClick: () => this.toggle(c) }]),
          ],
        },
      ],
    })),
  );

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.state.set('loading');
    this.service.currencies().subscribe({
      next: (items) => {
        this.items.set(items);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  startCreate(): void {
    this.editId.set(null);
    this.form.reset({ code: '', name: '', symbol: '', decimalPrecision: 2 });
    this.banner.set(null);
    this.editing.set(true);
  }

  startEdit(c: CurrencyItem): void {
    this.editId.set(c.id);
    this.form.reset({ code: c.code, name: c.name, symbol: c.symbol, decimalPrecision: c.decimalPrecision });
    this.banner.set(null);
    this.editing.set(true);
  }

  cancel(): void {
    this.editing.set(false);
  }

  save(): void {
    this.banner.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.saving.set(true);
    const request = buildCurrencyRequest(this.form.getRawValue());
    const id = this.editId();
    (id ? this.service.updateCurrency(id, request) : this.service.createCurrency(request)).subscribe({
      next: () => {
        this.saving.set(false);
        this.editing.set(false);
        this.toast.show(id ? 'Currency updated.' : 'Currency created.');
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.banner.set(applyProblem(this.form, toProblem(err)));
      },
    });
  }

  private toggle(c: CurrencyItem): void {
    this.service.setCurrencyActive(c.id, !c.isActive).subscribe({
      next: () => {
        this.toast.show(`${c.code} ${c.isActive ? 'deactivated' : 'activated'}.`);
        this.load();
      },
      error: (err: HttpErrorResponse) => this.toast.show(problemMessage(toProblem(err), 'Could not update this currency.')),
    });
  }
}
