import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { CountryLookupDto } from '../../../../shared/geo/geo.models';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { UiButton } from '../../../../shared/ui/button/button';
import { UiModal } from '../../../../shared/ui/modal/modal';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { problemFields, problemTitle } from '../problem-fields';
import { COMPONENT_KINDS, type TaxCalculationMethod, type TaxComponent, type TaxComponentKind } from '../tax-setup.models';
import { TaxSetupService } from '../tax-setup.service';

@Component({
  selector: 'app-tax-components-tab',
  standalone: true,
  imports: [ReactiveFormsModule, UiBadge, UiButton, UiModal],
  styleUrl: '../tax-setup.shared.scss',
  template: `
    <div class="toolbar">
      <div>
        <label class="field-label" for="tc-country">Country</label>
        <select id="tc-country" class="field-input" [value]="countryId()" (change)="setCountry($any($event.target).value)">
          <option value="">All countries</option>
          @for (c of countries(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
        </select>
      </div>
      <span class="grow"></span>
      @if (canManage()) { <ui-button variant="primary" (onClick)="openEditor(null)">+ New component</ui-button> }
    </div>

    <div class="panel">
      <p class="small muted">A component is what prints on the invoice (CGST, SGST, IGST, Cess, VAT). Profiles group components into a rate.</p>
      @if (loading()) {
        <p class="muted">Loading…</p>
      } @else if (loadFailed()) {
        <p class="field-error">Could not load components. <button type="button" class="link" (click)="load()">Retry</button></p>
      } @else if (items().length === 0) {
        <p class="muted" data-testid="components-empty">No tax components yet.</p>
      } @else {
        <div class="rows">
          @for (c of items(); track c.id) {
            <div class="row" data-testid="component-row">
              <div class="main"><span class="strong">{{ c.code }}</span> <span class="muted">{{ c.name }}</span></div>
              <span class="small muted">{{ c.countryCode }} · {{ c.kind }} · {{ c.method === 'Percent' ? 'Percent' : 'Amount per unit' }}</span>
              <ui-badge [text]="c.isActive ? 'Active' : 'Inactive'" [tone]="c.isActive ? 'good' : 'mute'" />
              @if (canManage()) {
                <div class="actions">
                  <button type="button" class="link" (click)="openEditor(c)">Edit</button>
                  <button type="button" class="link" (click)="toggle(c)">{{ c.isActive ? 'Deactivate' : 'Activate' }}</button>
                </div>
              }
            </div>
          }
        </div>
      }
    </div>

    @if (editing(); as target) {
      <form [formGroup]="form" (ngSubmit)="submit()" aria-label="Tax component" novalidate>
        <ui-modal [title]="target.id ? 'Edit component' : 'New component'" [closeOnBackdrop]="false" (closed)="editing.set(null)">
          @if (serverError()) { <div class="server-error">{{ serverError() }}</div> }
          @if (!target.id) {
            <label class="field-label" for="tce-country">Country</label>
            <select id="tce-country" class="field-input" formControlName="countryId">
              @for (c of countries(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
            </select>
            @if (fieldErrors()['countryId']; as m) { <div class="field-error">{{ m }}</div> }
          }
          <div class="grid-3" style="margin-top:10px">
            <div>
              <label class="field-label" for="tce-code">Code</label>
              <input id="tce-code" class="field-input" formControlName="code" maxlength="20" />
              @if (fieldErrors()['code'] ?? (form.controls.code.touched && form.controls.code.invalid ? 'Code is required.' : ''); as m) { <div class="field-error">{{ m }}</div> }
            </div>
            <div>
              <label class="field-label" for="tce-name">Name</label>
              <input id="tce-name" class="field-input" formControlName="name" maxlength="60" />
              @if (fieldErrors()['name'] ?? (form.controls.name.touched && form.controls.name.invalid ? 'Name is required.' : ''); as m) { <div class="field-error">{{ m }}</div> }
            </div>
          </div>
          <div class="grid-3">
            <div>
              <label class="field-label" for="tce-kind">Kind</label>
              <select id="tce-kind" class="field-input" formControlName="kind">
                @for (k of kinds; track k) { <option [value]="k">{{ k }}</option> }
              </select>
              @if (fieldErrors()['kind']; as m) { <div class="field-error">{{ m }}</div> }
            </div>
            <div>
              <label class="field-label" for="tce-method">Calculation</label>
              <select id="tce-method" class="field-input" formControlName="method">
                <option value="Percent">Percent of taxable value</option>
                <option value="AmountPerBaseUnit">Amount per base unit</option>
              </select>
              @if (fieldErrors()['method']; as m) { <div class="field-error">{{ m }}</div> }
            </div>
          </div>
          @if (target.isUsed) { <div class="field-hint" data-testid="locked-hint">Used by a profile or document: the kind and calculation cannot change.</div> }
          <div modal-footer>
            <ui-button variant="secondary" (onClick)="editing.set(null)">Cancel</ui-button>
            <ui-button type="submit" variant="primary" [disabled]="saving()">{{ saving() ? 'Saving…' : 'Save' }}</ui-button>
          </div>
        </ui-modal>
      </form>
    }
  `,
})
export class ComponentsTab {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(TaxSetupService);
  private readonly toast = inject(ToastService);

  readonly countries = input<CountryLookupDto[]>([]);
  readonly canManage = input(false);

  readonly kinds = COMPONENT_KINDS;
  readonly countryId = signal('');
  readonly items = signal<TaxComponent[]>([]);
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly saving = signal(false);
  /** Target of the modal; null = closed. */
  readonly editing = signal<{ id: string | null; isUsed: boolean } | null>(null);
  readonly serverError = signal<string | null>(null);
  readonly fieldErrors = signal<Record<string, string>>({});

  readonly form = this.fb.nonNullable.group({
    countryId: [''],
    code: ['', Validators.required],
    name: ['', Validators.required],
    kind: ['Cgst' as TaxComponentKind],
    method: ['Percent' as TaxCalculationMethod],
  });

  constructor() {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.service.listComponents(this.countryId() || null).subscribe({
      next: (list) => {
        this.items.set(list);
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

  openEditor(c: TaxComponent | null): void {
    this.serverError.set(null);
    this.fieldErrors.set({});
    this.form.reset({
      countryId: c?.countryId ?? (this.countryId() || this.countries()[0]?.id || ''),
      code: c?.code ?? '',
      name: c?.name ?? '',
      kind: c?.kind ?? 'Cgst',
      method: c?.method ?? 'Percent',
    });
    // Kind and method are fixed once a version or document line uses the component.
    if (c?.isUsed) {
      this.form.controls.kind.disable();
      this.form.controls.method.disable();
    } else {
      this.form.controls.kind.enable();
      this.form.controls.method.enable();
    }
    this.editing.set({ id: c?.id ?? null, isUsed: !!c?.isUsed });
  }

  submit(): void {
    const target = this.editing();
    if (!target || this.saving()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const body = { code: v.code.trim().toUpperCase(), name: v.name.trim(), kind: v.kind, method: v.method };
    const call$ = target.id ? this.service.updateComponent(target.id, body) : this.service.createComponent({ ...body, countryId: v.countryId });
    this.saving.set(true);
    this.serverError.set(null);
    this.fieldErrors.set({});
    call$.subscribe({
      next: () => {
        this.saving.set(false);
        this.editing.set(null);
        this.toast.show(target.id ? 'Component updated.' : 'Component added.');
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        const problem = err.error as ApiProblem | undefined;
        const fields = problemFields(problem);
        this.fieldErrors.set(fields);
        if (Object.keys(fields).length === 0) this.serverError.set(problemTitle(problem));
      },
    });
  }

  toggle(c: TaxComponent): void {
    this.service.setComponentActive(c.id, !c.isActive).subscribe({
      next: () => {
        this.toast.show(c.isActive ? 'Component deactivated.' : 'Component activated.');
        this.load();
      },
      error: (err: HttpErrorResponse) => this.toast.show(Object.values(problemFields(err.error as ApiProblem))[0] ?? problemTitle(err.error as ApiProblem)),
    });
  }
}
