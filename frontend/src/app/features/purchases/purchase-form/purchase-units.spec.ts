import { describe, expect, it } from 'vitest';
import type { PurchaseProductUnit } from '../purchases.models';
import { defaultPurchaseUnit, purchaseUnitOptions } from './purchase-units';

const unit = (over: Partial<PurchaseProductUnit>): PurchaseProductUnit => ({
  id: 'u',
  unitCode: 'PC',
  displayName: 'Piece',
  decimalPlaces: 0,
  conversionFactorToBase: 1,
  isBaseUnit: false,
  isPurchasable: true,
  isDefaultPurchaseUnit: false,
  sortOrder: 0,
  isActive: true,
  ...over,
});

const piece = unit({ id: 'pc', isBaseUnit: true, sortOrder: 0 });
const box = unit({ id: 'box', displayName: 'Box', conversionFactorToBase: 10, sortOrder: 1 });
const carton = unit({ id: 'ctn', displayName: 'Carton', conversionFactorToBase: 100, sortOrder: 2 });

describe('purchaseUnitOptions', () => {
  it('lists only active, purchasable units in sortOrder', () => {
    const units = [carton, unit({ id: 'old', isActive: false }), box, unit({ id: 'np', isPurchasable: false }), piece];
    expect(purchaseUnitOptions(units).map((u) => u.id)).toEqual(['pc', 'box', 'ctn']);
  });

  it('handles missing units', () => {
    expect(purchaseUnitOptions(undefined)).toEqual([]);
  });
});

describe('defaultPurchaseUnit', () => {
  it('prefers the default purchase unit', () => {
    expect(defaultPurchaseUnit([piece, box, { ...carton, isDefaultPurchaseUnit: true }])?.id).toBe('ctn');
  });

  it('ignores a default purchase unit that is inactive or not purchasable', () => {
    expect(defaultPurchaseUnit([piece, { ...carton, isDefaultPurchaseUnit: true, isActive: false }])?.id).toBe('pc');
    expect(defaultPurchaseUnit([piece, { ...carton, isDefaultPurchaseUnit: true, isPurchasable: false }])?.id).toBe('pc');
  });

  it('falls back to the base unit, then the first option', () => {
    expect(defaultPurchaseUnit([carton, box, piece])?.id).toBe('pc');
    expect(defaultPurchaseUnit([carton, box, { ...piece, isPurchasable: false }])?.id).toBe('box');
    expect(defaultPurchaseUnit([])).toBeUndefined();
  });
});
