import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { PricingService } from '../pricing.service';
import type { PricingProductGroup, PricingRow, SellingPriceHistoryEntry } from '../pricing.models';

const INR = (n: number) => '₹' + n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** Display a stored conversion factor as-is (no rounding to a fixed number of decimals). */
const FACTOR = (n: number) => n.toLocaleString('en-IN', { maximumFractionDigits: 10 });

/**
 * Groups pricing rows by product (first-seen product order from the server) and orders each
 * product's units by `sortOrder`, i.e. the configured unit hierarchy.
 */
export function groupPricingRows(rows: readonly PricingRow[]): PricingProductGroup[] {
  const groups = new Map<string, PricingProductGroup>();
  for (const row of rows) {
    let group = groups.get(row.productId);
    if (!group) {
      group = { productId: row.productId, productSku: row.productSku, productName: row.productName, rows: [] };
      groups.set(row.productId, group);
    }
    group.rows.push(row);
  }
  for (const group of groups.values()) {
    group.rows.sort((a, b) => a.sortOrder - b.sortOrder);
  }
  return [...groups.values()];
}

@Component({
  selector: 'app-pricing-page',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './pricing-page.html',
  styleUrl: './pricing-page.scss',
})
export class PricingPage implements OnInit {
  private readonly pricingService = inject(PricingService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);

  readonly INR = INR;
  readonly FACTOR = FACTOR;
  readonly search = signal('');
  readonly rows = signal<PricingRow[]>([]);
  readonly groups = computed(() => groupPricingRows(this.rows()));
  readonly loading = signal(true);
  readonly savingUnitId = signal<string | null>(null);

  readonly historyRow = signal<PricingRow | null>(null);
  readonly historyEntries = signal<SellingPriceHistoryEntry[]>([]);
  readonly historyLoading = signal(false);

  ngOnInit(): void {
    const preselect = this.route.snapshot.queryParamMap.get('search');
    if (preselect) this.search.set(preselect);
    this.load();
  }

  onSearch(event: Event): void {
    this.search.set((event.target as HTMLInputElement).value);
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.pricingService.list(this.search()).subscribe({
      next: (rows) => {
        this.rows.set(rows);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  onPriceChange(row: PricingRow, event: Event): void {
    const input = event.target as HTMLInputElement;
    const value = input.value === '' ? null : Number(input.value);
    this.savePrice(row, value, input);
  }

  /** Prefills and saves the server-suggested price (base price x factor). The user can still edit it afterwards. */
  applySuggested(row: PricingRow, input: HTMLInputElement): void {
    if (row.suggestedPrice === null) return;
    input.value = row.suggestedPrice.toString();
    this.savePrice(row, row.suggestedPrice, input);
  }

  private savePrice(row: PricingRow, value: number | null, input: HTMLInputElement): void {
    if (value === null || Number.isNaN(value) || value < 0) {
      this.toast.show('Enter a price of 0 or more.');
      input.value = row.currentPrice?.toString() ?? '';
      return;
    }
    if (value === row.currentPrice) return;

    this.savingUnitId.set(row.productUnitId);
    this.pricingService.setPrice(row.productUnitId, value).subscribe({
      next: () => {
        this.savingUnitId.set(null);
        this.toast.show(`${row.productName} · ${row.unitLabel} price updated to ${INR(value)}.`);
        this.load();
      },
      error: (err) => {
        this.savingUnitId.set(null);
        this.toast.show(err.error?.title ?? 'Could not update this price.');
        input.value = row.currentPrice?.toString() ?? '';
      },
    });
  }

  viewHistory(row: PricingRow): void {
    this.historyRow.set(row);
    this.historyLoading.set(true);
    this.pricingService.history(row.productUnitId).subscribe({
      next: (entries) => {
        this.historyEntries.set(entries);
        this.historyLoading.set(false);
      },
      error: () => this.historyLoading.set(false),
    });
  }

  closeHistory(): void {
    this.historyRow.set(null);
    this.historyEntries.set([]);
  }
}
