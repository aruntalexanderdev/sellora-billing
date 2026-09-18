import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { UiButton } from '../../../../shared/ui/button/button';
import { AuthService } from '../../../../core/auth/auth.service';
import { StockService } from '../stock.service';
import type { StockDetail as StockDetailModel } from '../stock.models';

const INR = (n: number) => '₹' + n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-stock-detail',
  standalone: true,
  imports: [RouterLink, UiBadge, UiButton, DatePipe],
  templateUrl: './stock-detail.html',
  styleUrl: './stock-detail.scss',
})
export class StockDetail implements OnInit {
  private readonly stockService = inject(StockService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);

  readonly INR = INR;
  readonly detail = signal<StockDetailModel | null>(null);
  readonly loading = signal(true);

  readonly canAdjust = computed(() => this.auth.hasPermission('Inventory.Adjust'));

  ngOnInit(): void {
    const productId = this.route.snapshot.paramMap.get('id')!;
    this.stockService.detail(productId).subscribe({
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
