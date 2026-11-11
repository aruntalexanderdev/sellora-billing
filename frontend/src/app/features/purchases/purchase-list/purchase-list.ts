import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../shared/ui/button/button';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../shared/ui/data-table/data-table.types';
import { AuthService } from '../../../core/auth/auth.service';
import { problemMessage, toProblem } from '../../../core/http/api-errors';
import { LoadError } from '../../../shared/scope/load-error';
import { ScopeFilters } from '../../../shared/scope/scope-filters';
import { injectBranchColumnVisible, scopeCells, scopeColumns } from '../../../shared/scope/list-scope';
import { PurchasesService } from '../purchases.service';
import type { PurchaseListItem } from '../purchases.models';

const HEAD_COLS: DataTableColumn[] = [
  { label: 'Purchase No', align: 'left' },
  { label: 'Supplier', align: 'left' },
  { label: 'Date', align: 'left' },
  { label: 'Total', align: 'right' },
  { label: 'Payment', align: 'left' },
  { label: 'Status', align: 'left' },
];
const TAIL_COLS: DataTableColumn[] = [{ label: '', align: 'right' }];

const INR = (n: number) => '₹' + Math.round(n).toLocaleString('en-IN');

@Component({
  selector: 'app-purchase-list',
  standalone: true,
  imports: [UiDataTable, UiButton, ScopeFilters, LoadError],
  templateUrl: './purchase-list.html',
  styleUrl: '../../../shared/scope/scope-list.scss',
})
export class PurchaseList implements OnInit {
  private readonly purchasesService = inject(PurchasesService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = computed<DataTableColumn[]>(() => [...HEAD_COLS, ...scopeColumns(this.showBranch(), this.showLocation()), ...TAIL_COLS]);
  readonly search = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<PurchaseListItem[]>([]);
  readonly total = signal(0);
  readonly branchId = signal<string | null>(null);
  readonly stockLocationId = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);
  readonly showBranch = injectBranchColumnVisible();
  readonly showLocation = computed(() => this.auth.hasPermission('StockLocations.View'));

  readonly canCreate = computed(() => this.auth.hasPermission('Purchase.Create'));
  readonly canPay = computed(() => this.auth.hasPermission('Purchase.Pay'));
  readonly hasMore = computed(() => this.page() * 20 < this.total());

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((p) => ({
      cells: [
        { kind: 'link', text: p.purchaseNo, onClick: () => this.router.navigate(['/purchases', p.id]) },
        { kind: 'text', text: p.supplierName, weight: '600' },
        { kind: 'text', text: p.purchaseDate, color: 'var(--color-text-muted)' },
        {
          kind: 'text',
          text: INR(p.grandTotal),
          sub: p.originalGrandTotal === p.grandTotal ? undefined : `${p.currencyCode} ${p.originalGrandTotal.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`,
          align: 'right',
          weight: '650',
        },
        { kind: 'badge', text: p.paymentStatus, tone: p.paymentStatus === 'Paid' ? 'good' : p.paymentStatus === 'PartiallyPaid' ? 'warn' : 'bad' },
        { kind: 'badge', text: p.status, tone: p.status === 'Completed' ? 'good' : 'mute' },
        ...scopeCells(p, this.showBranch(), this.showLocation()),
        {
          kind: 'actions',
          // Only a live purchase that still has something outstanding can be paid.
          actions:
            this.canPay() && p.status === 'Completed' && p.paymentStatus !== 'Paid'
              ? [{ label: 'Pay', onClick: () => this.router.navigate(['/purchases', p.id], { queryParams: { pay: 1 } }) }]
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
    this.page.set(1);
    this.load();
  }

  onPageChange(page: number): void {
    this.page.set(page);
    this.load();
  }

  onBranchChange(value: string | null): void {
    this.branchId.set(value);
    this.page.set(1);
    this.load();
  }

  onLocationChange(value: string | null): void {
    this.stockLocationId.set(value);
    this.page.set(1);
    this.load();
  }

  retry(): void {
    this.load();
  }

  goToCreate(): void {
    this.router.navigate(['/purchases/new']);
  }

  private load(): void {
    this.state.set('loading');
    this.errorMessage.set(null);
    this.purchasesService.list({ search: this.search(), branchId: this.branchId(), stockLocationId: this.stockLocationId(), page: this.page() }).subscribe({
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
