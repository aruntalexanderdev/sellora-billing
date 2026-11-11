import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DocumentScope, type DocumentScopeItem } from '../../../../shared/scope/document-scope';
import { placeLabel } from '../../../../shared/scope/list-scope';
import { loadErrorText } from '../../../../shared/scope/problem-text';
import { SalesService } from '../../sales.service';
import type { SalesReturnDetail as SalesReturnDetailModel } from '../../sales.models';

import { formatQuantity } from '../../../../shared/units/unit-conversion';

const INR = (n: number) => '₹' + n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-sales-return-detail',
  standalone: true,
  imports: [RouterLink, DocumentScope],
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
  readonly loadError = signal<string | null>(null);
  readonly scopeItems = computed<DocumentScopeItem[]>(() => {
    const r = this.ret();
    if (!r) return [];
    return [
      { label: 'Branch', value: placeLabel(r.branchCode, r.branchName), testId: 'scope-branch' },
      { label: 'Restocked to', value: placeLabel(r.stockLocationCode, r.stockLocationName), testId: 'scope-location' },
    ];
  });

  ngOnInit(): void {
    this.salesService.getReturn(this.route.snapshot.paramMap.get('id')!).subscribe({
      next: (r) => {
        this.ret.set(r);
        this.loading.set(false);
      },
      error: (err) => {
        this.loadError.set(loadErrorText(err, 'Could not load this return.'));
        this.loading.set(false);
      },
    });
  }
}
