import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { FinancialTransactionsService } from '../financial-transactions.service';
import type {
  FinancialTransaction,
  FinancialTransactionStatus,
  FinancialTransactionType,
  MoneyDirection,
  RelatedType,
} from '../financial-transactions.models';

const COLS: DataTableColumn[] = [
  { label: 'Date', align: 'left' },
  { label: 'Transaction', align: 'left' },
  { label: 'Party', align: 'left' },
  { label: 'Source', align: 'left' },
  { label: 'Account', align: 'left' },
  { label: 'Method', align: 'left' },
  { label: 'Money', align: 'right' },
  { label: 'Status', align: 'left' },
];

const PAGE_SIZE = 20;
const money = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

const TYPE_LABEL: Record<FinancialTransactionType, string> = {
  CustomerPayment: 'Customer payment',
  SupplierPayment: 'Supplier payment',
  ExpensePayment: 'Expense paid',
  OpeningBalance: 'Opening balance',
  Transfer: 'Transfer',
};

const ROUTE: Record<RelatedType, string> = {
  Purchase: '/purchases',
  Sale: '/sales',
  Expense: '/accounts/expenses',
};

@Component({
  selector: 'app-financial-transaction-list',
  standalone: true,
  imports: [UiDataTable],
  templateUrl: './financial-transaction-list.html',
  styleUrl: './financial-transaction-list.scss',
})
export class FinancialTransactionList implements OnInit {
  private readonly service = inject(FinancialTransactionsService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly search = signal('');
  readonly type = signal<FinancialTransactionType | ''>('');
  readonly direction = signal<MoneyDirection | ''>('');
  readonly status = signal<FinancialTransactionStatus | ''>('');
  readonly dateFrom = signal('');
  readonly dateTo = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<FinancialTransaction[]>([]);
  readonly total = signal(0);
  readonly moneyIn = signal(0);
  readonly moneyOut = signal(0);
  readonly baseSymbol = signal('₹');

  readonly net = computed(() => this.moneyIn() - this.moneyOut());
  readonly hasMore = computed(() => this.page() * PAGE_SIZE < this.total());
  readonly fmt = (n: number) => this.baseSymbol() + money(n);

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((t) => {
      const cancelled = t.status === 'Cancelled';
      const sign = t.direction === 'In' ? '+' : '−';
      return {
        cells: [
          { kind: 'text', text: t.transactionDate, color: 'var(--color-text-muted)' },
          { kind: 'text', text: TYPE_LABEL[t.type], weight: '600' },
          { kind: 'text', text: t.type === 'Transfer' ? (t.direction === 'In' ? `from ${t.party}` : `to ${t.party}`) : t.party },
          t.relatedType && t.relatedId
            ? { kind: 'link', text: `${t.relatedType} ${t.relatedNo}`, onClick: () => this.router.navigate([ROUTE[t.relatedType!], t.relatedId]) }
            : { kind: 'text', text: '—', color: 'var(--color-text-muted)' },
          { kind: 'text', text: t.accountName ?? 'Not tracked', color: 'var(--color-text-muted)' },
          { kind: 'text', text: t.type === 'OpeningBalance' || t.type === 'Transfer' ? '—' : t.paymentMethod, color: 'var(--color-text-muted)' },
          {
            kind: 'text',
            text: `${sign}${money(t.baseAmount)}`,
            // A foreign-currency movement shows what actually changed hands under its base-currency value.
            sub: t.currencyCode !== this.baseCode() ? `${t.currencyCode} ${money(t.amount)}` : undefined,
            align: 'right',
            weight: '650',
            color: cancelled ? 'var(--color-text-muted)' : t.direction === 'In' ? 'var(--color-success, #15803d)' : 'var(--color-danger)',
          },
          { kind: 'badge', text: t.status, tone: cancelled ? 'mute' : 'good' },
        ],
      };
    }),
  );

  private readonly baseCode = signal('INR');

  ngOnInit(): void {
    this.load();
  }

  onSearch(value: string): void {
    this.search.set(value);
    this.resetAndLoad();
  }

  onFilter(key: 'type' | 'direction' | 'status' | 'dateFrom' | 'dateTo', value: string): void {
    (this[key] as { set: (v: string) => void }).set(value);
    this.resetAndLoad();
  }

  onPageChange(page: number): void {
    this.page.set(page);
    this.load();
  }

  retry(): void {
    this.load();
  }

  private resetAndLoad(): void {
    this.page.set(1);
    this.load();
  }

  private load(): void {
    this.state.set('loading');
    this.service
      .list({
        search: this.search(),
        type: this.type() || undefined,
        direction: this.direction() || undefined,
        status: this.status() || undefined,
        dateFrom: this.dateFrom() || undefined,
        dateTo: this.dateTo() || undefined,
        page: this.page(),
        pageSize: PAGE_SIZE,
      })
      .subscribe({
        next: (res) => {
          this.baseCode.set(res.baseCurrencyCode);
          this.baseSymbol.set(res.baseCurrencySymbol);
          this.moneyIn.set(res.moneyIn);
          this.moneyOut.set(res.moneyOut);
          this.items.set(res.items);
          this.total.set(res.total);
          this.state.set('ready');
        },
        error: () => this.state.set('error'),
      });
  }
}
