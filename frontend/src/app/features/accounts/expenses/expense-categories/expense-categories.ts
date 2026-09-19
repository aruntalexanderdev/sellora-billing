import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { ExpensesService } from '../expenses.service';
import type { ExpenseCategory } from '../expenses.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';

@Component({
  selector: 'app-expense-categories',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton, UiBadge],
  templateUrl: './expense-categories.html',
  styleUrl: './expense-categories.scss',
})
export class ExpenseCategories implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly expensesService = inject(ExpensesService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);

  readonly categories = signal<ExpenseCategory[]>([]);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly editingId = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group({ name: ['', [Validators.required, Validators.maxLength(60)]] });

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.expensesService.categories().subscribe({
      next: (list) => {
        this.categories.set(list);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  startAdd(): void {
    this.editingId.set(null);
    this.error.set(null);
    this.form.reset({ name: '' });
  }

  startRename(c: ExpenseCategory): void {
    this.editingId.set(c.id);
    this.error.set(null);
    this.form.reset({ name: c.name });
  }

  submit(): void {
    if (this.saving()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const name = this.form.getRawValue().name.trim();
    const id = this.editingId();
    this.error.set(null);
    this.saving.set(true);
    (id ? this.expensesService.renameCategory(id, name) : this.expensesService.createCategory(name)).subscribe({
      next: () => {
        this.saving.set(false);
        this.toast.show(id ? 'Category renamed.' : 'Category added.');
        this.startAdd();
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        const problem = err.error as ApiProblem | undefined;
        this.error.set(Object.values(problem?.errors ?? {})[0]?.[0] ?? problem?.title ?? 'Could not save this category.');
      },
    });
  }

  toggle(c: ExpenseCategory): void {
    const disabling = c.isActive;
    this.confirm
      .ask({
        title: disabling ? `Deactivate ${c.name}?` : `Activate ${c.name}?`,
        body: disabling
          ? 'It will no longer be selectable on new expenses. Existing expenses keep it.'
          : 'It will be selectable on new expenses again.',
        confirmLabel: disabling ? 'Deactivate' : 'Activate',
        destructive: disabling,
      })
      .then((confirmed) => {
        if (!confirmed) return;
        this.expensesService.setCategoryActive(c.id, !c.isActive).subscribe({
          next: () => {
            this.toast.show(`${c.name} ${disabling ? 'deactivated' : 'activated'}.`);
            this.load();
          },
          error: (err: HttpErrorResponse) => this.toast.show(err.error?.title ?? 'Could not update this category.'),
        });
      });
  }
}
