import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableFilter, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { AccountsReportsService } from '../accounts-reports.service';
import { ReportDateFilter } from '../report-date-filter/report-date-filter';
import type { PaymentEntryKind, PaymentMethod, PaymentReport as PaymentReportModel, PaymentReportRow } from '../accounts-reports.models';

const PAGE_SIZE = 20;
const ALL = 'All';
const money = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** One page serves both payment reports: the route says whether it is the purchase (paid out) or sales (received) side. */
@Component({
  selector: 'app-payment-report',
  standalone: true,
  imports: [UiDataTable, ReportDateFilter],
  templateUrl: './payment-report.html',
  styleUrl: '../accounts-report.scss',
})
export class PaymentReport implements OnInit {
  private readonly service = inject(AccountsReportsService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly side: 'purchase' | 'sales' = this.route.snapshot.data['side'];
  readonly purchase = this.side === 'purchase';

  readonly search = signal('');
  readonly method = signal<'All' | PaymentMethod>(ALL);
  readonly kind = signal<'All' | PaymentEntryKind>(ALL);
  readonly dateFrom = signal('');
  readonly dateTo = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly report = signal<PaymentReportModel | null>(null);

  readonly cols = computed<DataTableColumn[]>(() => [
    { label: 'Date', align: 'left' },
    { label: 'Entry', align: 'left' },
    { label: this.purchase ? 'Supplier' : 'Customer', align: 'left' },
    { label: this.purchase ? 'Purchase' : 'Sale', align: 'left' },
    { label: 'Method', align: 'left' },
    { label: 'Account', align: 'left' },
    { label: 'Amount', align: 'right' },
    { label: this.purchase ? 'Purchase status' : 'Sale status', align: 'left' },
  ]);

  readonly total = computed(() => this.report()?.total ?? 0);
  readonly hasMore = computed(() => this.page() * PAGE_SIZE < this.total());
  readonly symbol = computed(() => this.report()?.baseCurrency.symbol ?? '₹');
  readonly fmt = (n: number) => this.symbol() + money(n);

  readonly filters = computed<DataTableFilter[]>(() => [
    { value: this.method(), options: [ALL, 'Cash', 'Upi', 'Card', 'Credit', 'BankTransfer'], onChange: (v) => this.set(this.method, v as 'All' | PaymentMethod) },
    { value: this.kind(), options: [ALL, 'Payment', 'Refund', 'Reversal'], onChange: (v) => this.set(this.kind, v as 'All' | PaymentEntryKind) },
  ]);

  readonly rows = computed<DataTableRow[]>(() => {
    const base = this.report()?.baseCurrency.code;
    return (this.report()?.items ?? []).map((r: PaymentReportRow) => {
      // Money paid in is a plus, anything paid back or undone a minus — so the column adds up to the net.
      const sign = r.kind === 'Payment' ? '' : '−';
      return {
        cells: [
          { kind: 'text', text: r.paymentDate, color: 'var(--color-text-muted)' },
          { kind: 'badge', text: r.kind, tone: r.kind === 'Payment' ? 'good' : 'warn' },
          { kind: 'text', text: r.party, weight: '600' },
          { kind: 'link', text: r.documentNo, onClick: () => this.router.navigate([this.purchase ? '/purchases' : '/sales', r.documentId]) },
          { kind: 'text', text: r.paymentMethod, color: 'var(--color-text-muted)' },
          { kind: 'text', text: r.accountName ?? 'Not tracked', color: 'var(--color-text-muted)' },
          {
            kind: 'text',
            text: `${sign}${money(r.baseAmount)}`,
            // A foreign-currency payment shows what actually changed hands under its base-currency value.
            sub: r.currencyCode !== base ? `${r.currencyCode} ${money(r.amount)} @ ${r.exchangeRateToBase}` : undefined,
            align: 'right',
            weight: '650',
          },
          { kind: 'badge', text: r.documentPaymentStatus, tone: r.documentPaymentStatus === 'Paid' ? 'good' : r.documentPaymentStatus === 'PartiallyPaid' ? 'warn' : 'bad' },
        ],
      };
    });
  });

  ngOnInit(): void {
    this.load();
  }

  private set<T>(target: { set: (v: T) => void }, value: T): void {
    target.set(value);
    this.page.set(1);
    this.load();
  }

  onSearch(value: string): void {
    this.set(this.search, value);
  }

  onDates(from: string | null, to: string | null): void {
    if (from !== null) this.dateFrom.set(from);
    if (to !== null) this.dateTo.set(to);
    this.page.set(1);
    this.load();
  }

  onPageChange(page: number): void {
    this.page.set(page);
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
    this.service
      .payments(this.side, {
        search: this.search(),
        paymentMethod: this.method() === ALL ? undefined : (this.method() as PaymentMethod),
        kind: this.kind() === ALL ? undefined : (this.kind() as PaymentEntryKind),
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
