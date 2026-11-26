import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, input, signal, viewChild } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { CountryLookupDto } from '../../../../shared/geo/geo.models';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { UiButton } from '../../../../shared/ui/button/button';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { UiModal } from '../../../../shared/ui/modal/modal';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { problemFields, problemTitle } from '../problem-fields';
import { TaxVersionForm } from '../tax-version-form/tax-version-form';
import { TAX_TREATMENTS, type TaxComponent, type TaxProfile, type TaxProfileDetail, type TaxRegime, type TaxTreatment, type TaxVersion } from '../tax-setup.models';
import { TaxSetupService } from '../tax-setup.service';

const treatmentLabel = (t: TaxTreatment) => TAX_TREATMENTS.find((x) => x.value === t)?.label ?? t;

@Component({
  selector: 'app-tax-profiles-tab',
  standalone: true,
  imports: [ReactiveFormsModule, UiBadge, UiButton, UiModal, TaxVersionForm],
  styleUrl: '../tax-setup.shared.scss',
  template: `
    <div class="toolbar">
      <div>
        <label class="field-label" for="tp-country">Country</label>
        <select id="tp-country" class="field-input" [value]="countryId()" (change)="setCountry($any($event.target).value)">
          <option value="">All countries</option>
          @for (c of countries(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
        </select>
      </div>
      <div>
        <label class="field-label" for="tp-active">Status</label>
        <select id="tp-active" class="field-input" [value]="activeFilter()" (change)="setActiveFilter($any($event.target).value)">
          <option value="true">Active</option>
          <option value="false">Inactive</option>
          <option value="">All</option>
        </select>
      </div>
      <span class="grow"></span>
      @if (canManage()) {
        <ui-button variant="secondary" (onClick)="seed()" [disabled]="busy()">Seed standard profiles</ui-button>
        <ui-button variant="primary" (onClick)="openCreate()">+ New profile</ui-button>
      }
    </div>

    <div class="panel">
      @if (loading()) {
        <p class="muted">Loading…</p>
      } @else if (loadFailed()) {
        <p class="field-error">Could not load tax profiles. <button type="button" class="link" (click)="load()">Retry</button></p>
      } @else if (profiles().length === 0) {
        <p class="muted" data-testid="profiles-empty">No tax profiles match.{{ canManage() ? ' Use "Seed standard profiles" to add the standard ones.' : '' }}</p>
      } @else {
        <div class="rows">
          @for (p of profiles(); track p.id) {
            <div class="row" [class.selected]="p.id === selectedId()" data-testid="profile-row">
              <div class="main">
                <button type="button" class="link" (click)="select(p.id)">{{ p.name }}</button>
                <span class="muted small"> {{ p.code }} · {{ p.countryCode }}</span>
              </div>
              <span class="small muted">{{ treatmentText(p.treatment) }}{{ p.currentVersion && p.treatment === 'Taxable' ? ' · ' + p.currentVersion.totalPercent + '%' : '' }}</span>
              <ui-badge [text]="p.isActive ? 'Active' : 'Inactive'" [tone]="p.isActive ? 'good' : 'mute'" />
            </div>
          }
        </div>
      }
    </div>

    @if (detail(); as d) {
      <div class="panel" data-testid="profile-detail">
        <div class="toolbar">
          <div class="grow">
            <h2 class="strong" style="margin:0;font-size:16px">{{ d.name }} <span class="muted small">{{ d.code }}</span></h2>
            <div class="small muted">{{ d.countryName }} · {{ d.regime === 'Gst' ? 'GST' : 'VAT' }} · {{ treatmentText(d.treatment) }}{{ d.isAssigned ? ' · in use by the tax mapping' : '' }}</div>
          </div>
          <ui-badge [text]="d.isActive ? 'Active' : 'Inactive'" [tone]="d.isActive ? 'good' : 'mute'" />
          @if (canManage()) {
            <button type="button" class="link" (click)="startRename()">Rename</button>
            <button type="button" class="link" (click)="toggleActive(d)">{{ d.isActive ? 'Deactivate' : 'Activate' }}</button>
          }
        </div>

        @if (renaming()) {
          <form [formGroup]="renameForm" (ngSubmit)="saveRename(d)" class="toolbar" aria-label="Rename profile">
            <div><label class="field-label" for="pr-code">Code</label><input id="pr-code" class="field-input" formControlName="code" maxlength="20" /></div>
            <div><label class="field-label" for="pr-name">Name</label><input id="pr-name" class="field-input" formControlName="name" maxlength="60" /></div>
            <ui-button type="submit" variant="primary" [disabled]="busy()">Save</ui-button>
            <ui-button variant="secondary" (onClick)="renaming.set(false)">Cancel</ui-button>
            @if (renameError()) { <div class="field-error">{{ renameError() }}</div> }
          </form>
        }

        <div class="toolbar">
          <h3 class="strong grow" style="margin:0;font-size:14px">Versions</h3>
          @if (canManage() && !addingVersion()) {
            <ui-button variant="secondary" (onClick)="addingVersion.set(true)">+ New rate from a date</ui-button>
          }
        </div>

        @if (addingVersion() && canManage()) {
          <div class="panel" style="background:var(--color-surface-subtle)" data-testid="add-version">
            @if (versionError()) { <div class="server-error">{{ versionError() }}</div> }
            <app-tax-version-form #addForm [regime]="d.regime" [treatment]="d.treatment" [components]="components()" [latestFrom]="latestFrom(d)" />
            <div class="toolbar" style="margin-top:12px">
              <ui-button variant="primary" (onClick)="submitVersion(d)" [disabled]="busy()">{{ busy() ? 'Saving…' : 'Add version' }}</ui-button>
              <ui-button variant="secondary" (onClick)="addingVersion.set(false)">Cancel</ui-button>
            </div>
          </div>
        }

        <div class="rows" data-testid="timeline">
          @for (v of d.versions; track v.id) {
            <div class="row" data-testid="version">
              <div class="main">
                <span class="strong">From {{ v.effectiveFrom }}{{ v.effectiveTo ? ' to ' + v.effectiveTo : ' onwards' }}</span>
                @if (d.treatment === 'Taxable') { <span> · {{ v.totalPercent }}%</span> }
                <div class="small muted">{{ componentSummary(v) }}</div>
              </div>
              @if (v.isCurrent) { <span class="chip here">Current</span> }
              @if (v.isUsed) { <span class="chip" title="A document uses this version, so it cannot change.">Used</span> }
            </div>
          } @empty {
            <p class="muted small">No versions yet: this profile cannot be used until one is added.</p>
          }
        </div>
      </div>
    }

    @if (creating()) {
      <form (ngSubmit)="submitCreate()" [formGroup]="createForm" aria-label="New tax profile" novalidate>
        <ui-modal title="New tax profile" size="wide" [closeOnBackdrop]="false" (closed)="creating.set(false)">
          @if (createError()) { <div class="server-error">{{ createError() }}</div> }
          <div class="grid-3">
            <div>
              <label class="field-label" for="np-country">Country</label>
              <select id="np-country" class="field-input" formControlName="countryId" (change)="onCreateCountry()">
                @for (c of countries(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
              </select>
              @if (createFieldErrors()['countryId']; as m) { <div class="field-error">{{ m }}</div> }
            </div>
            <div>
              <label class="field-label" for="np-regime">Regime</label>
              <select id="np-regime" class="field-input" formControlName="regime">
                <option value="Gst">GST</option>
                <option value="Vat">VAT</option>
              </select>
              @if (createFieldErrors()['regime']; as m) { <div class="field-error">{{ m }}</div> }
            </div>
            <div>
              <label class="field-label" for="np-treatment">Treatment</label>
              <select id="np-treatment" class="field-input" formControlName="treatment">
                @for (t of treatments; track t.value) { <option [value]="t.value">{{ t.label }}</option> }
              </select>
            </div>
          </div>
          <div class="grid-3">
            <div>
              <label class="field-label" for="np-code">Code</label>
              <input id="np-code" class="field-input" formControlName="code" maxlength="20" />
              @if (createFieldErrors()['code'] ?? (createForm.controls.code.touched && createForm.controls.code.invalid ? 'Code is required.' : ''); as m) { <div class="field-error">{{ m }}</div> }
            </div>
            <div>
              <label class="field-label" for="np-name">Name</label>
              <input id="np-name" class="field-input" formControlName="name" maxlength="60" />
              @if (createFieldErrors()['name'] ?? (createForm.controls.name.touched && createForm.controls.name.invalid ? 'Name is required.' : ''); as m) { <div class="field-error">{{ m }}</div> }
            </div>
          </div>
          <h3 class="strong" style="font-size:14px">First rate</h3>
          <app-tax-version-form #createVersion [regime]="createForm.controls.regime.value" [treatment]="createForm.controls.treatment.value" [components]="createComponents()" />
          <div modal-footer>
            <ui-button variant="secondary" (onClick)="creating.set(false)">Cancel</ui-button>
            <ui-button type="submit" variant="primary" [disabled]="busy()">{{ busy() ? 'Saving…' : 'Create profile' }}</ui-button>
          </div>
        </ui-modal>
      </form>
    }
  `,
})
export class ProfilesTab {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(TaxSetupService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);

  readonly countries = input<CountryLookupDto[]>([]);
  readonly canManage = input(false);

  readonly treatments = TAX_TREATMENTS;
  readonly countryId = signal('');
  readonly activeFilter = signal('true');
  readonly profiles = signal<TaxProfile[]>([]);
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly busy = signal(false);

  readonly selectedId = signal<string | null>(null);
  readonly detail = signal<TaxProfileDetail | null>(null);
  /** Active components of the selected profile's country (add-version form). */
  readonly components = signal<TaxComponent[]>([]);
  readonly addingVersion = signal(false);
  readonly versionError = signal<string | null>(null);
  readonly renaming = signal(false);
  readonly renameError = signal<string | null>(null);

  readonly creating = signal(false);
  readonly createError = signal<string | null>(null);
  readonly createFieldErrors = signal<Record<string, string>>({});
  readonly createComponents = signal<TaxComponent[]>([]);

  readonly renameForm = this.fb.nonNullable.group({ code: ['', Validators.required], name: ['', Validators.required] });
  readonly createForm = this.fb.nonNullable.group({
    countryId: ['', Validators.required],
    regime: ['Gst' as TaxRegime],
    treatment: ['Taxable' as TaxTreatment],
    code: ['', Validators.required],
    name: ['', Validators.required],
  });

  private readonly addForm = viewChild<TaxVersionForm>('addForm');
  private readonly createVersion = viewChild<TaxVersionForm>('createVersion');

  readonly hasProfiles = computed(() => this.profiles().length > 0);

  constructor() {
    this.load();
  }

  treatmentText = treatmentLabel;

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    const active = this.activeFilter() === '' ? null : this.activeFilter() === 'true';
    this.service.listProfiles(this.countryId() || null, active).subscribe({
      next: (list) => {
        this.profiles.set(list);
        this.loading.set(false);
      },
      error: () => {
        this.loadFailed.set(true);
        this.loading.set(false);
      },
    });
  }

  setCountry(id: string): void {
    this.countryId.set(id);
    this.load();
  }

  setActiveFilter(value: string): void {
    this.activeFilter.set(value);
    this.load();
  }

  select(id: string): void {
    this.selectedId.set(id);
    this.addingVersion.set(false);
    this.renaming.set(false);
    this.loadDetail(id);
  }

  private loadDetail(id: string): void {
    this.service.getProfile(id).subscribe({
      next: (d) => {
        this.detail.set(d);
        this.service.listComponents(d.countryId, true).subscribe({ next: (c) => this.components.set(c) });
      },
    });
  }

  latestFrom(d: TaxProfileDetail): string | null {
    return d.versions.reduce<string | null>((max, v) => (max === null || v.effectiveFrom > max ? v.effectiveFrom : max), null);
  }

  componentSummary(v: TaxVersion): string {
    if (v.components.length === 0) return 'No tax components';
    return v.components
      .map((c) => `${c.componentCode} ${c.method === 'Percent' ? `${c.ratePercent}%` : `${c.amountPerBaseUnit} per unit`}${c.applicability === 'IntraState' ? ' (intra)' : c.applicability === 'InterState' ? ' (inter)' : ''}`)
      .join(' · ');
  }

  // ---- add version
  submitVersion(d: TaxProfileDetail): void {
    const request = this.addForm()?.build();
    if (!request) return;
    this.versionError.set(null);
    this.busy.set(true);
    this.service.addVersion(d.id, request).subscribe({
      next: () => {
        this.busy.set(false);
        this.addingVersion.set(false);
        this.toast.show('New rate added.');
        this.loadDetail(d.id);
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        this.busy.set(false);
        const problem = err.error as ApiProblem | undefined;
        this.addForm()?.applyServerErrors(problem);
        if (!problem?.errors || Object.keys(problem.errors).length === 0) this.versionError.set(problemTitle(problem));
      },
    });
  }

  // ---- rename / status
  startRename(): void {
    const d = this.detail();
    if (!d) return;
    this.renameForm.reset({ code: d.code, name: d.name });
    this.renameError.set(null);
    this.renaming.set(true);
  }

  saveRename(d: TaxProfileDetail): void {
    if (this.renameForm.invalid) return;
    this.busy.set(true);
    this.service.updateProfile(d.id, this.renameForm.getRawValue()).subscribe({
      next: (updated) => {
        this.busy.set(false);
        this.renaming.set(false);
        this.detail.set(updated);
        this.toast.show('Profile updated.');
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        this.busy.set(false);
        const problem = err.error as ApiProblem | undefined;
        this.renameError.set(Object.values(problemFields(problem))[0] ?? problemTitle(problem));
      },
    });
  }

  async toggleActive(d: TaxProfileDetail): Promise<void> {
    if (d.isActive) {
      const ok = await this.confirm.ask({
        title: `Deactivate ${d.name}?`,
        body: 'It can no longer be newly assigned or used on new documents. History keeps it.',
        confirmLabel: 'Deactivate',
        destructive: true,
      });
      if (!ok) return;
    }
    this.service.setProfileActive(d.id, !d.isActive).subscribe({
      next: (updated) => {
        this.detail.set(updated);
        this.toast.show(updated.isActive ? 'Profile activated.' : 'Profile deactivated.');
        this.load();
      },
      error: (err: HttpErrorResponse) => this.toast.show(Object.values(problemFields(err.error as ApiProblem))[0] ?? problemTitle(err.error as ApiProblem)),
    });
  }

  // ---- seed
  async seed(): Promise<void> {
    const ok = await this.confirm.ask({
      title: 'Seed standard profiles?',
      body: 'Adds the standard components and profiles of your country (for example GST 5%, 18%, 40% or the country VAT). Names that already exist are skipped.',
      confirmLabel: 'Seed',
    });
    if (!ok) return;
    this.busy.set(true);
    this.service.seedStandard().subscribe({
      next: (r) => {
        this.busy.set(false);
        this.toast.show(
          r.countrySupported
            ? `Added ${r.profilesAdded} profile(s) and ${r.componentsAdded} component(s); ${r.profilesSkipped} skipped.`
            : 'There are no standard profiles for your country.',
        );
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        this.busy.set(false);
        this.toast.show(problemTitle(err.error as ApiProblem, 'Could not seed the profiles.'));
      },
    });
  }

  // ---- create
  openCreate(): void {
    const country = this.countryId() || this.countries()[0]?.id || '';
    this.createForm.reset({ countryId: country, regime: this.regimeFor(country), treatment: 'Taxable', code: '', name: '' });
    this.createError.set(null);
    this.createFieldErrors.set({});
    this.createComponents.set([]);
    this.creating.set(true);
    this.loadCreateComponents();
  }

  onCreateCountry(): void {
    this.createForm.controls.regime.setValue(this.regimeFor(this.createForm.controls.countryId.value));
    this.loadCreateComponents();
  }

  private regimeFor(countryId: string): TaxRegime {
    return this.countries().find((c) => c.id === countryId)?.code === 'IN' ? 'Gst' : 'Vat';
  }

  private loadCreateComponents(): void {
    const id = this.createForm.controls.countryId.value;
    if (!id) return;
    this.service.listComponents(id, true).subscribe({ next: (c) => this.createComponents.set(c) });
  }

  submitCreate(): void {
    this.createError.set(null);
    this.createFieldErrors.set({});
    if (this.createForm.invalid) {
      this.createForm.markAllAsTouched();
      return;
    }
    const version = this.createVersion()?.build();
    if (!version) return;
    const v = this.createForm.getRawValue();
    this.busy.set(true);
    this.service
      .createProfile({ countryId: v.countryId, code: v.code.trim().toUpperCase(), name: v.name.trim(), regime: v.regime, treatment: v.treatment, initialVersion: version })
      .subscribe({
        next: (created) => {
          this.busy.set(false);
          this.creating.set(false);
          this.toast.show('Tax profile added.');
          this.load();
          this.select(created.id);
        },
        error: (err: HttpErrorResponse) => {
          this.busy.set(false);
          const problem = err.error as ApiProblem | undefined;
          const fields = problemFields(problem);
          this.createFieldErrors.set(fields);
          this.createVersion()?.applyServerErrors(problem);
          if (Object.keys(fields).length === 0) this.createError.set(problemTitle(problem));
        },
      });
  }
}
