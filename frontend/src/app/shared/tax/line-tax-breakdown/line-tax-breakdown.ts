import { Component, computed, input } from '@angular/core';
import { displayRows } from '../tax-breakdown';
import type { TaxRowView } from '../tax-preview.models';

const format = (n: number, places: number) => n.toLocaleString('en-IN', { minimumFractionDigits: places, maximumFractionDigits: places });

/**
 * Tax rows of one document line: code, rate (or amount per unit), base and tax. Legacy rows (saved before the
 * calculator) are shown as one "Tax" row. Shared by purchases (TP-3) and sales (TP-4).
 */
@Component({
  selector: 'app-line-tax-breakdown',
  standalone: true,
  templateUrl: './line-tax-breakdown.html',
  styleUrl: './line-tax-breakdown.scss',
})
export class LineTaxBreakdown {
  readonly rows = input.required<readonly TaxRowView[] | null | undefined>();
  readonly symbol = input<string>('');
  /** Decimal places of the amounts (the document currency's precision). */
  readonly precision = input<number>(2);
  /** Label of the base quantity of per-unit rows (e.g. "pc"). */
  readonly baseUnitLabel = input<string>('');

  readonly view = computed(() =>
    displayRows(this.rows()).map((r) => ({
      code: r.code,
      rate: r.method === 'AmountPerBaseUnit' ? `${this.money(r.amountPerBaseUnit ?? 0)} / ${this.baseUnitLabel() || 'unit'}` : r.ratePercent == null ? '—' : `${r.ratePercent}%`,
      base: r.method === 'AmountPerBaseUnit' ? `${r.baseAmount} ${this.baseUnitLabel()}`.trim() : this.money(r.baseAmount),
      tax: this.money(r.taxAmount),
      legacy: !!r.isLegacy,
      treatment: r.treatment && r.treatment !== 'Taxable' ? r.treatment : null,
    })),
  );

  readonly total = computed(() => this.money(displayRows(this.rows()).reduce((s, r) => s + r.taxAmount, 0)));

  private money(n: number): string {
    return this.symbol() + format(n, this.precision());
  }
}
