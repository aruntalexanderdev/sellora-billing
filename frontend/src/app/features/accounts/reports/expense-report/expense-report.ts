import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableFilter, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { AccountsReportsService } from '../accounts-reports.service';
import { ExpensesService } from '../../expenses/expenses.service';
import { ReportDateFilter } from '../report-date-filter/report-date-filter';
import type { ExpenseCategory } from '../../expenses/expenses.models';
import type { ExpenseReport as ExpenseReportModel, PaymentMethod } from '../accounts-reports.models';
import { UiCompanyViewToggle } from '../../../../shared/ui/company-view-toggle/company-view-toggle';

const COLS: DataTableColumn[] = [
  { label: 'Date', align: 'left' },
  { label: 'Expense', align: 'left' },
  { label: 'Category', align: 'left' },
  { label: 'Method', align: 'left' },
  { label: 'Account', align: 'left' },
  { label: 'Amount', align: 'right' },
  { label: 'Status', align: 'left' },
];

const PAGE_SIZE = 20;
const ALL = 'All';
const money = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-expense-report',
  standalone: true,
  imports: [UiCompanyViewToggle, UiDataTable, ReportDateFilter],
  templateUrl: './expense-report.html',
  styleUrl: '../accounts-report.scss',
})
export class ExpenseReport implements OnInit {
  private readonly service = inject(AccountsReportsService);
  private readonly expensesService = inject(ExpensesService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly search = signal('');
  readonly categoryId = signal('');
  readonly method = signal<'All' | PaymentMethod>(ALL);
  readonly includeCancelled = signal(false);
  readonly dateFrom = signal('');
  readonly dateTo = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly report = signal<ExpenseReportModel | null>(null);
  readonly categories = signal<ExpenseCategory[]>([]);

  readonly total = computed(() => this.report()?.total ?? 0);
  readonly hasMore = computed(() => this.page() * PAGE_SIZE < this.total());
  readonly symbol = computed(() => this.report()?.baseCurrency.symbol ?? '₹');
  readonly fmt = (n: number) => this.symbol() + money(n);

  readonly filters = computed<DataTableFilter[]>(() => [
    { value: this.method(), options: [ALL, 'Cash', 'Upi', 'Card', 'Credit', 'BankTransfer'], onChange: (v) => this.onMethod(v as 'All' | PaymentMethod) },
  ]);

  readonly rows = computed<DataTableRow[]>(() => {
    const base = this.report()?.baseCurrency.code;
    return (this.report()?.items ?? []).map((e) => {
      const cancelled = e.status === 'Cancelled';
      return {
        cells: [
          { kind: 'text', text: e.expenseDate, color: 'var(--color-text-muted)' },
          { kind: 'link', text: e.expenseNo, onClick: () => this.router.navigate(['/accounts/expenses', e.id]) },
          { kind: 'text', text: e.category, weight: '600' },
          { kind: 'text', text: e.paymentMethod, color: 'var(--color-text-muted)' },
          { kind: 'text', text: e.accountName ?? 'Not tracked', color: 'var(--color-text-muted)' },
          {
            kind: 'text',
            text: money(e.baseAmount),
            sub: e.currencyCode !== base ? `${e.currencyCode} ${money(e.amount)} @ ${e.exchangeRateToBase}` : undefined,
            align: 'right',
            weight: '650',
            color: cancelled ? 'var(--color-text-muted)' : 'var(--color-text)',
          },
          { kind: 'badge', text: e.status, tone: cancelled ? 'mute' : 'good' },
        ],
      };
    });
  });

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

  onMethod(value: 'All' | PaymentMethod): void {
    this.method.set(value);
    this.resetAndLoad();
  }

  onIncludeCancelled(checked: boolean): void {
    this.includeCancelled.set(checked);
    this.resetAndLoad();
  }

  onDates(from: string | null, to: string | null): void {
    if (from !== null) this.dateFrom.set(from);
    if (to !== null) this.dateTo.set(to);
    this.resetAndLoad();
  }

  onPageChange(page: number): void {
    this.page.set(page);
    this.load();
  }

  onCompanyViewChange(): void {
    this.page.set(1);
    this.load();
  }

  retry(): void {
    this.load();
  }

  print(): void {
    window.print();
  }

  private resetAndLoad(): void {
    this.page.set(1);
    this.load();
  }

  private load(): void {
    this.state.set('loading');
    this.service
      .expenses({
        search: this.search(),
        categoryId: this.categoryId(),
        paymentMethod: this.method() === ALL ? undefined : (this.method() as PaymentMethod),
        includeCancelled: this.includeCancelled(),
        dateFrom: this.dateFrom(),
        dateTo: this.dateTo(),
        page: this.page(),
        pageSize: PAGE_SIZE,
      })
      .subscribe({
        next: (r) => {
          this.report.set(r);
          this.state.set('ready');
        },
        error: () => this.state.set('error'),
      });
  }
}
