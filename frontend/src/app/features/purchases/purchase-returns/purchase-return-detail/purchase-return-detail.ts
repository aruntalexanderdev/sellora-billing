import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { PurchasesService } from '../../purchases.service';
import type { PurchaseReturnDetail as PurchaseReturnDetailModel } from '../../purchases.models';

const FORMAT = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-purchase-return-detail',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './purchase-return-detail.html',
  styleUrl: '../../purchase-detail/purchase-detail.scss',
})
export class PurchaseReturnDetail implements OnInit {
  private readonly purchasesService = inject(PurchasesService);
  private readonly route = inject(ActivatedRoute);

  readonly ret = signal<PurchaseReturnDetailModel | null>(null);
  readonly loading = signal(true);

  readonly base = (n: number) => (this.ret()?.baseCurrencySymbol ?? '₹') + FORMAT(n);
  readonly original = (n: number) => (this.ret()?.currencySymbol ?? '') + FORMAT(n);

  ngOnInit(): void {
    this.purchasesService.getReturn(this.route.snapshot.paramMap.get('id')!).subscribe({
      next: (r) => {
        this.ret.set(r);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }
}
