import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { UiDataTable } from '../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableFilter, DataTableRow, DataTableState } from '../../../shared/ui/data-table/data-table.types';
import { problemMessage, toProblem } from '../../../core/http/api-errors';
import { LoadError } from '../../../shared/scope/load-error';
import { ScopeFilters } from '../../../shared/scope/scope-filters';
import { injectBranchColumnVisible, placeLabel } from '../../../shared/scope/list-scope';
import { ReportsService } from '../reports.service';
import type { PaymentMethod, SalesReportRow } from '../reports.models';
import { UiCompanyViewToggle } from '../../../shared/ui/company-view-toggle/company-view-toggle';

const BASE_COLS: DataTableColumn[] = [
  { label: 'Invoice', align: 'left' },
  { label: 'Date', align: 'left' },
  { label: 'Customer', align: 'left' },
  { label: 'Subtotal', align: 'right' },
  { label: 'Tax', align: 'right' },
  { label: 'Discount', align: 'right' },
  { label: 'Total', align: 'right' },
  { label: 'Paid', align: 'right' },
  { label: 'Due', align: 'right' },
];

const INR = (n: number) => '₹' + Math.round(n).toLocaleString('en-IN');
const ALL = 'All';
const PAGE_SIZE = 20;

@Component({
  selector: 'app-sales-report',
  standalone: true,
  imports: [UiCompanyViewToggle, UiDataTable, ScopeFilters, LoadError],
  templateUrl: './sales-report.html',
  styleUrl: '../report-list.scss',
})
export class SalesReport implements OnInit {
  private readonly reportsService = inject(ReportsService);

  readonly showBranch = injectBranchColumnVisible();
  readonly cols = computed<DataTableColumn[]>(() => [...BASE_COLS, ...(this.showBranch() ? [{ label: 'Branch', align: 'left' as const }] : [])]);
  readonly branchId = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);
  readonly search = signal('');
  readonly paymentMethod = signal<'All' | PaymentMethod>(ALL);
  readonly dateFrom = signal('');
  readonly dateTo = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<SalesReportRow[]>([]);
  readonly total = signal(0);

  readonly hasDateFilter = computed(() => !!this.dateFrom() || !!this.dateTo());
  readonly hasMore = computed(() => this.page() * PAGE_SIZE < this.total());
  readonly isPartialTotals = computed(() => this.page() > 1 || this.hasMore());

  readonly filters = computed<DataTableFilter[]>(() => [
    {
      value: this.paymentMethod(),
      options: [ALL, 'Cash', 'Upi', 'Card', 'Credit', 'BankTransfer'],
      onChange: (v) => this.onPaymentMethodChange(v as 'All' | PaymentMethod),
    },
  ]);

  readonly totals = computed(() => {
    const rows = this.items();
    return {
      subtotal: rows.reduce((s, r) => s + r.subtotal, 0),
      tax: rows.reduce((s, r) => s + r.taxTotal, 0),
      discount: rows.reduce((s, r) => s + r.discountTotal, 0),
      grandTotal: rows.reduce((s, r) => s + r.grandTotal, 0),
      due: rows.reduce((s, r) => s + r.dueAmount, 0),
    };
  });

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((s) => ({
      cells: [
        { kind: 'text', text: s.invoiceNo, weight: '600', color: s.status === 'Cancelled' ? 'var(--color-text-fainter)' : 'var(--color-text)' },
        { kind: 'text', text: s.invoiceDate, color: 'var(--color-text-muted)' },
        { kind: 'text', text: s.customerName },
        { kind: 'text', text: INR(s.subtotal), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(s.taxTotal), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(s.discountTotal), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(s.grandTotal), align: 'right', weight: '650' },
        { kind: 'text', text: INR(s.paidAmount), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(s.dueAmount), align: 'right', color: s.dueAmount > 0 ? 'var(--color-danger)' : 'var(--color-text-muted)' },
        ...(this.showBranch() ? [{ kind: 'text' as const, text: placeLabel(s.branchCode, s.branchName), color: 'var(--color-text-muted)' }] : []),
      ],
    })),
  );

  ngOnInit(): void {
    this.load();
  }

  onSearch(value: string): void {
    this.search.set(value);
    this.page.set(1);
    this.load();
  }

  onPaymentMethodChange(value: 'All' | PaymentMethod): void {
    this.paymentMethod.set(value);
    this.page.set(1);
    this.load();
  }

  onDateFromChange(value: string): void {
    this.dateFrom.set(value);
    this.page.set(1);
    this.load();
  }

  onDateToChange(value: string): void {
    this.dateTo.set(value);
    this.page.set(1);
    this.load();
  }

  clearDates(): void {
    this.dateFrom.set('');
    this.dateTo.set('');
    this.page.set(1);
    this.load();
  }

  onBranchChange(value: string | null): void {
    this.branchId.set(value);
    this.page.set(1);
    this.load();
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

  private load(): void {
    this.state.set('loading');
    this.errorMessage.set(null);
    const paymentMethod = this.paymentMethod();
    this.reportsService
      .sales({
        search: this.search(),
        paymentMethod: paymentMethod === ALL ? undefined : paymentMethod,
        branchId: this.branchId(),
        dateFrom: this.dateFrom() || undefined,
        dateTo: this.dateTo() || undefined,
        page: this.page(),
        pageSize: PAGE_SIZE,
      })
      .subscribe({
        next: (res) => {
          this.items.set(res.items);
          this.total.set(res.total);
          this.state.set('ready');
        },
        error: (err) => {
          this.errorMessage.set(problemMessage(toProblem(err)));
          this.state.set('error');
        },
      });
  }
}
