import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { formatChainQuantity, formatQuantity } from '../../../../shared/units/unit-conversion';
import { StockAdjustmentsService } from '../stock-adjustments.service';
import type { StockAdjustmentDetail as StockAdjustmentDetailModel } from '../stock-adjustments.models';

const money = (n: number, maxDecimals = 2) =>
  '₹' + n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: maxDecimals });

@Component({
  selector: 'app-stock-adjustment-detail',
  standalone: true,
  imports: [RouterLink, UiBadge, DatePipe],
  templateUrl: './stock-adjustment-detail.html',
  styleUrl: './stock-adjustment-detail.scss',
})
export class StockAdjustmentDetail implements OnInit {
  private readonly adjustmentsService = inject(StockAdjustmentsService);
  private readonly route = inject(ActivatedRoute);

  readonly adjustment = signal<StockAdjustmentDetailModel | null>(null);
  readonly loading = signal(true);

  readonly isIncrease = computed(() => this.adjustment()?.direction === 'Increase');

  /** Base-unit quantity formatted with the product's base decimal places. */
  readonly base = (n: number) => formatQuantity(n, this.adjustment()?.baseDecimalPlaces ?? 3);
  readonly entered = (n: number) => formatQuantity(n, this.adjustment()?.enteredDecimalPlaces ?? 3);
  readonly money = (n: number) => money(n);
  /** Per-base-unit costs are stored with up to 4 decimals. */
  readonly baseCost = (n: number) => money(n, 4);
  readonly factor = (n: number) => formatChainQuantity(n);

  ngOnInit(): void {
    this.adjustmentsService.get(this.route.snapshot.paramMap.get('id')!).subscribe({
      next: (a) => {
        this.adjustment.set(a);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }
}
