import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { AccountsReportsService } from '../accounts-reports.service';
import { ReportDateFilter } from '../report-date-filter/report-date-filter';
import type { OutstandingReport as OutstandingReportModel } from '../accounts-reports.models';

const PAGE_SIZE = 20;
const money = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** One page serves both outstanding reports: what we owe suppliers, and what customers owe us. */
@Component({
  selector: 'app-outstanding-report',
  standalone: true,
  imports: [UiDataTable, ReportDateFilter],
  templateUrl: './outstanding-report.html',
  styleUrl: '../accounts-report.scss',
})
export class OutstandingReport implements OnInit {
  private readonly service = inject(AccountsReportsService);
  private readonly route = inject(ActivatedRoute);

  readonly side: 'supplier' | 'customer' = this.route.snapshot.data['side'];
  readonly supplier = this.side === 'supplier';

  readonly search = signal('');
  readonly onlyOutstanding = signal(false);
  readonly dateFrom = signal('');
  readonly dateTo = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly report = signal<OutstandingReportModel | null>(null);

  readonly cols = computed<DataTableColumn[]>(() => [
    { label: this.supplier ? 'Supplier' : 'Customer', align: 'left' },
    { label: this.supplier ? 'Purchases' : 'Sales', align: 'right' },
    { label: 'Total', align: 'right' },
    { label: 'Returned', align: 'right' },
    { label: 'Net', align: 'right' },
    { label: this.supplier ? 'Paid' : 'Received', align: 'right' },
    { label: 'Outstanding', align: 'right' },
    { label: 'Credit held', align: 'right' },
    { label: 'Unpaid / partial', align: 'left' },
  ]);

  readonly total = computed(() => this.report()?.total ?? 0);
  readonly hasMore = computed(() => this.page() * PAGE_SIZE < this.total());
  readonly symbol = computed(() => this.report()?.baseCurrency.symbol ?? '₹');
  readonly fmt = (n: number) => this.symbol() + money(n);

  readonly rows = computed<DataTableRow[]>(() =>
    (this.report()?.items ?? []).map((r) => ({
      cells: [
        { kind: 'text', text: r.partyName, weight: '600' },
        { kind: 'text', text: String(r.documentCount), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: money(r.total), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: money(r.returned), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: money(r.net), align: 'right' },
        { kind: 'text', text: money(r.paid), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: money(r.outstanding), align: 'right', weight: '650', color: r.outstanding > 0 ? 'var(--color-danger)' : 'var(--color-text-muted)' },
        { kind: 'text', text: money(r.creditBalance), align: 'right', color: r.creditBalance > 0 ? 'var(--color-success, #15803d)' : 'var(--color-text-muted)' },
        { kind: 'text', text: `${r.unpaidCount} unpaid · ${r.partialCount} partial`, color: 'var(--color-text-muted)' },
      ],
    })),
  );

  ngOnInit(): void {
    this.load();
  }

  onSearch(value: string): void {
    this.search.set(value);
    this.resetAndLoad();
  }

  onOnlyOutstanding(checked: boolean): void {
    this.onlyOutstanding.set(checked);
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
      .outstanding(this.side, {
        search: this.search(),
        onlyOutstanding: this.onlyOutstanding(),
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
