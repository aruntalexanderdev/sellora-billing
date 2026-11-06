import { describe, expect, it } from 'vitest';
import { FormControl } from '@angular/forms';
import { quantityStep, quantityValidators } from '../../../shared/forms/quantity-input';
import type { PurchaseReturnUnit, PurchaseProductUnit } from '../purchases.models';
import {
  defaultPurchaseUnit, defaultReturnUnit, proratedCredit, purchaseBaseDecimalsError, purchaseUnitOptions,
  returnAllChoice, returnBaseQty, returnQtyError,
} from './purchase-units';

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

const ru = (over: Partial<PurchaseReturnUnit>): PurchaseReturnUnit => ({
  productUnitId: 'pc', unitCode: 'PC', displayName: 'Piece', decimalPlaces: 0, conversionFactorToBase: 1,
  isLineUnit: false, maxQty: 163, ...over,
});
// 2 Full Cartons bought, 37 pc already returned: 163 pc left.
const returnUnits = [
  ru({}),
  ru({ productUnitId: 'box', unitCode: 'BOX', displayName: 'Box', conversionFactorToBase: 10, maxQty: 16 }),
  ru({ productUnitId: 'fc', unitCode: 'FC', displayName: 'Full Carton', conversionFactorToBase: 100, maxQty: 1, isLineUnit: true }),
];

describe('purchase qty validators follow the selected unit', () => {
  it('Box (0 dp) rejects 1.5, kg (3 dp) accepts it', () => {
    const qty = new FormControl<number | null>(1.5, quantityValidators(0));
    expect(qty.errors?.['maxDecimalPlaces']).toBeTruthy();
    qty.setValidators(quantityValidators(3));
    qty.updateValueAndValidity();
    expect(qty.valid).toBe(true);
    expect(quantityStep(0)).toBe('1');
    expect(quantityStep(3)).toBe('0.001');
  });
});

describe('purchaseBaseDecimalsError', () => {
  const half = { unitCode: 'PK', conversionFactorToBase: 0.5 };
  it('rejects a quantity whose base value needs more decimals than the base unit tracks', () => {
    expect(purchaseBaseDecimalsError(0.01, half, 2, 'kg')).toContain('tracked to 2 decimal');
    expect(purchaseBaseDecimalsError(0.02, half, 2, 'kg')).toBeNull();
  });
});

describe('purchase return helpers', () => {
  it('defaults to the line unit', () => {
    expect(defaultReturnUnit(returnUnits)?.productUnitId).toBe('fc');
  });

  it('previews base quantity and prorates the credit by base', () => {
    expect(returnBaseQty(37, returnUnits[0])).toBe(37);
    expect(returnBaseQty(2, returnUnits[1])).toBe(20);
    // 2 FC line (200 base) worth 2000: 37 pc -> 370, 1 FC -> 1000.
    expect(proratedCredit(2000, 37, 200)).toBeCloseTo(370);
    expect(proratedCredit(2000, returnBaseQty(1, returnUnits[2]), 200)).toBeCloseTo(1000);
  });

  it('validates against the chosen unit max, decimals and base decimals', () => {
    expect(returnQtyError(163, returnUnits[0], 0, 'pc')).toBeNull();
    expect(returnQtyError(164, returnUnits[0], 0, 'pc')).toContain('At most 163');
    expect(returnQtyError(17, returnUnits[1], 0, 'pc')).toContain('At most 16');
    expect(returnQtyError(1.5, returnUnits[1], 0, 'pc')).toContain('whole numbers');
    expect(returnQtyError(null, returnUnits[1], 0, 'pc')).toBeNull();
  });

  it('returnAll uses the line unit when the remainder fits, else the base unit', () => {
    const fresh = returnUnits.map((u) => (u.productUnitId === 'fc' ? { ...u, maxQty: 2 } : u));
    expect(returnAllChoice({ returnableBaseQty: 200, returnUnits: fresh })).toEqual({ productUnitId: 'fc', qty: 2 });
    expect(returnAllChoice({ returnableBaseQty: 163, returnUnits })).toEqual({ productUnitId: 'pc', qty: 163 });
    expect(returnAllChoice({ returnableBaseQty: 0, returnUnits: [] })).toBeNull();
  });
});
