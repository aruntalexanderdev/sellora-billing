import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ExpensesService } from '../expenses.service';
import { CurrenciesService } from '../../../administration/currencies/currencies.service';
import { ExchangeRatesService } from '../../../administration/exchange-rates/exchange-rates.service';
import type { CurrencyItem } from '../../../administration/currencies/currencies.models';
import type { ResolvedExchangeRate } from '../../../administration/exchange-rates/exchange-rates.models';
import type { ExpenseCategory, PaymentMethod } from '../expenses.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

const roundTo = (n: number, places: number) => {
  const f = 10 ** places;
  return (Math.sign(n) * Math.round(Math.abs(n) * f + Number.EPSILON)) / f;
};

const todayIso = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

@Component({
  selector: 'app-expense-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './expense-form.html',
  styleUrl: '../../../administration/users/user-form/user-form.scss',
})
export class ExpenseForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly expensesService = inject(ExpensesService);
  private readonly currenciesService = inject(CurrenciesService);
  private readonly exchangeRatesService = inject(ExchangeRatesService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  readonly paymentMethods: PaymentMethod[] = ['Cash', 'Upi', 'Card', 'Credit', 'BankTransfer'];

  readonly form = this.fb.nonNullable.group({
    categoryId: ['', Validators.required],
    expenseDate: [todayIso(), Validators.required],
    currencyId: ['', Validators.required],
    amount: [null as number | null, [Validators.required, Validators.min(0.000001)]],
    paymentMethod: ['Cash' as PaymentMethod, Validators.required],
    reference: [''],
    notes: [''],
  });

  readonly categories = signal<ExpenseCategory[]>([]);
  readonly currencies = signal<CurrencyItem[]>([]);
  readonly resolvedRate = signal<ResolvedExchangeRate | null>(null);
  readonly rateError = signal<string | null>(null);

  private readonly value = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });
  readonly currency = computed(() => this.currencies().find((c) => c.id === this.value().currencyId) ?? null);
  readonly isForeign = computed(() => !!this.currency() && !this.currency()!.isBase);
  readonly amountStep = computed(() => String(1 / 10 ** (this.currency()?.decimalPrecision ?? 2)));

  // Previews what the server will record: the amount rounded to the currency's precision, converted at the
  // rate for the expense date. The server re-resolves both, so this is informational only.
  readonly basePreview = computed(() => {
    const c = this.currency();
    const rate = this.resolvedRate();
    const amount = this.value().amount;
    if (!c || !rate || amount == null || amount <= 0) return null;
    return roundTo(roundTo(amount, c.decimalPrecision) * rate.rateToBase, rate.baseCurrencyDecimalPrecision);
  });

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    this.expensesService.categories().subscribe({ next: (list) => this.categories.set(list.filter((c) => c.isActive)) });
    this.currenciesService.list().subscribe({
      next: (list) => {
        const active = list.filter((c) => c.isActive);
        this.currencies.set(active);
        this.form.patchValue({ currencyId: active.find((c) => c.isBase)?.id ?? '' });
        this.form.markAsPristine();
        this.refreshRate();
      },
    });

    this.form.controls.currencyId.valueChanges.subscribe(() => this.refreshRate());
    this.form.controls.expenseDate.valueChanges.subscribe(() => this.refreshRate());
  }

  private refreshRate(): void {
    const { currencyId, expenseDate } = this.form.getRawValue();
    this.rateError.set(null);
    this.resolvedRate.set(null);
    if (!currencyId || !expenseDate) return;

    this.exchangeRatesService.resolve(currencyId, expenseDate).subscribe({
      next: (rate) => {
        // Ignore a stale answer if the selection moved on while it was in flight.
        const now = this.form.getRawValue();
        if (now.currencyId === currencyId && now.expenseDate === expenseDate) this.resolvedRate.set(rate);
      },
      error: (err: HttpErrorResponse) => {
        const problem = err.error as ApiProblem | undefined;
        this.rateError.set(Object.values(problem?.errors ?? {})[0]?.[0] ?? problem?.title ?? 'No exchange rate is available for this date.');
      },
    });
  }

  submit(): void {
    this.serverError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    // A second click while the first request is in flight must not record a second expense.
    if (this.saving()) return;

    const v = this.form.getRawValue();
    this.saving.set(true);
    this.expensesService
      .create({
        categoryId: v.categoryId,
        expenseDate: v.expenseDate,
        currencyId: v.currencyId,
        amount: v.amount!,
        paymentMethod: v.paymentMethod,
        reference: v.reference || null,
        notes: v.notes || null,
      })
      .subscribe({
        next: (e) => {
          this.saving.set(false);
          this.saved = true;
          this.toast.show(`${e.expenseNo} recorded.`);
          this.router.navigate(['/accounts/expenses', e.id]);
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
