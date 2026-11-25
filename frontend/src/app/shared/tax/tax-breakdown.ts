import type { PlaceOfSupply, TaxRegime, TaxRowView } from './tax-preview.models';
import type { TaxComponentKind } from '../../features/inventory/tax-setup/tax-setup.models';

/** Column / row label of tax rows saved before the calculator. */
export const LEGACY_TAX_CODE = 'Tax';

const KIND_ORDER: TaxComponentKind[] = ['Cgst', 'Sgst', 'Utgst', 'Igst', 'Cess', 'Vat', 'Other'];

/** Display code of a row: legacy rows are all shown as one "Tax" row. */
export function rowCode(row: Pick<TaxRowView, 'code' | 'isLegacy'>): string {
  return row.isLegacy ? LEGACY_TAX_CODE : row.code;
}

/** Collapses legacy rows into one "Tax" row (rate kept when they agree); calculator rows are unchanged and keep their order. */
export function displayRows(rows: readonly TaxRowView[] | null | undefined): TaxRowView[] {
  const list = rows ?? [];
  const legacy = list.filter((r) => r.isLegacy);
  const rest = list.filter((r) => !r.isLegacy);
  if (legacy.length === 0) return [...rest];
  const rates = new Set(legacy.map((r) => r.ratePercent));
  const merged: TaxRowView = {
    componentId: null,
    code: LEGACY_TAX_CODE,
    kind: 'Other',
    method: 'Percent',
    ratePercent: rates.size === 1 ? legacy[0].ratePercent : null,
    amountPerBaseUnit: null,
    baseAmount: legacy[0].baseAmount,
    taxAmount: legacy.reduce((s, r) => s + r.taxAmount, 0),
    isLegacy: true,
  };
  return [...rest, merged];
}

/** Sum of tax per displayed code over several lines (header "tax by component"), in kind order then first seen. */
export function taxByComponent(lines: readonly { rows: readonly TaxRowView[] }[]): { code: string; kind: TaxComponentKind; taxAmount: number; isLegacy: boolean }[] {
  const map = new Map<string, { code: string; kind: TaxComponentKind; taxAmount: number; isLegacy: boolean }>();
  for (const line of lines) {
    for (const row of displayRows(line.rows)) {
      const entry = map.get(row.code) ?? { code: row.code, kind: row.kind, taxAmount: 0, isLegacy: !!row.isLegacy };
      entry.taxAmount += row.taxAmount;
      map.set(row.code, entry);
    }
  }
  return sortColumns([...map.values()]);
}

/** Distinct tax columns of a set of lines (CGST/SGST/IGST/Cess/VAT..., legacy as one "Tax" column last). */
export function taxColumns(lines: readonly { rows: readonly TaxRowView[] }[]): string[] {
  return taxByComponent(lines).map((c) => c.code);
}

function sortColumns<T extends { kind: TaxComponentKind; isLegacy: boolean }>(items: T[]): T[] {
  const rank = (i: T) => (i.isLegacy ? KIND_ORDER.length + 1 : KIND_ORDER.indexOf(i.kind));
  return items
    .map((item, idx) => ({ item, idx }))
    .sort((a, b) => rank(a.item) - rank(b.item) || a.idx - b.idx)
    .map((x) => x.item);
}

/** Tax of one line in one column (a column code from `taxColumns`); 0 when the line has no such row. */
export function taxInColumn(rows: readonly TaxRowView[], code: string): { taxAmount: number; ratePercent: number | null } | null {
  const row = displayRows(rows).find((r) => r.code === code);
  return row ? { taxAmount: row.taxAmount, ratePercent: row.method === 'Percent' ? row.ratePercent : null } : null;
}

/** Badge text of the place of supply: VAT regimes have none. */
export function placeOfSupplyLabel(place: PlaceOfSupply | null | undefined, regime?: TaxRegime | null): string | null {
  if (regime === 'Vat') return 'VAT';
  switch (place) {
    case 'IntraState': return 'Same state';
    case 'InterState': return 'Other state';
    case 'NotApplicable': return regime === 'Gst' ? null : 'VAT';
    default: return null;
  }
}
