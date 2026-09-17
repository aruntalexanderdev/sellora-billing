import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { TaxesService } from '../taxes.service';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

@Component({
  selector: 'app-tax-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './tax-form.html',
  styleUrl: '../../../administration/users/user-form/user-form.scss',
})
export class TaxForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly taxesService = inject(TaxesService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly taxId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.taxId !== null && this.taxId !== 'new');

  readonly form = this.fb.nonNullable.group({
    name: ['', Validators.required],
    ratePercent: [0, [Validators.required, Validators.min(0), Validators.max(100)]],
  });

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    if (this.isEdit()) {
      this.taxesService.list().subscribe({
        next: (taxes) => {
          const tax = taxes.find((t) => t.id === this.taxId);
          if (tax) this.form.patchValue({ name: tax.name, ratePercent: tax.ratePercent });
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
    const request$ = this.isEdit()
      ? this.taxesService.update(this.taxId!, this.form.getRawValue())
      : this.taxesService.create(this.form.getRawValue());

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(this.isEdit() ? 'Tax updated.' : 'Tax created.');
        this.router.navigateByUrl('/administration/taxes');
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        const problem = err.error as ApiProblem | undefined;
        this.serverError.set(problem?.title ?? Object.values(problem?.errors ?? {})[0]?.[0] ?? 'Something went wrong.');
      },
    });
  }
}
