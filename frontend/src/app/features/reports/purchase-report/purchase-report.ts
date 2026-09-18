import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { UiDataTable } from '../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../shared/ui/data-table/data-table.types';
import { ReportsService } from '../reports.service';
import type { PurchaseReportRow } from '../reports.models';

const COLS: DataTableColumn[] = [
  { label: 'Purchase No', align: 'left' },
  { label: 'Date', align: 'left' },
  { label: 'Supplier', align: 'left' },
  { label: 'Subtotal', align: 'right' },
  { label: 'Tax', align: 'right' },
  { label: 'Other', align: 'right' },
  { label: 'Total', align: 'right' },
  { label: 'Paid', align: 'right' },
  { label: 'Due', align: 'right' },
];

const INR = (n: number) => '₹' + Math.round(n).toLocaleString('en-IN');

@Component({
  selector: 'app-purchase-report',
  standalone: true,
  imports: [UiDataTable],
  templateUrl: './purchase-report.html',
  styleUrl: '../report-list.scss',
})
export class PurchaseReport implements OnInit {
  private readonly reportsService = inject(ReportsService);

  readonly cols = COLS;
  readonly search = signal('');
  readonly dateFrom = signal('');
  readonly dateTo = signal('');
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<PurchaseReportRow[]>([]);
  readonly total = signal(0);

  readonly hasDateFilter = computed(() => !!this.dateFrom() || !!this.dateTo());

  readonly totals = computed(() => {
    const rows = this.items();
    return {
      subtotal: rows.reduce((s, r) => s + r.subtotal, 0),
      tax: rows.reduce((s, r) => s + r.taxTotal, 0),
      grandTotal: rows.reduce((s, r) => s + r.grandTotal, 0),
      due: rows.reduce((s, r) => s + r.dueAmount, 0),
    };
  });

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((p) => ({
      cells: [
        { kind: 'text', text: p.purchaseNo, weight: '600', color: p.status === 'Cancelled' ? 'var(--color-text-fainter)' : 'var(--color-text)' },
        { kind: 'text', text: p.purchaseDate, color: 'var(--color-text-muted)' },
        { kind: 'text', text: p.supplierName },
        { kind: 'text', text: INR(p.subtotal), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(p.taxTotal), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(p.otherChargesTotal), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(p.grandTotal), align: 'right', weight: '650' },
        { kind: 'text', text: INR(p.paidAmount), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(p.dueAmount), align: 'right', color: p.dueAmount > 0 ? 'var(--color-danger)' : 'var(--color-text-muted)' },
      ],
    })),
  );

  ngOnInit(): void {
    this.load();
  }

  onSearch(value: string): void {
    this.search.set(value);
    this.load();
  }

  onDateFromChange(value: string): void {
    this.dateFrom.set(value);
    this.load();
  }

  onDateToChange(value: string): void {
    this.dateTo.set(value);
    this.load();
  }

  clearDates(): void {
    this.dateFrom.set('');
    this.dateTo.set('');
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
    this.reportsService
      .purchases({ search: this.search(), dateFrom: this.dateFrom() || undefined, dateTo: this.dateTo() || undefined, pageSize: 100 })
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
