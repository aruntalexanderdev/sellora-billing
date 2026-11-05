import { describe, expect, it } from 'vitest';
import type { PricingRow } from '../pricing.models';
import { groupPricingRows } from './pricing-page';

const row = (productId: string, productUnitId: string, sortOrder: number): PricingRow => ({
  productId,
  productSku: productId.toUpperCase(),
  productName: `Product ${productId}`,
  productUnitId,
  unitCode: productUnitId,
  unitLabel: productUnitId,
  conversionFactorToBase: 1,
  baseUnitLabel: 'pc',
  sortOrder,
  isBaseUnit: sortOrder === 0,
  costPerUnit: 0,
  currentPrice: null,
  suggestedPrice: null,
  marginPercent: null,
  taxLabel: 'GST 5%',
});

describe('groupPricingRows', () => {
  it('groups by product in server order and sorts units by sortOrder', () => {
    const groups = groupPricingRows([
      row('pen', 'box', 1),
      row('rice', 'kg', 0),
      row('pen', 'ctn', 2),
      row('pen', 'pc', 0),
      row('rice', 'bag', 1),
    ]);
    expect(groups.map((g) => g.productId)).toEqual(['pen', 'rice']);
    expect(groups[0].rows.map((r) => r.productUnitId)).toEqual(['pc', 'box', 'ctn']);
    expect(groups[1].rows.map((r) => r.productUnitId)).toEqual(['kg', 'bag']);
    expect(groups[0].productName).toBe('Product pen');
  });

  it('returns no groups for no rows', () => {
    expect(groupPricingRows([])).toEqual([]);
  });
});
