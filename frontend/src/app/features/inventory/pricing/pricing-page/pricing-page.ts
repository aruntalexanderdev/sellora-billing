import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { PricingService } from '../pricing.service';
import type { PricingRow, SellingPriceHistoryEntry } from '../pricing.models';

const INR = (n: number) => '₹' + n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

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
  readonly search = signal('');
  readonly rows = signal<PricingRow[]>([]);
  readonly loading = signal(true);
  readonly savingModeId = signal<string | null>(null);

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
    const raw = (event.target as HTMLInputElement).value;
    const value = raw === '' ? null : Number(raw);

    if (value === null || Number.isNaN(value) || value < 0) {
      this.toast.show('Enter a price of 0 or more.');
      (event.target as HTMLInputElement).value = row.currentPrice?.toString() ?? '';
      return;
    }
    if (value === row.currentPrice) return;

    this.savingModeId.set(row.sellingModeId);
    this.pricingService.setPrice(row.sellingModeId, value).subscribe({
      next: () => {
        this.savingModeId.set(null);
        this.toast.show(`${row.productName} · ${row.sellingModeLabel} price updated to ${INR(value)}.`);
        this.load();
      },
      error: (err) => {
        this.savingModeId.set(null);
        this.toast.show(err.error?.title ?? 'Could not update this price.');
        (event.target as HTMLInputElement).value = row.currentPrice?.toString() ?? '';
      },
    });
  }

  viewHistory(row: PricingRow): void {
    this.historyRow.set(row);
    this.historyLoading.set(true);
    this.pricingService.history(row.sellingModeId).subscribe({
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
