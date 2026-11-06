import { describe, expect, it } from 'vitest';
import { defaultReturnUnit, type ReturnUnit } from './return-units';

const u = (id: string, isLineUnit: boolean): ReturnUnit => ({
  productUnitId: id, unitCode: id, displayName: id, decimalPlaces: 0, conversionFactorToBase: 1, isLineUnit, maxQty: 1,
});

describe('defaultReturnUnit', () => {
  it('prefers the line unit, then the first unit, else undefined', () => {
    expect(defaultReturnUnit([u('pc', false), u('box', true)])?.productUnitId).toBe('box');
    expect(defaultReturnUnit([u('pc', false), u('box', false)])?.productUnitId).toBe('pc');
    expect(defaultReturnUnit([])).toBeUndefined();
  });
});
