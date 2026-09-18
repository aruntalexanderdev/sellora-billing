import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { CurrenciesService } from '../currencies.service';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

@Component({
  selector: 'app-currency-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './currency-form.html',
  styleUrl: '../../users/user-form/user-form.scss',
})
export class CurrencyForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly currenciesService = inject(CurrenciesService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly currencyId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.currencyId !== null && this.currencyId !== 'new');

  readonly form = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(/^[A-Za-z]{3}$/)]],
    name: ['', Validators.required],
    symbol: ['', Validators.required],
    decimalPrecision: [2, [Validators.required, Validators.min(0), Validators.max(6)]],
  });

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    if (this.isEdit()) {
      this.currenciesService.list().subscribe({
        next: (currencies) => {
          const c = currencies.find((x) => x.id === this.currencyId);
          if (c) this.form.patchValue({ code: c.code, name: c.name, symbol: c.symbol, decimalPrecision: c.decimalPrecision });
        },
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
    const raw = this.form.getRawValue();
    const request = { ...raw, code: raw.code.trim().toUpperCase() };
    const request$ = this.isEdit() ? this.currenciesService.update(this.currencyId!, request) : this.currenciesService.create(request);

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(this.isEdit() ? 'Currency updated.' : 'Currency created.');
        this.router.navigateByUrl('/administration/currencies');
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
