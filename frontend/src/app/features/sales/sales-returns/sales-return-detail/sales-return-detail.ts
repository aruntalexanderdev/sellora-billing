import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DocumentScope, type DocumentScopeItem } from '../../../../shared/scope/document-scope';
import { placeLabel } from '../../../../shared/scope/list-scope';
import { loadErrorText } from '../../../../shared/scope/problem-text';
import { SalesService } from '../../sales.service';
import type { SalesReturnDetail as SalesReturnDetailModel, SalesReturnLine } from '../../sales.models';
import { LEGACY_TAX_CODE, taxByComponent, taxColumns, taxInColumn } from '../../../../shared/tax/tax-breakdown';
import type { TaxRowView } from '../../../../shared/tax/tax-preview.models';

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

  /** Reversed tax rows of a return line; a line without rows (history) shows its tax as one legacy "Tax" row. */
  readonly rowsOf = (line: SalesReturnLine): TaxRowView[] =>
    line.taxes && line.taxes.length > 0
      ? line.taxes
      : [{ code: LEGACY_TAX_CODE, kind: 'Other', method: 'Percent', ratePercent: null, amountPerBaseUnit: null, baseAmount: line.taxableValue, taxAmount: line.taxAmount, isLegacy: true }];
  private readonly lineRows = computed(() => (this.ret()?.lines ?? []).map((l) => ({ rows: this.rowsOf(l) })));
  /** Distinct tax codes across the return lines (CGST/SGST or IGST, Cess, VAT; legacy as one "Tax" column last). */
  readonly taxCols = computed(() => taxColumns(this.lineRows()));
  readonly taxByCode = computed(() => taxByComponent(this.lineRows()));
  readonly cell = (line: SalesReturnLine, code: string) => taxInColumn(this.rowsOf(line), code);

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
