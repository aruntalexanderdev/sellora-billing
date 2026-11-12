import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { LocationSelect } from '../../../../shared/locations/location-select';
import { formatBreakdown, formatQuantity } from '../../../../shared/units/unit-conversion';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { AuthService } from '../../../../core/auth/auth.service';
import { StockService } from '../stock.service';
import type { StockOverviewItem } from '../stock.models';
import { CategoryFilter } from '../../../../shared/product-categories/category-filter';
import type { CategorySelection } from '../../../../shared/product-categories/product-categories.models';
import { UiCompanyViewToggle } from '../../../../shared/ui/company-view-toggle/company-view-toggle';

const COLS: DataTableColumn[] = [
  { label: 'Item', align: 'left' },
  { label: 'Unit', align: 'left' },
  { label: 'Location', align: 'left' },
  { label: 'Available qty', align: 'right' },
  { label: 'Avg. cost', align: 'right' },
  { label: 'Stock value', align: 'right' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

const INR = (n: number) => '₹' + Math.round(n).toLocaleString('en-IN');
const PAGE_SIZE = 20;

/** Per-location data exists only when the server sends `locations[]`; one row shows its code, several show a count. */
export function locationText(p: StockOverviewItem): string {
  const l = p.locations ?? [];
  if (l.length === 0) return '—';
  const one = l.length === 1 ? l[0] : null;
  if (one) return one.type === 'Transit' ? `${one.code} (in transit)` : one.code;
  return `${l.length} locations`;
}

@Component({
  selector: 'app-stock-overview',
  standalone: true,
  imports: [UiCompanyViewToggle, UiDataTable, LocationSelect, CategoryFilter],
  templateUrl: './stock-overview.html',
  styleUrl: '../../../administration/users/user-list/user-list.scss',
})
export class StockOverview implements OnInit {
  private readonly stockService = inject(StockService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly cols = COLS;
  readonly search = signal('');
  readonly page = signal(1);
  /** Location filter (null = all visible locations). */
  readonly locationId = signal<string | null>(null);
  readonly categoryId = signal<string | null>(null);
  readonly subcategoryId = signal<string | null>(null);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<StockOverviewItem[]>([]);
  readonly total = signal(0);

  readonly canAdjust = computed(() => this.auth.hasPermission('Inventory.Adjust'));
  readonly canPrice = computed(() => this.auth.hasPermission('Inventory.Price'));

  readonly hasMore = computed(() => this.page() * PAGE_SIZE < this.total());

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((p) => ({
      cells: [
        { kind: 'link', text: p.name, onClick: () => this.router.navigate(['/inventory/stock', p.productId]) },
        { kind: 'text', text: p.baseUnitLabel, color: 'var(--color-text-muted)' },
        { kind: 'text', text: locationText(p), color: 'var(--color-text-muted)' },
        { kind: 'text', text: formatQuantity(p.quantityOnHand, p.baseDecimalPlaces), sub: formatBreakdown(p.quantityOnHand, p.breakdownUnits, p.baseDecimalPlaces, p.baseUnitLabel) ?? undefined, align: 'right', weight: '600' },
        { kind: 'text', text: INR(p.averageCost), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(p.stockValue), align: 'right', weight: '650' },
        {
          kind: 'badge',
          text: p.status === 'OutOfStock' ? 'Out of stock' : p.status,
          tone: p.status === 'Normal' ? 'good' : p.status === 'Low' ? 'warn' : 'bad',
        },
        {
          kind: 'actions',
          actions: [
            ...(this.canAdjust()
              ? [{ label: 'Adjust', onClick: () => this.router.navigate(['/inventory/stock-adjustments/new'], { queryParams: { productId: p.productId } }) }]
              : []),
            ...(this.canPrice()
              ? [{ label: 'Price', onClick: () => this.router.navigate(['/inventory/pricing'], { queryParams: { search: p.sku } }) }]
              : []),
          ],
        },
      ],
    })),
  );

  ngOnInit(): void {
    const wanted = this.route.snapshot.queryParamMap.get('stockLocationId');
    if (wanted) this.locationId.set(wanted);
    this.load();
  }

  onSearch(value: string): void {
    this.search.set(value);
    this.page.set(1);
    this.load();
  }

  onLocationChange(id: string | null): void {
    if (id === this.locationId()) return;
    this.locationId.set(id);
    this.page.set(1);
    this.router.navigate([], { relativeTo: this.route, queryParams: { stockLocationId: id }, queryParamsHandling: 'merge', replaceUrl: true });
    this.load();
  }

  onCategoryChange(value: CategorySelection): void {
    this.categoryId.set(value.categoryId);
    this.subcategoryId.set(value.subcategoryId);
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
    this.stockService.overview({ search: this.search(), page: this.page(), pageSize: PAGE_SIZE, stockLocationId: this.locationId(), categoryId: this.categoryId(), subcategoryId: this.subcategoryId() }).subscribe({
      next: (res) => {
        this.items.set(res.items);
        this.total.set(res.total);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }
}
