import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { CurrenciesService } from '../currencies.service';
import type { CurrencyItem } from '../currencies.models';

const COLS: DataTableColumn[] = [
  { label: 'Code', align: 'left' },
  { label: 'Name', align: 'left' },
  { label: 'Symbol', align: 'left' },
  { label: 'Decimals', align: 'right' },
  { label: 'Status', align: 'left' },
];

@Component({
  selector: 'app-currency-list',
  standalone: true,
  imports: [UiDataTable],
  templateUrl: './currency-list.html',
  styleUrl: '../../users/user-list/user-list.scss',
})
export class CurrencyList implements OnInit {
  private readonly currenciesService = inject(CurrenciesService);

  readonly cols = COLS;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<CurrencyItem[]>([]);

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((c) => {
      const isBase = c.isBase;
      return {
        cells: [
          { kind: 'text', text: c.code, weight: '650' },
          { kind: 'text', text: c.name, weight: '600' },
          { kind: 'text', text: c.symbol, color: 'var(--color-text-muted)' },
          { kind: 'text', text: String(c.decimalPrecision), align: 'right', color: 'var(--color-text-muted)' },
          { kind: 'badge', text: isBase ? 'Base' : c.isActive ? 'Active' : 'Inactive', tone: isBase ? 'good' : c.isActive ? 'good' : 'mute' },
        ],
      };
    }),
  );

  ngOnInit(): void {
    this.load();
  }

  retry(): void {
    this.load();
  }

  private load(): void {
    this.state.set('loading');
    this.currenciesService.list().subscribe({
      next: (currencies) => {
        this.items.set(currencies);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }
}
