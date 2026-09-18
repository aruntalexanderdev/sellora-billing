import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { CurrenciesService } from '../../currencies/currencies.service';
import { ExchangeRatesService } from '../exchange-rates.service';
import type { CurrencyItem } from '../../currencies/currencies.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

@Component({
  selector: 'app-exchange-rate-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './exchange-rate-form.html',
  styleUrl: '../../users/user-form/user-form.scss',
})
export class ExchangeRateForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly ratesService = inject(ExchangeRatesService);
  private readonly currenciesService = inject(CurrenciesService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly rateId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.rateId !== null && this.rateId !== 'new');

  // Only active, non-base currencies can carry a rate — the base currency is always 1:1 with itself.
  readonly currencies = signal<CurrencyItem[]>([]);
  readonly baseCode = signal('');
  readonly baseSymbol = signal('');

  readonly form = this.fb.nonNullable.group({
    currencyId: ['', Validators.required],
    rateToBase: [null as number | null, [Validators.required, Validators.min(0.000001)]],
    effectiveDate: [today(), Validators.required],
  });

  readonly selectedCode = computed(() => this.currencies().find((c) => c.id === this.formCurrencyId())?.code ?? '');
  private readonly formCurrencyId = signal('');

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    this.form.controls.currencyId.valueChanges.subscribe((id) => this.formCurrencyId.set(id));

    this.currenciesService.list().subscribe({
      next: (list) => {
        const base = list.find((c) => c.isBase);
        this.baseCode.set(base?.code ?? '');
        this.baseSymbol.set(base?.symbol ?? '');
        this.currencies.set(list.filter((c) => c.isActive && !c.isBase));
      },
    });

    if (this.isEdit()) {
      this.ratesService.get(this.rateId!).subscribe({
        next: (r) => this.form.patchValue({ currencyId: r.currencyId, rateToBase: r.rateToBase, effectiveDate: r.effectiveDate }),
      });
    }
  }

  submit(): void {
    this.serverError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    const v = this.form.getRawValue();
    const request = { currencyId: v.currencyId, rateToBase: v.rateToBase!, effectiveDate: v.effectiveDate };
    const request$ = this.isEdit() ? this.ratesService.update(this.rateId!, request) : this.ratesService.create(request);

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(this.isEdit() ? 'Exchange rate updated.' : 'Exchange rate added.');
        this.router.navigateByUrl('/administration/exchange-rates');
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        const problem = err.error as ApiProblem | undefined;
        if (problem?.errors) {
          let matched = false;
          for (const [field, messages] of Object.entries(problem.errors)) {
            const control = this.form.get(field.charAt(0).toLowerCase() + field.slice(1));
            if (control) {
              control.setErrors({ server: messages[0] });
              matched = true;
            }
          }
          if (matched) return;
        }
        this.serverError.set(problem?.title ?? Object.values(problem?.errors ?? {})[0]?.[0] ?? 'Something went wrong.');
      },
    });
  }
}

function today(): string {
  return new Date().toISOString().slice(0, 10);
}
