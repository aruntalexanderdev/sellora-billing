import { describe, expect, it } from 'vitest';
import type { SaleCatalogUnit } from '../sales.models';
import { defaultSaleUnit, isPriced } from './pos-units';

const unit = (over: Partial<SaleCatalogUnit>): SaleCatalogUnit => ({
  productUnitId: 'u',
  unitId: 'unit',
  unitCode: 'PC',
  displayName: 'Piece',
  conversionFactorToBase: 1,
  decimalPlaces: 0,
  isBaseUnit: false,
  isDefault: false,
  currentPrice: null,
  barcode: null,
  ...over,
});

describe('isPriced', () => {
  it('is true for any price including zero, false for null', () => {
    expect(isPriced(unit({ currentPrice: 5 }))).toBe(true);
    expect(isPriced(unit({ currentPrice: 0 }))).toBe(true);
    expect(isPriced(unit({ currentPrice: null }))).toBe(false);
    expect(isPriced(undefined)).toBe(false);
  });
});

describe('defaultSaleUnit', () => {
  it('prefers the default sales unit when it is priced', () => {
    const units = [unit({ productUnitId: 'pc', currentPrice: 5 }), unit({ productUnitId: 'box', isDefault: true, currentPrice: 45 })];
    expect(defaultSaleUnit(units)?.productUnitId).toBe('box');
  });

  it('skips an unpriced default and falls back to the first priced unit', () => {
    const units = [unit({ productUnitId: 'pc' }), unit({ productUnitId: 'box', isDefault: true }), unit({ productUnitId: 'ctn', currentPrice: 90 })];
    expect(defaultSaleUnit(units)?.productUnitId).toBe('ctn');
  });

  it('is undefined when nothing is priced or there are no units', () => {
    expect(defaultSaleUnit([unit({ productUnitId: 'pc' }), unit({ productUnitId: 'box', isDefault: true })])).toBeUndefined();
    expect(defaultSaleUnit([])).toBeUndefined();
    expect(defaultSaleUnit(undefined)).toBeUndefined();
  });
});
