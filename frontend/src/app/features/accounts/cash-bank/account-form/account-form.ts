import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { CashBankService } from '../cash-bank.service';
import type { AccountKind } from '../cash-bank.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

const todayIso = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

@Component({
  selector: 'app-account-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './account-form.html',
  styleUrl: '../../../administration/users/user-form/user-form.scss',
})
export class AccountForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(CashBankService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly accountId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.accountId !== null);
  readonly kinds: AccountKind[] = ['Cash', 'Bank'];

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(60)]],
    kind: ['Cash' as AccountKind, Validators.required],
    reference: [''],
    openingBalance: [0, [Validators.required, Validators.min(0)]],
    openingDate: [todayIso(), Validators.required],
  });

  readonly kind = signal<AccountKind>('Cash');
  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    if (this.isEdit()) {
      this.service.get(this.accountId!).subscribe({
        next: (a) => {
          this.form.patchValue({ name: a.name, kind: a.kind, reference: a.reference ?? '' });
          this.kind.set(a.kind);
          this.form.controls.kind.disable();
          this.form.markAsPristine();
        },
      });
    }
    this.form.controls.kind.valueChanges.subscribe((k) => this.kind.set(k));
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
    const request$ = this.isEdit()
      ? this.service.update(this.accountId!, { name: v.name, reference: v.reference || null })
      : this.service.create({ name: v.name, kind: v.kind, reference: v.reference || null, openingBalance: v.openingBalance, openingDate: v.openingDate });

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(this.isEdit() ? 'Account updated.' : 'Account created.');
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
