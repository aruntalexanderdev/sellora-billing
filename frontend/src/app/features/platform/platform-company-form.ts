import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { applyProblem, problemMessage, toProblem } from '../../core/http/api-errors';
import { GeoSelect } from '../../shared/geo/geo-select';
import { UiButton } from '../../shared/ui/button/button';
import { ToastService } from '../../shared/ui/toast/toast.service';
import type { HasUnsavedChanges } from '../../core/unsaved-changes.guard';
import type { CreatePlatformCompanyRequest, PlatformCurrencyOption } from './platform.models';
import { PlatformService } from './platform.service';

const blank = (s: string): string | null => s.trim() || null;

/** Builds the create request: exactly one of an existing owner user id or a new owner user (mirrors the backend rule). */
export function buildCompanyRequest(
  v: ReturnType<PlatformCompanyForm['form']['getRawValue']>, ownerMode: 'new' | 'existing',
): CreatePlatformCompanyRequest {
  const n = v.owner.newUser;
  return {
    code: v.code.trim(),
    name: v.name.trim(),
    businessName: v.businessName.trim(),
    invoicePrefix: v.invoicePrefix.trim(),
    baseCurrencyId: v.baseCurrencyId,
    gstin: blank(v.gstin),
    addressLine: blank(v.addressLine),
    city: blank(v.city),
    phone: blank(v.phone),
    email: blank(v.email),
    owner:
      ownerMode === 'existing'
        ? { userId: v.owner.userId.trim(), newUser: null }
        : {
            userId: null,
            newUser: {
              firstName: n.firstName.trim(), lastName: n.lastName.trim(), username: n.username.trim(), email: n.email.trim(),
              phoneNumber: blank(n.phoneNumber), password: n.password, confirmPassword: n.confirmPassword,
            },
          },
    reason: blank(v.reason),
    countryId: blank(v.countryId),
    stateId: blank(v.stateId),
  };
}

@Component({
  selector: 'app-platform-company-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton, GeoSelect],
  templateUrl: './platform-company-form.html',
  styleUrl: './platform.scss',
})
export class PlatformCompanyForm implements HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(PlatformService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  readonly form = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.maxLength(20)]],
    name: ['', [Validators.required, Validators.maxLength(150)]],
    businessName: ['', [Validators.required, Validators.maxLength(150)]],
    invoicePrefix: ['', [Validators.required, Validators.maxLength(10)]],
    baseCurrencyId: ['', Validators.required],
    gstin: [''],
    addressLine: [''],
    city: [''],
    countryId: [''],
    stateId: [''],
    phone: [''],
    email: ['', Validators.email],
    owner: this.fb.nonNullable.group({
      userId: [''],
      newUser: this.fb.nonNullable.group({
        firstName: ['', Validators.required],
        lastName: ['', Validators.required],
        username: ['', Validators.required],
        email: ['', [Validators.required, Validators.email]],
        phoneNumber: [''],
        password: ['', [Validators.required, Validators.minLength(8)]],
        confirmPassword: ['', Validators.required],
      }),
    }),
    reason: [''],
  });

  readonly ownerMode = signal<'new' | 'existing'>('new');
  /** Onboarding uses the platform lookups (no company token), D10. */
  readonly geoLookup = this.service;
  readonly currencies = signal<PlatformCurrencyOption[]>([]);
  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  /** company_creation_disabled: the platform has switched company creation off, so the form is unusable. */
  readonly disabled = signal(false);
  private saved = false;

  constructor() {
    this.setOwnerMode('new');
    this.service.activeCurrencies().subscribe({ next: (c) => this.currencies.set(c), error: () => this.currencies.set([]) });
  }

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  setOwnerMode(mode: 'new' | 'existing'): void {
    this.ownerMode.set(mode);
    const owner = this.form.controls.owner;
    if (mode === 'new') {
      owner.controls.newUser.enable();
      owner.controls.userId.disable();
    } else {
      owner.controls.newUser.disable();
      owner.controls.userId.enable();
      owner.controls.userId.setValidators([Validators.required]);
      owner.controls.userId.updateValueAndValidity();
    }
  }

  submit(): void {
    this.serverError.set(null);
    this.setOwnerMode(this.ownerMode()); // keep enabled/disabled groups in sync before validating
    const owner = this.form.controls.owner.controls;
    if (this.ownerMode() === 'new' && owner.newUser.controls.password.value !== owner.newUser.controls.confirmPassword.value) {
      owner.newUser.controls.confirmPassword.setErrors({ mismatch: true });
    }
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.saving.set(true);
    this.service.createCompany(buildCompanyRequest(this.form.getRawValue(), this.ownerMode())).subscribe({
      next: (created) => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(`Company ${created.code} created.`);
        void this.router.navigate(['/platform/companies']);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        const problem = toProblem(err);
        if (problem?.code === 'company_creation_disabled') {
          this.disabled.set(true);
          this.serverError.set(problemMessage(problem));
          return;
        }
        // Field errors (e.g. "owner.newUser.password") map onto the nested controls; the rest goes to the banner.
        this.serverError.set(applyProblem(this.form, problem));
      },
    });
  }
}
