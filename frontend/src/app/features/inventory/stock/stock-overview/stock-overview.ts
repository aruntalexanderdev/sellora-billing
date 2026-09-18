import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { AuthService } from '../../../../core/auth/auth.service';
import { StockService } from '../stock.service';
import type { StockOverviewItem } from '../stock.models';

const COLS: DataTableColumn[] = [
  { label: 'Item', align: 'left' },
  { label: 'Unit', align: 'left' },
  { label: 'Available qty', align: 'right' },
  { label: 'Avg. cost', align: 'right' },
  { label: 'Stock value', align: 'right' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

const INR = (n: number) => '₹' + Math.round(n).toLocaleString('en-IN');

@Component({
  selector: 'app-stock-overview',
  standalone: true,
  imports: [UiDataTable],
  templateUrl: './stock-overview.html',
  styleUrl: '../../../administration/users/user-list/user-list.scss',
})
export class StockOverview implements OnInit {
  private readonly stockService = inject(StockService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly search = signal('');
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<StockOverviewItem[]>([]);
  readonly total = signal(0);

  readonly canAdjust = computed(() => this.auth.hasPermission('Inventory.Adjust'));

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((p) => ({
      cells: [
        { kind: 'link', text: p.name, onClick: () => this.router.navigate(['/inventory/stock', p.productId]) },
        { kind: 'text', text: p.baseUnitLabel, color: 'var(--color-text-muted)' },
        { kind: 'text', text: p.quantityOnHand.toFixed(2), align: 'right', weight: '600' },
        { kind: 'text', text: INR(p.averageCost), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(p.stockValue), align: 'right', weight: '650' },
        {
          kind: 'badge',
          text: p.status === 'OutOfStock' ? 'Out of stock' : p.status,
          tone: p.status === 'Normal' ? 'good' : p.status === 'Low' ? 'warn' : 'bad',
        },
        {
          kind: 'actions',
          actions: this.canAdjust()
            ? [{ label: 'Adjust', onClick: () => this.router.navigate(['/inventory/stock-adjustments/new'], { queryParams: { productId: p.productId } }) }]
            : [],
        },
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

  retry(): void {
    this.load();
  }

  private load(): void {
    this.state.set('loading');
    this.stockService.overview({ search: this.search() }).subscribe({
      next: (res) => {
        this.items.set(res.items);
        this.total.set(res.total);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }
}
