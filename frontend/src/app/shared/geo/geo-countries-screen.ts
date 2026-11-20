import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { applyProblem, problemMessage, toProblem } from '../../core/http/api-errors';
import { UiButton } from '../ui/button/button';
import { UiDataTable } from '../ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../ui/data-table/data-table.types';
import { ToastService } from '../ui/toast/toast.service';
import type { CountryDto, CreateCountryRequest, GeographyApi, UpdateCountryRequest } from './geo.models';

const blank = (s: string): string | null => s.trim().toUpperCase() || null;

export function buildCountryRequest(raw: { code: string; iso3Code: string; name: string }): UpdateCountryRequest {
  return { code: raw.code.trim().toUpperCase(), iso3Code: blank(raw.iso3Code), name: raw.name.trim() };
}

/**
 * Countries master screen shared by the company Administration area and the Platform console.
 * Writes show only when the server says the caller may edit (`canEdit`, D8/AC8) and the caller holds the permission (`allowWrite`).
 */
@Component({
  selector: 'app-geo-countries-screen',
  standalone: true,
  imports: [ReactiveFormsModule, UiButton, UiDataTable],
  styleUrl: './geo-masters.scss',
  template: `
    <div class="page-head">
      <div>
        <h1>Countries</h1>
        <p>Shared countries used on addresses. Countries are never deleted; deactivate them to stop new selections. Existing records keep their country.</p>
      </div>
      @if (writable() && !editing()) {
        <ui-button variant="primary" (onClick)="startCreate()">+ New country</ui-button>
      }
    </div>

    @if (state() === 'ready' && !serverCanEdit()) {
      <div class="card muted" data-testid="read-only-note">These countries are managed by the platform administrator and cannot be changed here.</div>
    }

    @if (editing()) {
      <form class="panel" [formGroup]="form" (ngSubmit)="save()" [attr.aria-label]="editId() ? 'Edit country' : 'New country'">
        <h3>{{ editId() ? 'Edit country' : 'New country' }}</h3>
        @if (banner()) { <div class="server-error" role="alert">{{ banner() }}</div> }
        <div class="grid-2">
          <div>
            <label class="field-label" for="co-code">Code (ISO alpha-2)</label>
            <input id="co-code" class="field-input" formControlName="code" maxlength="2" />
            @if (form.controls.code.touched && form.controls.code.errors?.['pattern']) { <div class="field-error">Use a 2-letter code.</div> }
            @if (form.controls.code.errors?.['server']) { <div class="field-error">{{ form.controls.code.errors?.['server'] }}</div> }
          </div>
          <div>
            <label class="field-label" for="co-iso3">ISO alpha-3 (optional)</label>
            <input id="co-iso3" class="field-input" formControlName="iso3Code" maxlength="3" />
            @if (form.controls.iso3Code.errors?.['pattern']) { <div class="field-error">Use a 3-letter code.</div> }
            @if (form.controls.iso3Code.errors?.['server']) { <div class="field-error">{{ form.controls.iso3Code.errors?.['server'] }}</div> }
          </div>
          <div>
            <label class="field-label" for="co-name">Name</label>
            <input id="co-name" class="field-input" formControlName="name" maxlength="100" />
            @if (form.controls.name.touched && form.controls.name.errors?.['required']) { <div class="field-error">Name is required.</div> }
            @if (form.controls.name.errors?.['server']) { <div class="field-error">{{ form.controls.name.errors?.['server'] }}</div> }
          </div>
        </div>
        @if (editIsSystem()) { <p class="muted">System country: the codes are locked; the name can be changed.</p> }
        @if (!editId()) {
          <label class="check"><input type="checkbox" formControlName="isActive" /> Active</label>
        }
        <div class="actions">
          <ui-button type="submit" variant="primary" [disabled]="saving()">{{ saving() ? 'Saving…' : 'Save country' }}</ui-button>
          <ui-button (onClick)="cancel()">Cancel</ui-button>
        </div>
      </form>
    }

    <div class="filters">
      <label class="check"><input type="checkbox" [checked]="showInactive()" (change)="toggleInactive($event)" data-testid="show-inactive" /> Show inactive</label>
    </div>

    <ui-data-table [cols]="cols()" [rows]="rows()" [state]="state()" emptyTitle="No countries yet" emptyBody="Countries appear here once they are added." (retry)="load()" />
  `,
})
export class GeoCountriesScreen implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly toast = inject(ToastService);

  readonly api = input.required<GeographyApi>();
  /** Permission gate (Masters.Edit); the platform console passes true. */
  readonly allowWrite = input(true);

  readonly state = signal<DataTableState>('loading');
  readonly items = signal<CountryDto[]>([]);
  readonly serverCanEdit = signal(false);
  readonly showInactive = signal(true);
  readonly editing = signal(false);
  readonly editId = signal<string | null>(null);
  readonly editIsSystem = signal(false);
  readonly saving = signal(false);
  readonly banner = signal<string | null>(null);

  readonly writable = computed(() => this.serverCanEdit() && this.allowWrite());

  readonly form = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(/^[A-Za-z]{2}$/)]],
    iso3Code: ['', Validators.pattern(/^[A-Za-z]{3}$/)],
    name: ['', [Validators.required, Validators.maxLength(100)]],
    isActive: [true],
  });

  readonly cols = computed<DataTableColumn[]>(() => [
    { label: 'Code', align: 'left' },
    { label: 'ISO3', align: 'left' },
    { label: 'Name', align: 'left' },
    { label: 'Status', align: 'left' },
    ...(this.writable() ? [{ label: '', align: 'right' as const }] : []),
  ]);

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((c) => ({
      cells: [
        { kind: 'text', text: c.code, weight: '650' },
        { kind: 'text', text: c.iso3Code ?? '', color: 'var(--color-text-muted)' },
        { kind: 'text', text: c.name, weight: '600' },
        { kind: 'badge', text: c.isActive ? 'Active' : 'Inactive', tone: c.isActive ? 'good' : 'mute' },
        ...(this.writable()
          ? [{
              kind: 'actions' as const,
              actions: [
                { label: 'Edit', onClick: () => this.startEdit(c) },
                { label: c.isActive ? 'Deactivate' : 'Activate', onClick: () => this.toggle(c) },
              ],
            }]
          : []),
      ],
    })),
  );

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.state.set('loading');
    this.api().listCountries(this.showInactive()).subscribe({
      next: (list) => {
        this.items.set(list.items);
        this.serverCanEdit.set(list.canEdit);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  toggleInactive(e: Event): void {
    this.showInactive.set((e.target as HTMLInputElement).checked);
    this.load();
  }

  startCreate(): void {
    this.editId.set(null);
    this.editIsSystem.set(false);
    this.form.enable();
    this.form.reset({ code: '', iso3Code: '', name: '', isActive: true });
    this.banner.set(null);
    this.editing.set(true);
  }

  startEdit(c: CountryDto): void {
    this.editId.set(c.id);
    this.editIsSystem.set(c.isSystem);
    this.form.enable();
    this.form.reset({ code: c.code, iso3Code: c.iso3Code ?? '', name: c.name, isActive: c.isActive });
    if (c.isSystem) {
      this.form.controls.code.disable();
      this.form.controls.iso3Code.disable();
    }
    this.banner.set(null);
    this.editing.set(true);
  }

  cancel(): void {
    this.editing.set(false);
  }

  /** Locked (system) codes are disabled in the form but still posted unchanged, as the server expects. */
  private buildRequest(): UpdateCountryRequest {
    return buildCountryRequest(this.form.getRawValue());
  }

  save(): void {
    this.banner.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.saving.set(true);
    const base = this.buildRequest();
    const id = this.editId();
    const call = id
      ? this.api().updateCountry(id, base)
      : this.api().createCountry({ ...base, isActive: this.form.getRawValue().isActive } satisfies CreateCountryRequest);
    call.subscribe({
      next: () => {
        this.saving.set(false);
        this.editing.set(false);
        this.toast.show(id ? 'Country updated.' : 'Country created.');
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.banner.set(applyProblem(this.form, toProblem(err)));
      },
    });
  }

  private toggle(c: CountryDto): void {
    this.api().setCountryActive(c.id, !c.isActive).subscribe({
      next: () => {
        this.toast.show(`${c.name} ${c.isActive ? 'deactivated' : 'activated'}.`);
        this.load();
      },
      error: (err: HttpErrorResponse) => this.toast.show(problemMessage(toProblem(err), 'Could not update this country.')),
    });
  }
}
