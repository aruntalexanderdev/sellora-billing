import { displayRows, placeOfSupplyLabel, taxByComponent, taxColumns, taxInColumn } from './tax-breakdown';
import type { TaxRowView } from './tax-preview.models';

const row = (code: string, kind: TaxRowView['kind'], taxAmount: number, extra: Partial<TaxRowView> = {}): TaxRowView => ({
  code, kind, method: 'Percent', ratePercent: 9, amountPerBaseUnit: null, baseAmount: 100, taxAmount, ...extra,
});
const legacy = (taxAmount: number, ratePercent = 18): TaxRowView => row('GST', 'Other', taxAmount, { isLegacy: true, ratePercent });

describe('displayRows', () => {
  it('shows legacy rows as one "Tax" row and keeps calculator rows', () => {
    const rows = displayRows([legacy(10), legacy(8)]);
    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({ code: 'Tax', taxAmount: 18, ratePercent: 18, isLegacy: true });
    expect(displayRows([row('CGST', 'Cgst', 9), row('SGST', 'Sgst', 9)]).map((r) => r.code)).toEqual(['CGST', 'SGST']);
    expect(displayRows(null)).toEqual([]);
  });
});

describe('taxColumns / taxByComponent', () => {
  const intra = { rows: [row('SGST', 'Sgst', 9), row('CGST', 'Cgst', 9)] };
  const cess = { rows: [row('CGST', 'Cgst', 5), row('SGST', 'Sgst', 5), row('CESS', 'Cess', 2, { method: 'AmountPerBaseUnit', ratePercent: null, amountPerBaseUnit: 1 })] };

  it('lists the distinct codes across lines in kind order (CGST, SGST, Cess)', () => {
    expect(taxColumns([intra, cess])).toEqual(['CGST', 'SGST', 'CESS']);
  });

  it('puts the legacy "Tax" column last and sums per code', () => {
    expect(taxColumns([{ rows: [legacy(18)] }, intra])).toEqual(['CGST', 'SGST', 'Tax']);
    expect(taxByComponent([intra, cess]).map((c) => [c.code, c.taxAmount])).toEqual([['CGST', 14], ['SGST', 14], ['CESS', 2]]);
  });

  it('reads one cell, with the percent only for percent rows', () => {
    expect(taxInColumn(cess.rows, 'CGST')).toEqual({ taxAmount: 5, ratePercent: 9 });
    expect(taxInColumn(cess.rows, 'CESS')).toEqual({ taxAmount: 2, ratePercent: null });
    expect(taxInColumn(cess.rows, 'IGST')).toBeNull();
    expect(taxInColumn([legacy(7)], 'Tax')?.taxAmount).toBe(7);
  });
});

describe('placeOfSupplyLabel', () => {
  it('maps to Same state / Other state / VAT', () => {
    expect(placeOfSupplyLabel('IntraState', 'Gst')).toBe('Same state');
    expect(placeOfSupplyLabel('InterState', 'Gst')).toBe('Other state');
    expect(placeOfSupplyLabel('NotApplicable', 'Vat')).toBe('VAT');
    expect(placeOfSupplyLabel('NotApplicable')).toBe('VAT');
    expect(placeOfSupplyLabel(null)).toBeNull();
  });
});
