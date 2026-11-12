import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { CashBankService } from '../cash-bank.service';
import type { FinancialAccount } from '../cash-bank.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

const todayIso = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

const money = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

type Preset = 'deposit' | 'withdrawal' | 'transfer';

@Component({
  selector: 'app-transfer-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './transfer-form.html',
  styleUrl: '../../../administration/users/user-form/user-form.scss',
})
export class TransferForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(CashBankService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly accounts = signal<FinancialAccount[]>([]);
  readonly symbol = signal('₹');
  readonly preset = signal<Preset>('transfer');

  readonly form = this.fb.nonNullable.group({
    fromAccountId: ['', Validators.required],
    toAccountId: ['', Validators.required],
    amount: [null as number | null, [Validators.required, Validators.min(0.01)]],
    transferDate: [todayIso(), Validators.required],
    notes: [''],
  });
  private readonly value = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });

  // Only active accounts can send or receive money.
  private readonly active = computed(() => this.accounts().filter((a) => a.isActive));
  readonly fromChoices = computed(() => this.filterByPreset(this.active(), 'from'));
  readonly toChoices = computed(() => this.filterByPreset(this.active(), 'to').filter((a) => a.id !== this.value().fromAccountId));
  readonly from = computed(() => this.accounts().find((a) => a.id === this.value().fromAccountId) ?? null);
  readonly to = computed(() => this.accounts().find((a) => a.id === this.value().toAccountId) ?? null);
  readonly fmt = (n: number | null) => (n === null ? '—' : this.symbol() + money(n));

  // The sending account is not blocked at zero (a bank account may overdraw), but going negative is worth a warning.
  readonly overdraws = computed(() => {
    const from = this.from();
    const amount = this.value().amount ?? 0;
    return !!from && from.balance !== null && amount > from.balance;
  });

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    this.service.list().subscribe({
      next: (d) => {
        this.accounts.set(d.items);
        this.symbol.set(d.baseCurrencySymbol);
        const from = this.route.snapshot.queryParamMap.get('from');
        if (from) this.form.patchValue({ fromAccountId: from });
        this.form.markAsPristine();
      },
    });
  }

  setPreset(p: Preset): void {
    this.preset.set(p);
    this.form.patchValue({ fromAccountId: '', toAccountId: '' });
  }

  private filterByPreset(list: FinancialAccount[], side: 'from' | 'to'): FinancialAccount[] {
    const p = this.preset();
    if (p === 'transfer') return list;
    // A deposit takes cash to the bank; a withdrawal brings cash back from it.
    const cashSide = (p === 'deposit') === (side === 'from');
    return list.filter((a) => (cashSide ? a.kind === 'Cash' : a.kind === 'Bank'));
  }

  submit(): void {
    this.serverError.set(null);
    if (this.saving()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const v = this.form.getRawValue();
    this.saving.set(true);
    this.service
      .transfer({ fromAccountId: v.fromAccountId, toAccountId: v.toAccountId, amount: v.amount!, transferDate: v.transferDate, notes: v.notes || null })
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.saved = true;
          this.toast.show('Transfer recorded.');
          this.router.navigateByUrl('/accounts/cash-bank');
        },
        error: (err: HttpErrorResponse) => {
          this.saving.set(false);
          const problem = err.error as ApiProblem | undefined;
          if (problem?.errors) {
            for (const [field, messages] of Object.entries(problem.errors)) {
              this.form.get(field.charAt(0).toLowerCase() + field.slice(1))?.setErrors({ server: messages[0] });
            }
            return;
          }
          this.serverError.set(problem?.title ?? 'Something went wrong.');
        },
      });
  }
}
