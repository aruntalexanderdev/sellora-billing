import { HttpErrorResponse } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { ExpensesService } from '../expenses.service';
import type { ExpenseCategory, ExpenseDetail as ExpenseDetailModel } from '../expenses.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';

const FORMAT = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-expense-detail',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, DatePipe, UiButton, UiBadge],
  templateUrl: './expense-detail.html',
  styleUrl: '../../../purchases/purchase-detail/purchase-detail.scss',
})
export class ExpenseDetail implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly expensesService = inject(ExpensesService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly auth = inject(AuthService);

  readonly expense = signal<ExpenseDetailModel | null>(null);
  readonly categories = signal<ExpenseCategory[]>([]);
  readonly loading = signal(true);
  readonly editing = signal(false);
  readonly cancelling = signal(false);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  readonly own = (n: number) => (this.expense()?.currencySymbol ?? '') + FORMAT(n);
  readonly base = (n: number) => (this.expense()?.baseCurrencySymbol ?? '₹') + FORMAT(n);
  readonly isForeign = computed(() => {
    const e = this.expense();
    return !!e && e.currencyCode !== e.baseCurrencyCode;
  });

  readonly active = computed(() => this.expense()?.status === 'Active');
  readonly canEdit = computed(() => this.active() && this.auth.hasPermission('Expenses.Edit'));
  readonly canCancel = computed(() => this.active() && this.auth.hasPermission('Expenses.Cancel'));

  readonly editForm = this.fb.nonNullable.group({
    categoryId: ['', Validators.required],
    reference: [''],
    notes: [''],
  });
  readonly cancelForm = this.fb.nonNullable.group({ reason: [''] });

  // Active categories, plus the expense's own category if it has since been deactivated.
  readonly selectableCategories = computed(() =>
    this.categories().filter((c) => c.isActive || c.id === this.expense()?.categoryId),
  );

  ngOnInit(): void {
    this.expensesService.categories().subscribe({ next: (c) => this.categories.set(c) });
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.expensesService.get(this.route.snapshot.paramMap.get('id')!).subscribe({
      next: (e) => {
        this.expense.set(e);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  startEdit(): void {
    const e = this.expense()!;
    this.editForm.reset({ categoryId: e.categoryId, reference: e.reference ?? '', notes: e.notes ?? '' });
    this.error.set(null);
    this.cancelling.set(false);
    this.editing.set(true);
  }

  saveEdit(): void {
    if (this.saving()) return;
    if (this.editForm.invalid) {
      this.editForm.markAllAsTouched();
      return;
    }
    const v = this.editForm.getRawValue();
    this.saving.set(true);
    this.expensesService.update(this.expense()!.id, { categoryId: v.categoryId, reference: v.reference || null, notes: v.notes || null }).subscribe({
      next: (e) => {
        this.saving.set(false);
        this.expense.set(e);
        this.editing.set(false);
        this.toast.show('Expense updated.');
      },
      error: (err) => this.fail(err),
    });
  }

  startCancel(): void {
    this.cancelForm.reset({ reason: '' });
    this.error.set(null);
    this.editing.set(false);
    this.cancelling.set(true);
  }

  confirmCancel(): void {
    if (this.saving()) return;
    this.saving.set(true);
    this.expensesService.cancel(this.expense()!.id, this.cancelForm.getRawValue().reason || null).subscribe({
      next: (e) => {
        this.saving.set(false);
        this.expense.set(e);
        this.cancelling.set(false);
        this.toast.show(`${e.expenseNo} cancelled.`);
      },
      error: (err) => this.fail(err),
    });
  }

  private fail(err: HttpErrorResponse): void {
    this.saving.set(false);
    const problem = err.error as ApiProblem | undefined;
    this.error.set(Object.values(problem?.errors ?? {})[0]?.[0] ?? problem?.title ?? 'Something went wrong.');
  }
}
