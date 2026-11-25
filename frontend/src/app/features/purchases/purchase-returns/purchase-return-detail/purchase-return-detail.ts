import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DocumentScope, type DocumentScopeItem } from '../../../../shared/scope/document-scope';
import { placeLabel } from '../../../../shared/scope/list-scope';
import { loadErrorText } from '../../../../shared/scope/problem-text';
import { PurchasesService } from '../../purchases.service';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { LineTaxBreakdown } from '../../../../shared/tax/line-tax-breakdown/line-tax-breakdown';
import { taxByComponent } from '../../../../shared/tax/tax-breakdown';
import type { PurchaseReturnDetail as PurchaseReturnDetailModel } from '../../purchases.models';

const FORMAT = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-purchase-return-detail',
  standalone: true,
  imports: [RouterLink, DocumentScope, LineTaxBreakdown],
  templateUrl: './purchase-return-detail.html',
  styleUrl: '../../purchase-detail/purchase-detail.scss',
})
export class PurchaseReturnDetail implements OnInit {
  readonly formatQuantity = formatQuantity;
  private readonly purchasesService = inject(PurchasesService);
  private readonly route = inject(ActivatedRoute);

  readonly ret = signal<PurchaseReturnDetailModel | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly scopeItems = computed<DocumentScopeItem[]>(() => {
    const r = this.ret();
    if (!r) return [];
    return [
      { label: 'Branch', value: placeLabel(r.branchCode, r.branchName), testId: 'scope-branch' },
      { label: 'Issued from', value: placeLabel(r.stockLocationCode, r.stockLocationName), testId: 'scope-location' },
    ];
  });

  /** Reversed tax per component over the return (legacy rows as one "Tax"). */
  readonly taxByCode = computed(() => taxByComponent((this.ret()?.lines ?? []).map((l) => ({ rows: l.taxes ?? [] }))));
  readonly expanded = signal<ReadonlySet<string>>(new Set());
  toggle(id: string): void {
    this.expanded.update((set) => {
      const next = new Set(set);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  readonly base = (n: number) => (this.ret()?.baseCurrencySymbol ?? '₹') + FORMAT(n);
  readonly original = (n: number) => (this.ret()?.currencySymbol ?? '') + FORMAT(n);

  ngOnInit(): void {
    this.purchasesService.getReturn(this.route.snapshot.paramMap.get('id')!).subscribe({
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
