import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableFilter, DataTableRow, DataTableState } from '../../../shared/ui/data-table/data-table.types';
import { AuthService } from '../../../core/auth/auth.service';
import { problemMessage, toProblem } from '../../../core/http/api-errors';
import { LoadError } from '../../../shared/scope/load-error';
import { ScopeFilters } from '../../../shared/scope/scope-filters';
import { placeLabel } from '../../../shared/scope/list-scope';
import { ReportsService } from '../reports.service';
import type { StockMovementReportRow, StockMovementType } from '../reports.models';
import { STOCK_MOVEMENT_TYPES, enteredLabel, movementTone, movementTypeLabel } from '../../../shared/units/stock-movement';
import { formatQuantity } from '../../../shared/units/unit-conversion';
import { UiCompanyViewToggle } from '../../../shared/ui/company-view-toggle/company-view-toggle';

const BASE_COLS: DataTableColumn[] = [
  { label: 'Date', align: 'left' },
  { label: 'Item', align: 'left' },
  { label: 'Type', align: 'left' },
  { label: 'Quantity', align: 'right' },
  { label: 'Reference', align: 'left' },
  { label: 'User', align: 'left' },
];

/** Movements written by a stock transfer link to the transfer (backend `ReferenceType`). */
export const STOCK_TRANSFER_REFERENCE = 'StockTransfer';

const ALL = 'All';
const PAGE_SIZE = 20;

@Component({
  selector: 'app-stock-movement-report',
  standalone: true,
  imports: [UiCompanyViewToggle, UiDataTable, ScopeFilters, LoadError],
  templateUrl: './stock-movement-report.html',
  styleUrl: '../report-list.scss',
})
export class StockMovementReport implements OnInit {
  private readonly reportsService = inject(ReportsService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly showLocation = computed(() => this.auth.hasPermission('StockLocations.View'));
  readonly cols = computed<DataTableColumn[]>(() => {
    if (!this.showLocation()) return BASE_COLS;
    // Location sits after Reference; User stays last.
    return [...BASE_COLS.slice(0, 5), { label: 'Location', align: 'left' }, ...BASE_COLS.slice(5)];
  });
  readonly stockLocationId = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);
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
    { value: this.type(), options: [ALL, ...STOCK_MOVEMENT_TYPES], onChange: (v) => this.onTypeChange(v as 'All' | StockMovementType) },
  ]);

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((m) => ({
      cells: [
        { kind: 'text', text: formatDateTime(m.occurredAt), color: 'var(--color-text-muted)' },
        { kind: 'text', text: m.productName, sub: m.sku, weight: '600' },
        {
          kind: 'badge',
          text: movementTypeLabel(m.type, m.reasonCode),
          tone: movementTone(m.type),
        },
        {
          kind: 'text',
          text: (m.quantityBaseUnits > 0 ? '+' : '') + formatQuantity(m.quantityBaseUnits, m.baseDecimalPlaces) + ' ' + m.baseUnitLabel,
          sub: enteredLabel(m) ?? undefined,
          align: 'right',
          weight: '650',
          color: m.quantityBaseUnits >= 0 ? 'var(--tone-good-fg)' : 'var(--color-danger)',
        },
        m.referenceType === STOCK_TRANSFER_REFERENCE && m.referenceId
          ? { kind: 'link', text: m.referenceNo, onClick: () => this.openTransfer(m.referenceId!) }
          : { kind: 'text', text: m.referenceNo, color: 'var(--color-text-muted)' },
        ...(this.showLocation() ? [{ kind: 'text' as const, text: placeLabel(m.stockLocationCode, m.stockLocationName), color: 'var(--color-text-muted)' }] : []),
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

  onLocationChange(value: string | null): void {
    this.stockLocationId.set(value);
    this.page.set(1);
    this.load();
  }

  openTransfer(id: string): void {
    this.router.navigate(['/inventory/stock-transfers', id]);
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
    const type = this.type();
    this.reportsService
      .stockMovement({
        search: this.search(),
        type: type === ALL ? undefined : type,
        dateFrom: this.dateFrom() || undefined,
        dateTo: this.dateTo() || undefined,
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

function formatDateTime(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' }) + ' ' + d.toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' });
}
