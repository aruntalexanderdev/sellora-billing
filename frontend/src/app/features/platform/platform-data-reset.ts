import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { applyProblem, problemMessage, toProblem } from '../../core/http/api-errors';
import { UiButton } from '../../shared/ui/button/button';
import type { DataResetOptions, DataResetReport } from './platform.models';
import { PlatformService } from './platform.service';

export const DEFAULT_RESET_OPTIONS: DataResetOptions = {
  wipeCustomers: false, wipeSuppliers: false, keepTaxes: false, keepCategories: false, keepExpenseCategories: false, keepAccounts: false,
};

export const OPTION_GROUPS: { title: string; items: { key: keyof DataResetOptions; label: string }[] }[] = [
  { title: 'Also remove', items: [{ key: 'wipeCustomers', label: 'Customers' }, { key: 'wipeSuppliers', label: 'Suppliers' }] },
  {
    title: 'Keep',
    items: [
      { key: 'keepTaxes', label: 'Taxes' },
      { key: 'keepCategories', label: 'Product categories' },
      { key: 'keepExpenseCategories', label: 'Expense categories' },
      { key: 'keepAccounts', label: 'Financial accounts' },
    ],
  },
];

/** Platform admin tool: returns the business data of the whole database to a fresh-setup state. Server switch Platform:DataResetEnabled. */
@Component({
  selector: 'app-platform-data-reset',
  standalone: true,
  imports: [ReactiveFormsModule, UiButton],
  templateUrl: './platform-data-reset.html',
  styleUrl: './platform-data-reset.scss',
})
export class PlatformDataReset implements OnInit {
  private readonly service = inject(PlatformService);
  private readonly fb = inject(FormBuilder);

  readonly groups = OPTION_GROUPS;
  /** 'checking' until the status call returns; 'off' = notice only. */
  readonly availability = signal<'checking' | 'off' | 'on'>('checking');
  readonly state = signal<'loading' | 'error' | 'ready'>('loading');
  readonly errorMessage = signal('');
  readonly options = signal<DataResetOptions>({ ...DEFAULT_RESET_OPTIONS });
  readonly report = signal<DataResetReport | null>(null);
  readonly result = signal<DataResetReport | null>(null);
  readonly submitting = signal(false);
  readonly confirmError = signal('');
  readonly refusal = signal('');
  readonly confirmForm = this.fb.nonNullable.group({ confirmDatabase: [''] });
  private loadSeq = 0;

  readonly database = computed(() => this.report()?.database ?? '');
  readonly tableCount = computed(() => this.report()?.tables.length ?? 0);
  readonly totalRows = computed(() => (this.report()?.tables ?? []).reduce((n, t) => n + t.rowsBefore, 0));
  readonly kept = computed(() => this.report()?.keptMasters ?? []);

  private readonly typed = signal('');

  readonly disabledReason = computed<string | null>(() => {
    if (this.submitting()) return 'A reset is already running.';
    if (!this.database() || this.typed() !== this.database()) return 'Type the database name exactly to enable the reset.';
    return null;
  });

  ngOnInit(): void {
    this.confirmForm.controls.confirmDatabase.valueChanges.subscribe((v) => this.typed.set(v));
    this.service.dataResetStatus().subscribe({
      next: (s) => {
        if (!s.enabled) {
          this.availability.set('off');
          return;
        }
        this.availability.set('on');
        this.loadPreview();
      },
      error: (err: HttpErrorResponse) => {
        if (toProblem(err)?.code === 'data_reset_disabled') {
          this.availability.set('off');
          return;
        }
        this.availability.set('on');
        this.errorMessage.set(problemMessage(toProblem(err), 'Could not check the data reset switch.'));
        this.state.set('error');
      },
    });
  }

  loadPreview(): void {
    const seq = ++this.loadSeq;
    this.state.set('loading');
    this.service.dataResetPreview(this.options()).subscribe({
      next: (r) => {
        if (seq !== this.loadSeq) return;
        this.report.set(r);
        this.state.set('ready');
      },
      error: (err: HttpErrorResponse) => {
        if (seq !== this.loadSeq) return;
        const problem = toProblem(err);
        if (problem?.code === 'data_reset_disabled') {
          this.availability.set('off');
          return;
        }
        this.errorMessage.set(problem?.status === 409 && problem.title ? problem.title : problemMessage(problem, 'Could not load the preview.'));
        this.state.set('error');
      },
    });
  }

  isChecked(key: keyof DataResetOptions): boolean {
    return this.options()[key];
  }

  setOption(key: keyof DataResetOptions, value: boolean): void {
    this.options.update((o) => ({ ...o, [key]: value }));
    this.refusal.set('');
    this.loadPreview();
  }

  reset(): void {
    if (this.disabledReason()) return;
    this.submitting.set(true);
    this.confirmError.set('');
    this.refusal.set('');
    this.service.dataReset(this.confirmForm.controls.confirmDatabase.value, this.options()).subscribe({
      next: (r) => {
        this.submitting.set(false);
        this.result.set(r);
        this.confirmForm.reset();
      },
      error: (err: HttpErrorResponse) => {
        this.submitting.set(false);
        const problem = toProblem(err);
        if (problem?.code === 'data_reset_disabled') {
          this.availability.set('off');
        } else if (err.status === 409) {
          this.refusal.set(problem?.title || problemMessage(problem));
        } else if (err.status === 400) {
          this.confirmError.set(applyProblem(this.confirmForm, problem) ?? '');
        } else {
          this.confirmError.set(problemMessage(problem, 'Could not reset the data.'));
        }
      },
    });
  }

  newPreview(): void {
    this.result.set(null);
    this.loadPreview();
  }

  fieldError(): string {
    const e = this.confirmForm.controls.confirmDatabase.errors?.['server'];
    return e ? String(e) : '';
  }
}
