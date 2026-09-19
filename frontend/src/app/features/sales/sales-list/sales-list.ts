import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../shared/ui/button/button';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../shared/ui/data-table/data-table.types';
import { AuthService } from '../../../core/auth/auth.service';
import { SalesService } from '../sales.service';
import type { SalesInvoiceListItem } from '../sales.models';

const COLS: DataTableColumn[] = [
  { label: 'Invoice', align: 'left' },
  { label: 'Customer', align: 'left' },
  { label: 'Date', align: 'left' },
  { label: 'Total', align: 'right' },
  { label: 'Paid', align: 'right' },
  { label: 'Due', align: 'right' },
  { label: 'Status', align: 'left' },
  { label: 'Payment', align: 'left' },
  { label: '', align: 'right' },
];

const INR = (n: number) => '₹' + Math.round(n).toLocaleString('en-IN');

@Component({
  selector: 'app-sales-list',
  standalone: true,
  imports: [UiDataTable, UiButton],
  templateUrl: './sales-list.html',
  styleUrl: '../../administration/users/user-list/user-list.scss',
})
export class SalesList implements OnInit {
  private readonly salesService = inject(SalesService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly search = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<SalesInvoiceListItem[]>([]);
  readonly total = signal(0);

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

  retry(): void {
    this.load();
  }

  goToCreate(): void {
    this.router.navigate(['/sales/new']);
  }

  private load(): void {
    this.state.set('loading');
    this.salesService.list({ search: this.search(), page: this.page() }).subscribe({
      next: (res) => {
        this.items.set(res.items);
        this.total.set(res.total);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }
}
