import { FormControl } from '@angular/forms';
import { describe, expect, it } from 'vitest';
import { maxDecimalPlaces, minQuantity, quantityStep, quantityValidators } from './quantity-input';

describe('quantity-input', () => {
  it('derives the step from decimal places', () => {
    expect(quantityStep(0)).toBe('1');
    expect(quantityStep(1)).toBe('0.1');
    expect(quantityStep(2)).toBe('0.01');
    expect(quantityStep(3)).toBe('0.001');
    expect(quantityStep(null)).toBe('1');
    expect(quantityStep(7)).toBe('0.001');
  });

  it('derives the minimum quantity', () => {
    expect(minQuantity(0)).toBe(1);
    expect(minQuantity(3)).toBe(0.001);
  });

  it('rejects too many decimals', () => {
    expect(maxDecimalPlaces(0)(new FormControl(2))).toBeNull();
    expect(maxDecimalPlaces(0)(new FormControl(2.5))).toEqual({ maxDecimalPlaces: { max: 0, actual: 1 } });
    expect(maxDecimalPlaces(3)(new FormControl(1.25))).toBeNull();
    expect(maxDecimalPlaces(3)(new FormControl(1.2345))).toEqual({ maxDecimalPlaces: { max: 3, actual: 4 } });
    expect(maxDecimalPlaces(2)(new FormControl(1e-7))).toEqual({ maxDecimalPlaces: { max: 2, actual: 7 } });
    expect(maxDecimalPlaces(0)(new FormControl(null))).toBeNull();
  });

  it('combines required, min and decimals', () => {
    const control = new FormControl<number | null>(null, quantityValidators(0));
    expect(control.hasError('required')).toBe(true);
    control.setValue(0.5);
    expect(control.hasError('min')).toBe(true);
    expect(control.hasError('maxDecimalPlaces')).toBe(true);
    control.setValue(3);
    expect(control.valid).toBe(true);
  });
});
