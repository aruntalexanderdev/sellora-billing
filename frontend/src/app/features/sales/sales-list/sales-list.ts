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
import { SalesService } from '../sales.service';
import type { SalesInvoiceListItem } from '../sales.models';

const HEAD_COLS: DataTableColumn[] = [
  { label: 'Invoice', align: 'left' },
  { label: 'Customer', align: 'left' },
  { label: 'Date', align: 'left' },
  { label: 'Total', align: 'right' },
  { label: 'Paid', align: 'right' },
  { label: 'Due', align: 'right' },
  { label: 'Status', align: 'left' },
  { label: 'Payment', align: 'left' },
];
const TAIL_COLS: DataTableColumn[] = [{ label: '', align: 'right' }];

const INR = (n: number) => '₹' + Math.round(n).toLocaleString('en-IN');

@Component({
  selector: 'app-sales-list',
  standalone: true,
  imports: [UiDataTable, UiButton, ScopeFilters, LoadError],
  templateUrl: './sales-list.html',
  styleUrl: '../../../shared/scope/scope-list.scss',
})
export class SalesList implements OnInit {
  private readonly salesService = inject(SalesService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = computed<DataTableColumn[]>(() => [...HEAD_COLS, ...scopeColumns(this.showBranch(), this.showLocation()), ...TAIL_COLS]);
  readonly search = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<SalesInvoiceListItem[]>([]);
  readonly total = signal(0);
  readonly branchId = signal<string | null>(null);
  readonly stockLocationId = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);
  readonly showBranch = injectBranchColumnVisible();
  readonly showLocation = computed(() => this.auth.hasPermission('StockLocations.View'));

  readonly canReceive = computed(() => this.auth.hasPermission('Sales.Receive'));
  readonly canCreate = computed(() => this.auth.hasPermission('Sales.Create'));
  readonly hasMore = computed(() => this.page() * 20 < this.total());

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((s) => ({
      cells: [
        { kind: 'link', text: s.invoiceNo, onClick: () => this.router.navigate(['/sales', s.id]) },
        { kind: 'text', text: s.customerName, weight: '600' },
        { kind: 'text', text: s.invoiceDate, color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(s.grandTotal), align: 'right', weight: '650' },
        { kind: 'text', text: INR(s.paidAmount), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(s.dueAmount), align: 'right', color: s.dueAmount > 0 ? 'var(--color-danger)' : 'var(--color-text-muted)' },
        { kind: 'badge', text: s.status, tone: s.status === 'Completed' ? 'good' : 'mute' },
        { kind: 'badge', text: s.paymentStatus, tone: s.paymentStatus === 'Paid' ? 'good' : s.paymentStatus === 'PartiallyPaid' ? 'warn' : 'bad' },
        ...scopeCells(s, this.showBranch(), this.showLocation()),
        {
          kind: 'actions',
          // Only a live invoice that still has something outstanding can receive a payment.
          actions:
            this.canReceive() && s.status === 'Completed' && s.paymentStatus !== 'Paid'
              ? [{ label: 'Receive payment', onClick: () => this.router.navigate(['/sales', s.id], { queryParams: { pay: 1 } }) }]
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
    this.router.navigate(['/sales/new']);
  }

  private load(): void {
    this.state.set('loading');
    this.errorMessage.set(null);
    this.salesService.list({ search: this.search(), branchId: this.branchId(), stockLocationId: this.stockLocationId(), page: this.page() }).subscribe({
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
