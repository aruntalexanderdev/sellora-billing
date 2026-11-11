import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { UiButton } from '../../../../shared/ui/button/button';
import { AuthService } from '../../../../core/auth/auth.service';
import { StockService } from '../stock.service';
import type { BatchConsumption, BatchStatus, StockBatch, StockDetail as StockDetailModel, StockMovement } from '../stock.models';

import { QuantityPipe } from '../../../../shared/units/quantity.pipe';
import { formatBreakdown, formatQuantity } from '../../../../shared/units/unit-conversion';
import { enteredLabel, movementTone, movementTypeLabel } from '../../../../shared/units/stock-movement';

const INR = (n: number) => '₹' + n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-stock-detail',
  standalone: true,
  imports: [RouterLink, UiBadge, UiButton, DatePipe, QuantityPipe],
  templateUrl: './stock-detail.html',
  styleUrl: './stock-detail.scss',
})
export class StockDetail implements OnInit {
  private readonly stockService = inject(StockService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);

  readonly INR = INR;
  readonly movementTone = movementTone;
  readonly movementTypeLabel = movementTypeLabel;
  readonly enteredLabel = enteredLabel;
  readonly formatQuantity = formatQuantity;
  readonly detail = signal<StockDetailModel | null>(null);
  readonly loading = signal(true);
  /** Packaging breakdown text for a base quantity of this product, or null (base-only / zero). */
  breakdownText(d: StockDetailModel, qty: number): string | null {
    return formatBreakdown(qty, d.breakdownUnits, d.baseDecimalPlaces, d.baseUnitLabel);
  }

  readonly transferRoute = (id: string) => ['/inventory/stock-transfers', id];

  /** Movement links to its transfer only for StockTransfer references that carry an id. */
  transferLinkId(m: StockMovement): string | null {
    return m.referenceType === 'StockTransfer' && m.referenceId ? m.referenceId : null;
  }

  readonly canAdjust = computed(() => this.auth.hasPermission('Inventory.Adjust'));
  readonly canViewPurchases = computed(() => this.auth.hasPermission('Purchase.View'));
  readonly canViewSales = computed(() => this.auth.hasPermission('Sales.View'));

  // Which batch rows are expanded, and the sales each expanded batch has fed (loaded on first expand).
  readonly expanded = signal<ReadonlySet<string>>(new Set());
  readonly consumptions = signal<ReadonlyMap<string, BatchConsumption[] | 'error'>>(new Map());

  batchStatus(b: StockBatch): BatchStatus {
    if (b.remainingBaseQty <= 0) return 'Depleted';
    return b.remainingBaseQty < b.purchasedBaseQty ? 'Partly used' : 'Available';
  }

  consumedQty(b: StockBatch): number {
    return b.purchasedBaseQty - b.remainingBaseQty;
  }

  toggleBatch(b: StockBatch): void {
    const next = new Set(this.expanded());
    if (next.has(b.id)) {
      next.delete(b.id);
    } else {
      next.add(b.id);
      if (this.canViewSales() && !this.consumptions().has(b.id)) this.loadConsumptions(b.id);
    }
    this.expanded.set(next);
  }

  private loadConsumptions(batchId: string): void {
    this.stockService.batchConsumptions(batchId).subscribe({
      next: (rows) => this.consumptions.update((m) => new Map(m).set(batchId, rows)),
      error: () => this.consumptions.update((m) => new Map(m).set(batchId, 'error')),
    });
  }

  ngOnInit(): void {
    const productId = this.route.snapshot.paramMap.get('id')!;
    this.stockService.detail(productId, null, true).subscribe({
      next: (d) => {
        this.detail.set(d);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  goToAdjust(): void {
    this.router.navigate(['/inventory/stock-adjustments/new'], { queryParams: { productId: this.detail()!.productId } });
  }
}
