import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { PurchasesService } from '../../purchases.service';
import type { PurchaseReturnListItem } from '../../purchases.models';

import { AuthService } from '../../../../core/auth/auth.service';
import { problemMessage, toProblem } from '../../../../core/http/api-errors';
import { LoadError } from '../../../../shared/scope/load-error';
import { ScopeFilters } from '../../../../shared/scope/scope-filters';
import { injectBranchColumnVisible, scopeCells, scopeColumns } from '../../../../shared/scope/list-scope';
const HEAD_COLS: DataTableColumn[] = [
  { label: 'Return', align: 'left' },
  { label: 'Date', align: 'left' },
  { label: 'Purchase', align: 'left' },
  { label: 'Supplier', align: 'left' },
  { label: 'Credit', align: 'right' },
];

const PAGE_SIZE = 20;
const money = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-purchase-return-list',
  standalone: true,
  imports: [UiDataTable, ScopeFilters, LoadError],
  templateUrl: './purchase-return-list.html',
  styleUrl: '../../../../shared/scope/scope-list.scss',
})
export class PurchaseReturnList implements OnInit {
  private readonly purchasesService = inject(PurchasesService);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);

  readonly cols = computed<DataTableColumn[]>(() => [...HEAD_COLS, ...scopeColumns(this.showBranch(), this.showLocation())]);
  readonly search = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<PurchaseReturnListItem[]>([]);
  readonly total = signal(0);
  readonly branchId = signal<string | null>(null);
  readonly stockLocationId = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);
  readonly showBranch = injectBranchColumnVisible();
  readonly showLocation = computed(() => this.auth.hasPermission('StockLocations.View'));
  readonly hasMore = computed(() => this.page() * PAGE_SIZE < this.total());

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((r) => ({
      cells: [
        { kind: 'link', text: r.returnNo, onClick: () => this.router.navigate(['/purchases/returns', r.id]) },
        { kind: 'text', text: r.returnDate, color: 'var(--color-text-muted)' },
        { kind: 'link', text: r.purchaseNo, onClick: () => this.router.navigate(['/purchases', r.purchaseOrderId]) },
        { kind: 'text', text: r.supplierName, weight: '600' },
        {
          kind: 'text',
          text: '₹' + money(r.total),
          // A foreign-currency return shows what the supplier credited under its base-currency value.
          sub: r.originalTotal !== r.total ? `${r.currencyCode} ${money(r.originalTotal)}` : undefined,
          align: 'right',
          weight: '650',
        },
        ...scopeCells(r, this.showBranch(), this.showLocation()),
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

  private load(): void {
    this.state.set('loading');
    this.errorMessage.set(null);
    this.purchasesService
      .returns({ search: this.search(), branchId: this.branchId(), stockLocationId: this.stockLocationId(), page: this.page(), pageSize: PAGE_SIZE })
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
