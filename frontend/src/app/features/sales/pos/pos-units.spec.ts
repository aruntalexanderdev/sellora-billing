import { describe, expect, it } from 'vitest';
import type { SaleCatalogUnit } from '../sales.models';
import { defaultSaleUnit } from './pos-units';

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
  ...over,
});

describe('defaultSaleUnit', () => {
  it('prefers the default sales unit even when unpriced', () => {
    const units = [unit({ productUnitId: 'pc', currentPrice: 5 }), unit({ productUnitId: 'box', isDefault: true })];
    expect(defaultSaleUnit(units)?.productUnitId).toBe('box');
  });

  it('falls back to the first priced unit', () => {
    const units = [unit({ productUnitId: 'pc' }), unit({ productUnitId: 'box', currentPrice: 0 }), unit({ productUnitId: 'ctn', currentPrice: 90 })];
    expect(defaultSaleUnit(units)?.productUnitId).toBe('box');
  });

  it('falls back to the first unit, or undefined when there are none', () => {
    expect(defaultSaleUnit([unit({ productUnitId: 'pc' }), unit({ productUnitId: 'box' })])?.productUnitId).toBe('pc');
    expect(defaultSaleUnit([])).toBeUndefined();
    expect(defaultSaleUnit(undefined)).toBeUndefined();
  });
});
