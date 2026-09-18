import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../../shared/ui/button/button';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { AuthService } from '../../../../core/auth/auth.service';
import { SettingsService } from '../../../masters/settings/settings.service';
import { ExchangeRatesService } from '../exchange-rates.service';
import type { ExchangeRateItem } from '../exchange-rates.models';

const COLS: DataTableColumn[] = [
  { label: 'Currency', align: 'left' },
  { label: 'Rate to base', align: 'right' },
  { label: 'Effective from', align: 'left' },
  { label: 'Added', align: 'left' },
  { label: '', align: 'right' },
];

@Component({
  selector: 'app-exchange-rate-list',
  standalone: true,
  imports: [UiDataTable, UiButton],
  templateUrl: './exchange-rate-list.html',
  styleUrl: '../../users/user-list/user-list.scss',
})
export class ExchangeRateList implements OnInit {
  private readonly ratesService = inject(ExchangeRatesService);
  private readonly settingsService = inject(SettingsService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<ExchangeRateItem[]>([]);
  readonly baseCode = signal('');
  readonly baseSymbol = signal('');

  readonly canEdit = computed(() => this.auth.hasPermission('Masters.Edit'));

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((r) => ({
      cells: [
        { kind: 'text', text: r.currencyCode, weight: '650' },
        {
          kind: 'text',
          text: `1 ${r.currencyCode} = ${this.baseSymbol()}${r.rateToBase.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 6 })}`,
          align: 'right',
          weight: '600',
        },
        { kind: 'text', text: r.effectiveDate, color: 'var(--color-text-muted)' },
        { kind: 'text', text: r.createdAt.slice(0, 10), color: 'var(--color-text-muted)' },
        {
          kind: 'actions',
          actions: this.canEdit() ? [{ label: 'Edit', onClick: () => this.router.navigate(['/administration/exchange-rates', r.id, 'edit']) }] : [],
        },
      ],
    })),
  );

  ngOnInit(): void {
    this.load();
  }

  retry(): void {
    this.load();
  }

  goToCreate(): void {
    this.router.navigate(['/administration/exchange-rates/new']);
  }

  private load(): void {
    this.state.set('loading');
    this.settingsService.get().subscribe({
      next: (s) => {
        this.baseCode.set(s.baseCurrencyCode);
        this.baseSymbol.set(s.baseCurrencySymbol);
      },
    });
    this.ratesService.list().subscribe({
      next: (rates) => {
        this.items.set(rates);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }
}
