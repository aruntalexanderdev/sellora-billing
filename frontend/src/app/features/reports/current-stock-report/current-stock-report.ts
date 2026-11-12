import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { UiDataTable } from '../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableFilter, DataTableRow, DataTableState } from '../../../shared/ui/data-table/data-table.types';
import { problemMessage, toProblem } from '../../../core/http/api-errors';
import { LoadError } from '../../../shared/scope/load-error';
import { ScopeFilters } from '../../../shared/scope/scope-filters';
import { formatBreakdown, formatQuantity } from '../../../shared/units/unit-conversion';
import { AuthService } from '../../../core/auth/auth.service';
import { ReportsService } from '../reports.service';
import type { CurrentStockReportRow, StockStatus } from '../reports.models';
import { UiCompanyViewToggle } from '../../../shared/ui/company-view-toggle/company-view-toggle';

const BASE_COLS: DataTableColumn[] = [
  { label: 'Item', align: 'left' },
  { label: 'Unit', align: 'left' },
  { label: 'Current qty', align: 'right' },
  { label: 'Avg. cost', align: 'right' },
  { label: 'Stock value', align: 'right' },
  { label: 'Selling value', align: 'right' },
  { label: 'Status', align: 'left' },
];

const INR = (n: number) => '₹' + Math.round(n).toLocaleString('en-IN');
const ALL = 'All';
const PAGE_SIZE = 20;

@Component({
  selector: 'app-current-stock-report',
  standalone: true,
  imports: [UiCompanyViewToggle, UiDataTable, ScopeFilters, LoadError],
  templateUrl: './current-stock-report.html',
  styleUrl: '../report-list.scss',
})
export class CurrentStockReport implements OnInit {
  private readonly reportsService = inject(ReportsService);

  private readonly auth = inject(AuthService);

  readonly cols = BASE_COLS;
  readonly showLocation = computed(() => this.auth.hasPermission('StockLocations.View'));
  readonly stockLocationId = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);
  readonly search = signal('');
  readonly category = signal(ALL);
  readonly status = signal<'All' | StockStatus>(ALL);
  readonly categories = signal<string[]>([]);
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<CurrentStockReportRow[]>([]);
  readonly total = signal(0);

  readonly hasMore = computed(() => this.page() * PAGE_SIZE < this.total());

  readonly filters = computed<DataTableFilter[]>(() => [
    { value: this.category(), options: [ALL, ...this.categories()], onChange: (v) => this.onCategoryChange(v) },
    { value: this.status(), options: [ALL, 'Normal', 'Low', 'OutOfStock'], onChange: (v) => this.onStatusChange(v as 'All' | StockStatus) },
  ]);

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((r) => ({
      cells: [
        { kind: 'text', text: r.name, sub: r.sku, weight: '600' },
        { kind: 'text', text: r.baseUnitLabel, color: 'var(--color-text-muted)' },
        { kind: 'text', text: formatQuantity(r.quantityOnHand, r.baseDecimalPlaces), sub: formatBreakdown(r.quantityOnHand, r.breakdownUnits, r.baseDecimalPlaces, r.baseUnitLabel) ?? undefined, align: 'right', weight: '600' },
        { kind: 'text', text: INR(r.averageCost), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(r.stockValue), align: 'right', weight: '650' },
        { kind: 'text', text: r.sellingValue === null ? '—' : INR(r.sellingValue), align: 'right', color: 'var(--color-text-muted)' },
        {
          kind: 'badge',
          text: r.status === 'OutOfStock' ? 'Out of stock' : r.status,
          tone: r.status === 'Normal' ? 'good' : r.status === 'Low' ? 'warn' : 'bad',
        },
      ],
    })),
  );

  ngOnInit(): void {
    this.reportsService.currentStockCategories().subscribe({ next: (res) => this.categories.set(res) });
    this.load();
  }

  onSearch(value: string): void {
    this.search.set(value);
    this.page.set(1);
    this.load();
  }

  onCategoryChange(value: string): void {
    this.category.set(value);
    this.page.set(1);
    this.load();
  }

  onStatusChange(value: 'All' | StockStatus): void {
    this.status.set(value);
    this.page.set(1);
    this.load();
  }

  onLocationChange(value: string | null): void {
    this.stockLocationId.set(value);
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

  private load(): void {
    this.state.set('loading');
    this.errorMessage.set(null);
    const category = this.category();
    const status = this.status();
    this.reportsService
      .currentStock({
        search: this.search(),
        category: category === ALL ? undefined : category,
        status: status === ALL ? undefined : status,
        stockLocationId: this.stockLocationId(),
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
