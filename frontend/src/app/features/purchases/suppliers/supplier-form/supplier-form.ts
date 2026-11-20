import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { GeoSelect } from '../../../../shared/geo/geo-select';
import type { GeoStoredLink } from '../../../../shared/geo/geo.models';
import { AuthService } from '../../../../core/auth/auth.service';
import { SettingsService } from '../../../masters/settings/settings.service';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { SuppliersService } from '../suppliers.service';
import { CurrenciesService } from '../../../administration/currencies/currencies.service';
import type { CurrencyItem } from '../../../administration/currencies/currencies.models';

import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

@Component({
  selector: 'app-supplier-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton, GeoSelect],
  templateUrl: './supplier-form.html',
  styleUrl: '../../../administration/users/user-form/user-form.scss',
})
export class SupplierForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly suppliersService = inject(SuppliersService);
  private readonly currenciesService = inject(CurrenciesService);
  private readonly toast = inject(ToastService);
  private readonly auth = inject(AuthService);
  private readonly settingsService = inject(SettingsService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly supplierId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.supplierId !== null && this.supplierId !== 'new');

  readonly form = this.fb.nonNullable.group({
    name: ['', Validators.required],
    city: [''],
    gstin: [''],
    paymentTerms: [''],
    currencyId: ['', Validators.required],
    countryId: [''],
    stateId: [''],
  });

  readonly currencies = signal<CurrencyItem[]>([]);
  private supplierCurrencyId: string | null = null;

  readonly storedCountry = signal<GeoStoredLink | null>(null);
  readonly storedState = signal<GeoStoredLink | null>(null);
  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    this.defaultCountry();
    this.currenciesService.list().subscribe({
      next: (list) => {
        this.currencies.set(list);
        // New suppliers default to the business base currency.
        if (!this.isEdit()) this.form.patchValue({ currencyId: list.find((c) => c.isBase)?.id ?? '' }, { emitEvent: false });
      },
    });

    if (this.isEdit()) {
      this.suppliersService.get(this.supplierId!).subscribe({
        next: (s) => {
          this.supplierCurrencyId = s.currencyId;
          this.form.patchValue({ name: s.name, city: s.city ?? '', gstin: s.gstin ?? '', paymentTerms: s.paymentTerms ?? '', currencyId: s.currencyId, countryId: s.countryId ?? '', stateId: s.stateId ?? '' });
          this.setStored(s);
        },
      });
    }
  }

  // A supplier already on a since-deactivated currency keeps it listed so the form can still be saved.
  isSelectable(c: CurrencyItem): boolean {
    return c.isActive || c.id === this.supplierCurrencyId;
  }

  private setStored(x: { countryId: string | null; countryName: string | null; stateId: string | null; stateName: string | null }): void {
    this.storedCountry.set(x.countryId ? { id: x.countryId, name: x.countryName ?? '' } : null);
    this.storedState.set(x.stateId ? { id: x.stateId, name: x.stateName ?? '' } : null);
  }

  /** D7/D27: a new record defaults to the Business Settings country (state stays empty); the user can change it. */
  private defaultCountry(): void {
    if (this.isEdit() || !this.auth.hasPermission('Masters.View')) return;
    this.settingsService.get().subscribe({
      next: (s) => {
        if (s.countryId && !this.form.controls.countryId.value) this.form.controls.countryId.setValue(s.countryId);
      },
      error: () => undefined,
    });
  }

  /** Always posts the current links (null clears, D22). */
  private buildRequest() {
    const v = this.form.getRawValue();
    return { ...v, countryId: v.countryId || null, stateId: v.countryId ? v.stateId || null : null };
  }

  submit(): void {
    this.serverError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    const request$ = this.isEdit()
      ? this.suppliersService.update(this.supplierId!, this.buildRequest())
      : this.suppliersService.create(this.buildRequest());

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(this.isEdit() ? 'Supplier updated.' : 'Supplier created.');
        this.router.navigateByUrl('/purchases/suppliers');
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
