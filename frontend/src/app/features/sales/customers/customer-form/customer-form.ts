import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { CustomersService } from '../customers.service';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

@Component({
  selector: 'app-customer-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './customer-form.html',
  styleUrl: '../../../administration/users/user-form/user-form.scss',
})
export class CustomerForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly customersService = inject(CustomersService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly customerId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.customerId !== null && this.customerId !== 'new');

  readonly form = this.fb.nonNullable.group({
    name: ['', Validators.required],
    phone: [''],
    address: [''],
  });

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    if (this.isEdit()) {
      this.customersService.get(this.customerId!).subscribe({
        next: (c) => this.form.patchValue({ name: c.name, phone: c.phone ?? '', address: c.address ?? '' }),
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
    const request$ = this.isEdit()
      ? this.customersService.update(this.customerId!, this.form.getRawValue())
      : this.customersService.create(this.form.getRawValue());

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(this.isEdit() ? 'Customer updated.' : 'Customer created.');
        this.router.navigateByUrl('/sales/customers');
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
