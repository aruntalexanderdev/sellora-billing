import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { UiDataTable } from '../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableFilter, DataTableRow, DataTableState } from '../../../shared/ui/data-table/data-table.types';
import { ReportsService } from '../reports.service';
import type { StockMovementReportRow, StockMovementType } from '../reports.models';

const COLS: DataTableColumn[] = [
  { label: 'Date', align: 'left' },
  { label: 'Item', align: 'left' },
  { label: 'Type', align: 'left' },
  { label: 'Quantity', align: 'right' },
  { label: 'Reference', align: 'left' },
  { label: 'User', align: 'left' },
];

const ALL = 'All';
const PAGE_SIZE = 20;

@Component({
  selector: 'app-stock-movement-report',
  standalone: true,
  imports: [UiDataTable],
  templateUrl: './stock-movement-report.html',
  styleUrl: '../report-list.scss',
})
export class StockMovementReport implements OnInit {
  private readonly reportsService = inject(ReportsService);

  readonly cols = COLS;
  readonly search = signal('');
  readonly type = signal<'All' | StockMovementType>(ALL);
  readonly dateFrom = signal('');
  readonly dateTo = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<StockMovementReportRow[]>([]);
  readonly total = signal(0);

  readonly hasDateFilter = computed(() => !!this.dateFrom() || !!this.dateTo());
  readonly hasMore = computed(() => this.page() * PAGE_SIZE < this.total());

  readonly filters = computed<DataTableFilter[]>(() => [
    { value: this.type(), options: [ALL, 'Purchase', 'Sale', 'Adjustment', 'SaleReturn', 'PurchaseReturn', 'SaleCancellation'], onChange: (v) => this.onTypeChange(v as 'All' | StockMovementType) },
  ]);

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((m) => ({
      cells: [
        { kind: 'text', text: formatDateTime(m.occurredAt), color: 'var(--color-text-muted)' },
        { kind: 'text', text: m.productName, sub: m.sku, weight: '600' },
        {
          kind: 'badge',
          text: m.type,
          tone: m.type === 'Purchase' || m.type === 'SaleReturn' || m.type === 'SaleCancellation' ? 'good' : m.type === 'Sale' || m.type === 'PurchaseReturn' ? 'info' : 'warn',
        },
        {
          kind: 'text',
          text: (m.quantityBaseUnits > 0 ? '+' : '') + m.quantityBaseUnits.toFixed(2) + ' ' + m.baseUnitLabel,
          align: 'right',
          weight: '650',
          color: m.quantityBaseUnits >= 0 ? 'var(--tone-good-fg)' : 'var(--color-danger)',
        },
        { kind: 'text', text: m.referenceNo, color: 'var(--color-text-muted)' },
        { kind: 'text', text: m.userName, color: 'var(--color-text-muted)' },
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

  onTypeChange(value: 'All' | StockMovementType): void {
    this.type.set(value);
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

  onPageChange(page: number): void {
    this.page.set(page);
    this.load();
  }

  retry(): void {
    this.load();
  }

  private load(): void {
    this.state.set('loading');
    const type = this.type();
    this.reportsService
      .stockMovement({
        search: this.search(),
        type: type === ALL ? undefined : type,
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
        error: () => this.state.set('error'),
      });
  }
}

function formatDateTime(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' }) + ' ' + d.toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' });
}
