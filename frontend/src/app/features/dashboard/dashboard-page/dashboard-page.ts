import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { QuantityPipe } from '../../../shared/units/quantity.pipe';
import { Router, RouterLink } from '@angular/router';
import { UiCard } from '../../../shared/ui/card/card';
import { UiButton } from '../../../shared/ui/button/button';
import { UiBadge } from '../../../shared/ui/badge/badge';
import { AuthService } from '../../../core/auth/auth.service';
import { DashboardService } from '../dashboard.service';
import type { Dashboard } from '../dashboard.models';

const INR = (n: number) => '₹' + Math.round(n).toLocaleString('en-IN');

@Component({
  selector: 'app-dashboard-page',
  standalone: true,
  imports: [RouterLink, UiCard, UiButton, UiBadge, QuantityPipe],
  templateUrl: './dashboard-page.html',
  styleUrl: './dashboard-page.scss',
})
export class DashboardPage implements OnInit {
  private readonly dashboardService = inject(DashboardService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly INR = INR;
  readonly loading = signal(true);
  readonly error = signal(false);
  readonly data = signal<Dashboard | null>(null);

  readonly canCreateSale = computed(() => this.auth.hasPermission('Sales.Create'));
  readonly canCreatePurchase = computed(() => this.auth.hasPermission('Purchase.Create'));

  readonly hasAnySection = computed(() => {
    const d = this.data();
    if (!d) return false;
    return d.todaysSales !== null || d.todaysPurchases !== null || d.currentStockValue !== null || d.outstandingCustomerDue !== null;
  });

  ngOnInit(): void {
    this.load();
  }

  retry(): void {
    this.load();
  }

  goToNewSale(): void {
    this.router.navigateByUrl('/sales/new');
  }

  goToNewPurchase(): void {
    this.router.navigateByUrl('/purchases/new');
  }

  formatTime(iso: string): string {
    return new Date(iso).toLocaleString('en-IN', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' });
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(false);
    this.dashboardService.get().subscribe({
      next: (res) => {
        this.data.set(res);
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }
}
