import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { SalesService } from '../../sales.service';
import type { SalesReturnDetail as SalesReturnDetailModel } from '../../sales.models';

import { formatQuantity } from '../../../../shared/units/unit-conversion';

const INR = (n: number) => '₹' + n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-sales-return-detail',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './sales-return-detail.html',
  styleUrl: '../../../purchases/purchase-detail/purchase-detail.scss',
})
export class SalesReturnDetail implements OnInit {
  private readonly salesService = inject(SalesService);
  private readonly route = inject(ActivatedRoute);

  readonly INR = INR;
  readonly formatQuantity = formatQuantity;
  readonly ret = signal<SalesReturnDetailModel | null>(null);
  readonly loading = signal(true);

  ngOnInit(): void {
    this.salesService.getReturn(this.route.snapshot.paramMap.get('id')!).subscribe({
      next: (r) => {
        this.ret.set(r);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }
}
