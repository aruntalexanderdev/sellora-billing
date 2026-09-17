import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { SettingsService } from '../settings.service';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

@Component({
  selector: 'app-settings-page',
  standalone: true,
  imports: [ReactiveFormsModule, UiButton],
  templateUrl: './settings-page.html',
  styleUrl: '../../../administration/users/user-form/user-form.scss',
})
export class SettingsPage implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly settingsService = inject(SettingsService);
  private readonly toast = inject(ToastService);
  private readonly auth = inject(AuthService);

  readonly canEdit = computed(() => this.auth.hasPermission('Masters.Edit'));

  readonly form = this.fb.nonNullable.group({
    businessName: ['', Validators.required],
    addressLine: [''],
    city: [''],
    state: [''],
    gstin: [''],
    phone: [''],
    email: ['', Validators.email],
    invoicePrefix: ['', Validators.required],
  });

  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    this.settingsService.get().subscribe({
      next: (s) => {
        this.form.patchValue({
          businessName: s.businessName,
          addressLine: s.addressLine ?? '',
          city: s.city ?? '',
          state: s.state ?? '',
          gstin: s.gstin ?? '',
          phone: s.phone ?? '',
          email: s.email ?? '',
          invoicePrefix: s.invoicePrefix,
        });
        if (!this.canEdit()) this.form.disable();
        this.loading.set(false);
      },
    });
  }

  submit(): void {
    this.serverError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    this.settingsService.update(this.form.getRawValue()).subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.form.markAsPristine();
        this.toast.show('Business settings saved.');
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        const problem = err.error as ApiProblem | undefined;
        this.serverError.set(problem?.title ?? 'Something went wrong.');
      },
    });
  }
}
