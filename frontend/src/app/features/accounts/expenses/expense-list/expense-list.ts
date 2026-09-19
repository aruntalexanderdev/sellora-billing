import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../../shared/ui/button/button';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { AuthService } from '../../../../core/auth/auth.service';
import { ExpensesService } from '../expenses.service';
import type { ExpenseCategory, ExpenseListItem, ExpenseStatus } from '../expenses.models';

const COLS: DataTableColumn[] = [
  { label: 'Expense', align: 'left' },
  { label: 'Date', align: 'left' },
  { label: 'Category', align: 'left' },
  { label: 'Method', align: 'left' },
  { label: 'Amount', align: 'right' },
  { label: 'Status', align: 'left' },
];

const PAGE_SIZE = 20;
const money = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-expense-list',
  standalone: true,
  imports: [UiDataTable, UiButton],
  templateUrl: './expense-list.html',
  styleUrl: './expense-list.scss',
})
export class ExpenseList implements OnInit {
  private readonly expensesService = inject(ExpensesService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly search = signal('');
  readonly categoryId = signal('');
  readonly status = signal<ExpenseStatus | ''>('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<ExpenseListItem[]>([]);
  readonly categories = signal<ExpenseCategory[]>([]);
  readonly total = signal(0);

  readonly canCreate = computed(() => this.auth.hasPermission('Expenses.Create'));
  readonly canManageCategories = computed(() => this.auth.hasPermission('Expenses.Categories'));
  readonly hasMore = computed(() => this.page() * PAGE_SIZE < this.total());

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((e) => ({
      cells: [
        { kind: 'link', text: e.expenseNo, onClick: () => this.router.navigate(['/accounts/expenses', e.id]) },
        { kind: 'text', text: e.expenseDate, color: 'var(--color-text-muted)' },
        { kind: 'text', text: e.categoryName, weight: '600' },
        { kind: 'text', text: e.paymentMethod, color: 'var(--color-text-muted)' },
        {
          kind: 'text',
          text: money(e.baseAmount),
          // A foreign-currency expense shows what was actually spent under its base-currency value.
          sub: e.currencyCode ? `${e.currencyCode} ${money(e.amount)}` : undefined,
          align: 'right',
          weight: '650',
        },
        { kind: 'badge', text: e.status, tone: e.status === 'Active' ? 'good' : 'mute' },
      ],
    })),
  );

  ngOnInit(): void {
    this.expensesService.categories().subscribe({ next: (c) => this.categories.set(c) });
    this.load();
  }

  onSearch(value: string): void {
    this.search.set(value);
    this.resetAndLoad();
  }

  onCategory(value: string): void {
    this.categoryId.set(value);
    this.resetAndLoad();
  }

  onStatus(value: string): void {
    this.status.set(value as ExpenseStatus | '');
    this.resetAndLoad();
  }

  onPageChange(page: number): void {
    this.page.set(page);
    this.load();
  }

  retry(): void {
    this.load();
  }

  goToCreate(): void {
    this.router.navigate(['/accounts/expenses/new']);
  }

  goToCategories(): void {
    this.router.navigate(['/accounts/expenses/categories']);
  }

  private resetAndLoad(): void {
    this.page.set(1);
    this.load();
  }

  private load(): void {
    this.state.set('loading');
    this.expensesService
      .list({
        search: this.search(),
        categoryId: this.categoryId() || undefined,
        status: this.status() || undefined,
        page: this.page(),
        pageSize: PAGE_SIZE,
      })
      .subscribe({
        next: (res) => {
          this.items.set(res.items);
          this.total.set(res.total);
          this.state.set('ready');
        },
        error: () => this.state.set('error'),
      });
  }
}
