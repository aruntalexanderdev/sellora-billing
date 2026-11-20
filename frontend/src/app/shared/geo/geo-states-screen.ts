import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { applyProblem, problemMessage, toProblem } from '../../core/http/api-errors';
import { UiButton } from '../ui/button/button';
import { UiDataTable } from '../ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../ui/data-table/data-table.types';
import { ToastService } from '../ui/toast/toast.service';
import { STATE_KINDS, stateKindLabel } from './geo.models';
import type { CountryLookupDto, GeographyApi, StateDto, StateKind, UpdateStateRequest } from './geo.models';

export function buildStateRequest(raw: { code: string; name: string; kind: StateKind; gstStateCode: string }): UpdateStateRequest {
  return { code: raw.code.trim().toUpperCase(), name: raw.name.trim(), kind: raw.kind, gstStateCode: raw.gstStateCode.trim() || null };
}

/** States master screen shared by the company Administration area and the Platform console (see GeoCountriesScreen). */
@Component({
  selector: 'app-geo-states-screen',
  standalone: true,
  imports: [ReactiveFormsModule, UiButton, UiDataTable],
  styleUrl: './geo-masters.scss',
  template: `
    <div class="page-head">
      <div>
        <h1>States</h1>
        <p>First-level subdivisions (state, union territory, emirate, region, governorate) of each country. States are never deleted; deactivate them to stop new selections.</p>
      </div>
      @if (writable() && !editing()) {
        <ui-button variant="primary" (onClick)="startCreate()">+ New state</ui-button>
      }
    </div>

    @if (state() === 'ready' && !serverCanEdit()) {
      <div class="card muted" data-testid="read-only-note">These states are managed by the platform administrator and cannot be changed here.</div>
    }

    @if (editing()) {
      <form class="panel" [formGroup]="form" (ngSubmit)="save()" [attr.aria-label]="editId() ? 'Edit state' : 'New state'">
        <h3>{{ editId() ? 'Edit state' : 'New state' }}</h3>
        @if (banner()) { <div class="server-error" role="alert">{{ banner() }}</div> }
        <div class="grid-2">
          <div>
            <label class="field-label" for="st-country">Country</label>
            <select id="st-country" class="field-input" formControlName="countryId">
              <option value="">Select a country…</option>
              @for (c of countryOptions(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
            </select>
            @if (form.controls.countryId.touched && form.controls.countryId.errors?.['required']) { <div class="field-error">Choose a country.</div> }
            @if (form.controls.countryId.errors?.['server']) { <div class="field-error">{{ form.controls.countryId.errors?.['server'] }}</div> }
          </div>
          <div>
            <label class="field-label" for="st-kind">Kind</label>
            <select id="st-kind" class="field-input" formControlName="kind">
              @for (k of kinds; track k.value) { <option [value]="k.value">{{ k.label }}</option> }
            </select>
            @if (form.controls.kind.errors?.['server']) { <div class="field-error">{{ form.controls.kind.errors?.['server'] }}</div> }
          </div>
          <div>
            <label class="field-label" for="st-code">Code (1-3 letters/digits)</label>
            <input id="st-code" class="field-input" formControlName="code" maxlength="3" />
            @if (form.controls.code.touched && form.controls.code.errors?.['pattern']) { <div class="field-error">Use 1-3 letters or digits.</div> }
            @if (form.controls.code.errors?.['server']) { <div class="field-error">{{ form.controls.code.errors?.['server'] }}</div> }
          </div>
          <div>
            <label class="field-label" for="st-name">Name</label>
            <input id="st-name" class="field-input" formControlName="name" maxlength="100" />
            @if (form.controls.name.touched && form.controls.name.errors?.['required']) { <div class="field-error">Name is required.</div> }
            @if (form.controls.name.errors?.['server']) { <div class="field-error">{{ form.controls.name.errors?.['server'] }}</div> }
          </div>
          @if (isIndia()) {
            <div>
              <label class="field-label" for="st-gst">GST state code (optional)</label>
              <input id="st-gst" class="field-input" formControlName="gstStateCode" maxlength="2" />
              @if (form.controls.gstStateCode.errors?.['pattern']) { <div class="field-error">Use a 2-digit code.</div> }
              @if (form.controls.gstStateCode.errors?.['server']) { <div class="field-error">{{ form.controls.gstStateCode.errors?.['server'] }}</div> }
            </div>
          }
        </div>
        @if (editIsSystem()) { <p class="muted">System state: the code and GST state code are locked; the name and kind can be changed.</p> }
        @if (!editId()) {
          <label class="check"><input type="checkbox" formControlName="isActive" /> Active</label>
          @if (form.controls.isActive.errors?.['server']) { <div class="field-error">{{ form.controls.isActive.errors?.['server'] }}</div> }
        }
        <div class="actions">
          <ui-button type="submit" variant="primary" [disabled]="saving()">{{ saving() ? 'Saving…' : 'Save state' }}</ui-button>
          <ui-button (onClick)="cancel()">Cancel</ui-button>
        </div>
      </form>
    }

    <div class="filters">
      <label class="field-label" for="st-filter">Country</label>
      <select id="st-filter" class="field-input" [value]="countryFilter()" (change)="setFilter($event)" data-testid="country-filter">
        <option value="">All countries</option>
        @for (c of countryOptions(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
      </select>
      <label class="check"><input type="checkbox" [checked]="showInactive()" (change)="toggleInactive($event)" data-testid="show-inactive" /> Show inactive</label>
    </div>

    <ui-data-table [cols]="cols()" [rows]="rows()" [state]="state()" emptyTitle="No states yet" emptyBody="States appear here once they are added." (retry)="load()" />
  `,
})
export class GeoStatesScreen implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly toast = inject(ToastService);

  readonly api = input.required<GeographyApi>();
  readonly allowWrite = input(true);

  readonly kinds = STATE_KINDS;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<StateDto[]>([]);
  readonly countryOptions = signal<CountryLookupDto[]>([]);
  readonly serverCanEdit = signal(false);
  readonly showInactive = signal(true);
  readonly countryFilter = signal('');
  readonly editing = signal(false);
  readonly editId = signal<string | null>(null);
  readonly editIsSystem = signal(false);
  readonly saving = signal(false);
  readonly banner = signal<string | null>(null);
  private readonly selectedCountryId = signal('');
  private readonly editCountryCode = signal<string | null>(null);

  readonly writable = computed(() => this.serverCanEdit() && this.allowWrite());
  /** GST state code applies to India only (BR4). */
  readonly isIndia = computed(() => {
    const code = this.editId() ? this.editCountryCode() : this.countryOptions().find((c) => c.id === this.selectedCountryId())?.code;
    return code === 'IN';
  });

  readonly form = this.fb.nonNullable.group({
    countryId: ['', Validators.required],
    code: ['', [Validators.required, Validators.pattern(/^[A-Za-z0-9]{1,3}$/)]],
    name: ['', [Validators.required, Validators.maxLength(100)]],
    kind: ['State' as StateKind, Validators.required],
    gstStateCode: ['', Validators.pattern(/^[0-9]{2}$/)],
    isActive: [true],
  });

  readonly cols = computed<DataTableColumn[]>(() => [
    { label: 'Country', align: 'left' },
    { label: 'Code', align: 'left' },
    { label: 'Name', align: 'left' },
    { label: 'Kind', align: 'left' },
    { label: 'GST code', align: 'left' },
    { label: 'Status', align: 'left' },
    ...(this.writable() ? [{ label: '', align: 'right' as const }] : []),
  ]);

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((s) => ({
      cells: [
        { kind: 'text', text: s.countryName, color: 'var(--color-text-muted)' },
        { kind: 'text', text: s.code, weight: '650' },
        { kind: 'text', text: s.name, weight: '600' },
        { kind: 'text', text: stateKindLabel(s.kind), color: 'var(--color-text-muted)' },
        { kind: 'text', text: s.gstStateCode ?? '', color: 'var(--color-text-muted)' },
        { kind: 'badge', text: s.isActive ? 'Active' : 'Inactive', tone: s.isActive ? 'good' : 'mute' },
        ...(this.writable()
          ? [{
              kind: 'actions' as const,
              actions: [
                { label: 'Edit', onClick: () => this.startEdit(s) },
                { label: s.isActive ? 'Deactivate' : 'Activate', onClick: () => this.toggle(s) },
              ],
            }]
          : []),
      ],
    })),
  );

  constructor() {
    this.form.controls.countryId.valueChanges.subscribe((v) => this.selectedCountryId.set(v));
  }

  ngOnInit(): void {
    this.api().lookupCountries().subscribe({ next: (c) => this.countryOptions.set(c), error: () => this.countryOptions.set([]) });
    this.load();
  }

  load(): void {
    this.state.set('loading');
    this.api().listStates(this.countryFilter() || null, this.showInactive()).subscribe({
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

  setFilter(e: Event): void {
    this.countryFilter.set((e.target as HTMLSelectElement).value);
    this.load();
  }

  startCreate(): void {
    this.editId.set(null);
    this.editIsSystem.set(false);
    this.editCountryCode.set(null);
    this.form.enable();
    this.form.reset({ countryId: this.countryFilter(), code: '', name: '', kind: 'State', gstStateCode: '', isActive: true });
    this.banner.set(null);
    this.editing.set(true);
  }

  startEdit(s: StateDto): void {
    this.editId.set(s.id);
    this.editIsSystem.set(s.isSystem);
    this.editCountryCode.set(s.countryCode);
    this.form.enable();
    this.form.reset({ countryId: s.countryId, code: s.code, name: s.name, kind: s.kind, gstStateCode: s.gstStateCode ?? '', isActive: s.isActive });
    this.form.controls.countryId.disable(); // CountryId never changes (not in the update contract)
    if (s.isSystem) {
      this.form.controls.code.disable();
      this.form.controls.gstStateCode.disable();
    }
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
    const raw = this.form.getRawValue();
    const base = buildStateRequest(raw);
    // GST code is India-only: never post one for another country.
    if (!this.isIndia()) base.gstStateCode = null;
    const id = this.editId();
    const call = id ? this.api().updateState(id, base) : this.api().createState({ ...base, countryId: raw.countryId, isActive: raw.isActive });
    call.subscribe({
      next: () => {
        this.saving.set(false);
        this.editing.set(false);
        this.toast.show(id ? 'State updated.' : 'State created.');
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.banner.set(applyProblem(this.form, toProblem(err)));
      },
    });
  }

  private toggle(s: StateDto): void {
    this.api().setStateActive(s.id, !s.isActive).subscribe({
      next: () => {
        this.toast.show(`${s.name} ${s.isActive ? 'deactivated' : 'activated'}.`);
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        const p = toProblem(err);
        this.toast.show(p?.errors?.['isActive']?.[0] ?? p?.errors?.['IsActive']?.[0] ?? problemMessage(p, 'Could not update this state.'));
      },
    });
  }
}
