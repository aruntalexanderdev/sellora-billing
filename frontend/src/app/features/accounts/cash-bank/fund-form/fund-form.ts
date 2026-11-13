import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { IdempotentSubmission } from '../../../../core/idempotency';
import { CustomersService } from '../../../sales/customers/customers.service';
import type { CustomerItem } from '../../../sales/customers/customers.models';
import { CashBankService } from '../cash-bank.service';
import { FUNDING_SOURCES, type FinancialAccount, type FundingRequest, type FundingSource } from '../cash-bank.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

const todayIso = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

/** Which optional fields a source shows, and which of them are mandatory (the server re-validates). */
export interface SourceFields {
  counterparty: { label: string; required: boolean } | null;
  reference: { label: string; required: boolean } | null;
  customer: boolean;
  reason: boolean;
}

export function sourceFields(source: FundingSource): SourceFields {
  switch (source) {
    case 'OwnerCapital':
      return { counterparty: { label: 'Owner name', required: false }, reference: { label: 'Reference', required: false }, customer: false, reason: false };
    case 'Loan':
      return { counterparty: { label: 'Lender', required: false }, reference: { label: 'Loan reference', required: false }, customer: false, reason: false };
    case 'CustomerReceipt':
      return { counterparty: null, reference: { label: 'Receipt reference', required: true }, customer: true, reason: false };
    case 'Other':
      return { counterparty: { label: 'Counterparty', required: false }, reference: { label: 'Reference', required: false }, customer: false, reason: true };
    default:
      return { counterparty: null, reference: { label: 'Reference', required: false }, customer: false, reason: false };
  }
}

@Component({
  selector: 'app-fund-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './fund-form.html',
  styleUrl: '../../../administration/users/user-form/user-form.scss',
})
export class FundForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(CashBankService);
  private readonly customersService = inject(CustomersService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly accountId = this.route.snapshot.paramMap.get('id')!;
  readonly sources = FUNDING_SOURCES;
  readonly account = signal<FinancialAccount | null>(null);
  readonly customers = signal<CustomerItem[]>([]);

  readonly form = this.fb.nonNullable.group({
    source: ['OwnerCapital' as FundingSource, Validators.required],
    amount: [null as number | null, [Validators.required, Validators.min(0.01)]],
    fundingDate: [todayIso(), Validators.required],
    counterparty: [''],
    sourceReference: [''],
    customerId: [''],
    reason: [''],
    notes: [''],
  });
  private readonly value = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });
  readonly fields = computed(() => sourceFields(this.value().source ?? 'OwnerCapital'));
  readonly hint = computed(() => this.sources.find((s) => s.value === this.value().source)?.hint ?? '');
  readonly today = todayIso();

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private readonly submission = new IdempotentSubmission();
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    this.service.get(this.accountId).subscribe({ next: (a) => this.account.set(a) });
    this.customersService.list({ pageSize: 200 }).subscribe({ next: (r) => this.customers.set(r.items.filter((c) => c.isActive && !c.isWalkIn)) });
    this.form.controls.source.valueChanges.subscribe(() => this.applyRules());
    this.applyRules();
  }

  /** Required-ness follows the source; hidden fields are cleared so they are never sent. */
  private applyRules(): void {
    const f = sourceFields(this.form.controls.source.value);
    const set = (c: FormControl<string>, show: boolean, required: boolean) => {
      c.setValidators(required ? Validators.required : null);
      if (!show) c.reset();
      c.updateValueAndValidity({ emitEvent: false });
    };
    set(this.form.controls.sourceReference, !!f.reference, !!f.reference?.required);
    set(this.form.controls.counterparty, !!f.counterparty, false);
    set(this.form.controls.customerId, f.customer, f.customer);
    set(this.form.controls.reason, f.reason, f.reason);
  }

  private text(v: string): string | null {
    const t = v.trim();
    return t === '' ? null : t;
  }

  submit(): void {
    this.serverError.set(null);
    if (this.saving()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const f = this.fields();
    const body: FundingRequest = {
      source: v.source,
      amount: v.amount!,
      fundingDate: v.fundingDate,
      sourceReference: f.reference ? this.text(v.sourceReference) : null,
      counterparty: f.counterparty ? this.text(v.counterparty) : null,
      customerId: f.customer ? this.text(v.customerId) : null,
      reason: f.reason ? this.text(v.reason) : null,
      notes: this.text(v.notes),
    };
    this.saving.set(true);
    this.service.fund(this.accountId, body, this.submission.keyFor(body)).subscribe({
      next: () => {
        this.submission.succeeded();
        this.saving.set(false);
        this.saved = true;
        this.toast.show('Funds added.');
        this.router.navigate(['/accounts/cash-bank', this.accountId]);
      },
      error: (err: HttpErrorResponse) => {
        this.submission.failed(err);
        this.saving.set(false);
        const problem = err.error as ApiProblem | undefined;
        if (problem?.errors) {
          let unmapped: string | null = null;
          for (const [field, messages] of Object.entries(problem.errors)) {
            const control = this.form.get(field.charAt(0).toLowerCase() + field.slice(1));
            if (control) control.setErrors({ server: messages[0] });
            else unmapped ??= messages[0];
          }
          if (unmapped) this.serverError.set(unmapped);
          return;
        }
        this.serverError.set(problem?.title ?? 'Something went wrong.');
      },
    });
  }
}
