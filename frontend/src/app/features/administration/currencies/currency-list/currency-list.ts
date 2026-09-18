import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../../shared/ui/button/button';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { CurrenciesService } from '../currencies.service';
import type { CurrencyItem } from '../currencies.models';

const COLS: DataTableColumn[] = [
  { label: 'Code', align: 'left' },
  { label: 'Name', align: 'left' },
  { label: 'Symbol', align: 'left' },
  { label: 'Decimals', align: 'right' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

@Component({
  selector: 'app-currency-list',
  standalone: true,
  imports: [UiDataTable, UiButton],
  templateUrl: './currency-list.html',
  styleUrl: '../../users/user-list/user-list.scss',
})
export class CurrencyList implements OnInit {
  private readonly currenciesService = inject(CurrenciesService);
  private readonly toast = inject(ToastService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<CurrencyItem[]>([]);

  readonly canEdit = computed(() => this.auth.hasPermission('Masters.Edit'));

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
          {
            kind: 'actions',
            actions: this.canEdit()
              ? [
                  { label: 'Edit', onClick: () => this.router.navigate(['/administration/currencies', c.id, 'edit']) },
                  ...(isBase ? [] : [{ label: c.isActive ? 'Deactivate' : 'Activate', onClick: () => this.toggle(c) }]),
                ]
              : [],
          },
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

  goToCreate(): void {
    this.router.navigate(['/administration/currencies/new']);
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

  private toggle(c: CurrencyItem): void {
    this.currenciesService.setActive(c.id, !c.isActive).subscribe({
      next: () => {
        this.toast.show(`${c.code} ${c.isActive ? 'deactivated' : 'activated'}.`);
        this.load();
      },
      error: (err) => this.toast.show(err.error?.title ?? 'Could not update this currency.'),
    });
  }
}
