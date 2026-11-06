import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../../shared/ui/button/button';
import type { DataTableColumn, DataTableFilter, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { AuthService } from '../../../../core/auth/auth.service';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { ProductsService } from '../../products/products.service';
import { StockAdjustmentsService } from '../stock-adjustments.service';
import type { AdjustmentDirection, AdjustmentType, StockAdjustmentListItem } from '../stock-adjustments.models';

const COLS: DataTableColumn[] = [
  { label: 'No', align: 'left' },
  { label: 'Date', align: 'left' },
  { label: 'Product', align: 'left' },
  { label: 'Type', align: 'left' },
  { label: '±', align: 'center' },
  { label: 'Entered', align: 'right' },
  { label: 'Base', align: 'right' },
  { label: 'Cost / entered unit', align: 'right' },
  { label: 'Before → After', align: 'right' },
  { label: 'User', align: 'left' },
];

const ALL_TYPES = 'All types';
const ALL_DIRECTIONS = 'All directions';
const TYPES: AdjustmentType[] = ['Damage', 'Loss', 'Correction', 'Expiry', 'OpeningStock'];
const DIRECTIONS: AdjustmentDirection[] = ['Increase', 'Decrease'];
export const STOCK_ADJUSTMENT_PAGE_SIZE = 20;

const money = (n: number) => '₹' + n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-stock-adjustment-list',
  standalone: true,
  imports: [UiDataTable, UiButton],
  templateUrl: './stock-adjustment-list.html',
  styleUrl: './stock-adjustment-list.scss',
})
export class StockAdjustmentList implements OnInit {
  private readonly adjustmentsService = inject(StockAdjustmentsService);
  private readonly productsService = inject(ProductsService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly cols = COLS;
  readonly search = signal('');
  readonly productId = signal('');
  readonly type = signal<AdjustmentType | typeof ALL_TYPES>(ALL_TYPES);
  readonly direction = signal<AdjustmentDirection | typeof ALL_DIRECTIONS>(ALL_DIRECTIONS);
  readonly dateFrom = signal('');
  readonly dateTo = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<StockAdjustmentListItem[]>([]);
  readonly total = signal(0);
  readonly products = signal<{ id: string; name: string; sku: string }[]>([]);

  readonly canAdjust = computed(() => this.auth.hasPermission('Inventory.Adjust'));
  readonly hasMore = computed(() => this.page() * STOCK_ADJUSTMENT_PAGE_SIZE < this.total());
  readonly hasExtraFilter = computed(() => !!this.productId() || !!this.dateFrom() || !!this.dateTo());
  /** True when the product filter (e.g. from a deep link) is not in the loaded picker list. */
  readonly productOutsideList = computed(() => !!this.productId() && !this.products().some((p) => p.id === this.productId()));

  readonly filters = computed<DataTableFilter[]>(() => [
    { value: this.type(), options: [ALL_TYPES, ...TYPES], onChange: (v) => this.onTypeChange(v as AdjustmentType | typeof ALL_TYPES) },
    {
      value: this.direction(),
      options: [ALL_DIRECTIONS, ...DIRECTIONS],
      onChange: (v) => this.onDirectionChange(v as AdjustmentDirection | typeof ALL_DIRECTIONS),
    },
  ]);

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((a) => {
      const increase = a.direction === 'Increase';
      const base = (n: number) => formatQuantity(n, a.baseDecimalPlaces);
      return {
        cells: [
          { kind: 'link', text: a.adjustmentNo, onClick: () => this.openDetail(a.id) },
          { kind: 'text', text: formatDateTime(a.createdAt), color: 'var(--color-text-muted)' },
          { kind: 'text', text: a.productName, sub: a.sku, weight: '600' },
          { kind: 'badge', text: a.type, tone: a.type === 'OpeningStock' ? 'good' : a.type === 'Correction' ? 'info' : 'warn' },
          {
            kind: 'text',
            text: increase ? '+' : '−',
            align: 'center',
            weight: '700',
            color: increase ? 'var(--tone-good-fg)' : 'var(--color-danger)',
          },
          { kind: 'text', text: `${formatQuantity(a.enteredQuantity, a.enteredDecimalPlaces)} ${a.unitCode}`, align: 'right', weight: '650' },
          { kind: 'text', text: `${base(a.quantityBaseUnits)} ${a.baseUnitCode}`, align: 'right', color: 'var(--color-text-muted)' },
          {
            kind: 'text',
            text: a.enteredUnitCost === null ? '—' : `${money(a.enteredUnitCost)} / ${a.unitCode}`,
            align: 'right',
            color: 'var(--color-text-muted)',
          },
          { kind: 'text', text: `${base(a.beforeQty)} → ${base(a.afterQty)} ${a.baseUnitCode}`, align: 'right' },
          { kind: 'text', text: a.userName, color: 'var(--color-text-muted)' },
        ],
      } satisfies DataTableRow;
    }),
  );

  ngOnInit(): void {
    const productId = this.route.snapshot.queryParamMap.get('productId');
    if (productId) this.productId.set(productId);
    this.productsService.list().subscribe({
      next: (res) => this.products.set(res.items.map((p) => ({ id: p.id, name: p.name, sku: p.sku }))),
    });
    this.load();
  }

  onSearch(value: string): void {
    this.search.set(value);
    this.reload();
  }

  onProductChange(value: string): void {
    this.productId.set(value);
    this.reload();
  }

  onTypeChange(value: AdjustmentType | typeof ALL_TYPES): void {
    this.type.set(value);
    this.reload();
  }

  onDirectionChange(value: AdjustmentDirection | typeof ALL_DIRECTIONS): void {
    this.direction.set(value);
    this.reload();
  }

  onDateFromChange(value: string): void {
    this.dateFrom.set(value);
    this.reload();
  }

  onDateToChange(value: string): void {
    this.dateTo.set(value);
    this.reload();
  }

  clearFilters(): void {
    this.productId.set('');
    this.dateFrom.set('');
    this.dateTo.set('');
    this.reload();
  }

  onPageChange(page: number): void {
    this.page.set(page);
    this.load();
  }

  retry(): void {
    this.load();
  }

  goToCreate(): void {
    const productId = this.productId();
    this.router.navigate(['/inventory/stock-adjustments/new'], productId ? { queryParams: { productId } } : {});
  }

  openDetail(id: string): void {
    this.router.navigate(['/inventory/stock-adjustments', id]);
  }

  private reload(): void {
    this.page.set(1);
    this.load();
  }

  private load(): void {
    this.state.set('loading');
    const type = this.type();
    const direction = this.direction();
    this.adjustmentsService
      .list({
        search: this.search() || undefined,
        productId: this.productId() || undefined,
        type: type === ALL_TYPES ? undefined : type,
        direction: direction === ALL_DIRECTIONS ? undefined : direction,
        dateFrom: this.dateFrom() || undefined,
        dateTo: this.dateTo() || undefined,
        page: this.page(),
        pageSize: STOCK_ADJUSTMENT_PAGE_SIZE,
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
