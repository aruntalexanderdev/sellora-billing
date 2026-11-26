import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormArray, FormBuilder, FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import {
  defaultApplicability, splitGstRate, todayIso, totalPercent, validateEffectiveFrom, validateVersionLines, type VersionLineDraft,
} from '../gst-version-rules';
import { problemFields } from '../problem-fields';
import type { AddTaxVersionRequest, TaxComponent, TaxRegime, TaxTreatment } from '../tax-setup.models';

type RowGroup = FormGroup<{
  componentId: FormControl<string>;
  ratePercent: FormControl<number | null>;
  amountPerBaseUnit: FormControl<number | null>;
}>;

/**
 * Effective-from date plus the component rows of one profile version. For GST it seeds CGST, SGST/UTGST and IGST rows
 * and offers "Fill from total GST rate" so IGST = CGST + SGST holds by construction. The host owns the buttons and calls `build()`.
 */
@Component({
  selector: 'app-tax-version-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  styleUrl: '../tax-setup.shared.scss',
  template: `
    <div [formGroup]="form">
      <div class="grid-3">
        <div>
          <label class="field-label" [for]="id + '-from'">Effective from</label>
          <input [id]="id + '-from'" type="date" class="field-input" formControlName="effectiveFrom" [max]="today" data-testid="version-from" />
          @if (fieldErrors()['effectiveFrom']; as msg) { <div class="field-error" data-testid="version-from-error">{{ msg }}</div> }
          <div class="field-hint">Cannot be after today. A new version closes the previous one the day before.</div>
        </div>
      </div>

      @if (treatment() === 'Taxable') {
        @if (regime() === 'Gst') {
          <div class="toolbar">
            <div>
              <label class="field-label" [for]="id + '-total'">Total GST rate %</label>
              <input [id]="id + '-total'" type="number" step="any" min="0" max="100" class="field-input" [value]="quickTotal()" (input)="quickTotal.set($any($event.target).value)" data-testid="quick-total" />
            </div>
            <button type="button" class="link" (click)="fillFromTotal()" data-testid="quick-fill">Fill CGST / SGST / IGST</button>
          </div>
        }

        <div formArrayName="rows" class="rows">
          @for (row of rows.controls; track row; let i = $index) {
            <div class="row" [formGroupName]="i" data-testid="version-row">
              <div class="main">
                <label class="field-label" [for]="id + '-c' + i">Component</label>
                <select [id]="id + '-c' + i" class="field-input" formControlName="componentId">
                  <option value="" disabled>Select a component…</option>
                  @for (c of components(); track c.id) { <option [value]="c.id">{{ c.code }} — {{ c.name }}</option> }
                </select>
                <div class="field-hint">{{ applicabilityLabel(i) }}</div>
                @if (fieldErrors()['components[' + i + '].taxComponentId']; as msg) { <div class="field-error">{{ msg }}</div> }
              </div>
              <div>
                @if (methodOf(i) === 'AmountPerBaseUnit') {
                  <label class="field-label" [for]="id + '-a' + i">Amount per base unit</label>
                  <input [id]="id + '-a' + i" type="number" step="any" min="0" class="field-input" formControlName="amountPerBaseUnit" />
                  @if (fieldErrors()['components[' + i + '].amountPerBaseUnit']; as msg) { <div class="field-error">{{ msg }}</div> }
                } @else {
                  <label class="field-label" [for]="id + '-r' + i">Rate %</label>
                  <input [id]="id + '-r' + i" type="number" step="any" min="0" max="100" class="field-input" formControlName="ratePercent" data-testid="row-rate" />
                  @if (fieldErrors()['components[' + i + '].ratePercent']; as msg) { <div class="field-error">{{ msg }}</div> }
                }
              </div>
              <div class="actions"><button type="button" class="link danger" (click)="removeRow(i)" aria-label="Remove component">Remove</button></div>
            </div>
          }
        </div>
        <button type="button" class="link" (click)="addRow()" data-testid="add-row">+ Add component (for example Cess)</button>

        <p class="small muted" data-testid="version-total">Total rate within one supply: <span class="strong">{{ total() }}%</span></p>
        @for (msg of ruleErrors(); track msg) { <div class="field-error" data-testid="rule-error">{{ msg }}</div> }
        @if (fieldErrors()['components']; as msg) { <div class="field-error">{{ msg }}</div> }
      } @else {
        <p class="small muted">This treatment carries no tax components; only the date is needed.</p>
      }
    </div>
  `,
})
export class TaxVersionForm {
  private readonly fb = inject(FormBuilder);
  private static nextId = 0;
  readonly id = `tvf${++TaxVersionForm.nextId}`;

  readonly regime = input.required<TaxRegime>();
  readonly treatment = input.required<TaxTreatment>();
  /** Active components of the profile's country. */
  readonly components = input<TaxComponent[]>([]);
  /** Start of the latest existing version (append-only rule), when adding to an existing profile. */
  readonly latestFrom = input<string | null>(null);

  readonly today = todayIso();
  readonly quickTotal = signal('');
  readonly fieldErrors = signal<Record<string, string>>({});
  private readonly drafts = signal<VersionLineDraft[]>([]);
  private showRuleErrors = signal(false);

  readonly form = this.fb.nonNullable.group({
    effectiveFrom: [todayIso()],
    rows: this.fb.array<RowGroup>([]),
  });

  get rows(): FormArray<RowGroup> {
    return this.form.controls.rows;
  }

  readonly total = computed(() => totalPercent(this.drafts()));
  readonly ruleErrors = computed(() => (this.showRuleErrors() ? validateVersionLines(this.regime(), this.treatment(), this.drafts()) : []));

  constructor() {
    this.form.valueChanges.subscribe(() => this.refreshDrafts());
    effect(() => {
      this.regime();
      this.treatment();
      this.components();
      untracked(() => this.seedRows());
    });
  }

  methodOf(i: number): 'Percent' | 'AmountPerBaseUnit' {
    return this.componentOf(this.rows.at(i).controls.componentId.value)?.method ?? 'Percent';
  }

  applicabilityLabel(i: number): string {
    const c = this.componentOf(this.rows.at(i).controls.componentId.value);
    if (!c) return '';
    const a = defaultApplicability(c.kind);
    return a === 'IntraState' ? 'Intra-state (same state)' : a === 'InterState' ? 'Inter-state (different state)' : 'Always applies';
  }

  addRow(componentId = '', rate: number | null = null): void {
    this.rows.push(
      this.fb.nonNullable.group({
        componentId: [componentId],
        ratePercent: new FormControl<number | null>(rate),
        amountPerBaseUnit: new FormControl<number | null>(null),
      }) as RowGroup,
    );
  }

  removeRow(i: number): void {
    this.rows.removeAt(i);
  }

  /** Fills CGST = SGST = total / 2 and IGST = total from the typed total GST rate. */
  fillFromTotal(): void {
    const total = Number(this.quickTotal());
    if (!Number.isFinite(total) || total <= 0) return;
    const { half, igst } = splitGstRate(total);
    for (const row of this.rows.controls) {
      const kind = this.componentOf(row.controls.componentId.value)?.kind;
      if (kind === 'Cgst' || kind === 'Sgst' || kind === 'Utgst') row.controls.ratePercent.setValue(half);
      else if (kind === 'Igst') row.controls.ratePercent.setValue(igst);
    }
  }

  /** Validates (client rules, T19) and returns the request, or null with the errors shown. */
  build(): AddTaxVersionRequest | null {
    this.showRuleErrors.set(true);
    this.refreshDrafts();
    const errors: Record<string, string> = {};
    const dateError = validateEffectiveFrom(this.form.controls.effectiveFrom.value, this.today, this.latestFrom());
    if (dateError) errors['effectiveFrom'] = dateError;
    this.fieldErrors.set(errors);
    const taxable = this.treatment() === 'Taxable';
    if (dateError || (taxable && validateVersionLines(this.regime(), this.treatment(), this.drafts()).length > 0)) return null;
    if (taxable && this.rows.controls.some((r) => !r.controls.componentId.value)) {
      this.fieldErrors.set({ components: 'Select a component for every row.' });
      return null;
    }
    return {
      effectiveFrom: this.form.controls.effectiveFrom.value,
      components: taxable
        ? this.rows.controls.map((r) => {
            const c = this.componentOf(r.controls.componentId.value)!;
            return {
              taxComponentId: c.id,
              applicability: defaultApplicability(c.kind),
              ratePercent: c.method === 'Percent' ? r.controls.ratePercent.value : null,
              amountPerBaseUnit: c.method === 'AmountPerBaseUnit' ? r.controls.amountPerBaseUnit.value : null,
            };
          })
        : [],
    };
  }

  /** Shows the server's field errors (`effectiveFrom`, `components`, `components[i].ratePercent`, ...). */
  applyServerErrors(problem: ApiProblem | undefined): void {
    this.fieldErrors.set(problemFields(problem));
  }

  private componentOf(id: string): TaxComponent | undefined {
    return this.components().find((c) => c.id === id);
  }

  private refreshDrafts(): void {
    this.drafts.set(
      this.rows.controls.flatMap((r) => {
        const c = this.componentOf(r.controls.componentId.value);
        if (!c) return [];
        return [{
          code: c.code, kind: c.kind, method: c.method, applicability: defaultApplicability(c.kind),
          ratePercent: r.controls.ratePercent.value, amountPerBaseUnit: r.controls.amountPerBaseUnit.value,
        }];
      }),
    );
  }

  /** Default rows: GST = CGST + SGST (or UTGST) + IGST; VAT = the VAT component. */
  private seedRows(): void {
    this.rows.clear();
    this.showRuleErrors.set(false);
    if (this.treatment() === 'Taxable') {
      const first = (...kinds: string[]) => this.components().find((c) => kinds.includes(c.kind));
      const wanted = this.regime() === 'Gst' ? [first('Cgst'), first('Sgst', 'Utgst'), first('Igst')] : [first('Vat')];
      for (const c of wanted) this.addRow(c?.id ?? '');
    }
    this.refreshDrafts();
  }
}
