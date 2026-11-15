import { FormControl, FormGroup } from '@angular/forms';
import { discountExceedsGross, discountFromPercent, discountWithinGross, hasTooManyDecimals, isValidPercent, lineAmounts, roundMoney } from './money';

describe('roundMoney', () => {
  it('rounds decimal midpoints away from zero despite binary drift', () => {
    expect(roundMoney(1.005)).toBe(1.01);
    expect(roundMoney(2.675)).toBe(2.68);
    expect(roundMoney(1.015)).toBe(1.02);
    expect(roundMoney(0.125)).toBe(0.13);
    expect(roundMoney(1.004)).toBe(1);
  });
  it('rounds negatives away from zero', () => {
    expect(roundMoney(-1.005)).toBe(-1.01);
    expect(roundMoney(-2.675)).toBe(-2.68);
    expect(roundMoney(-0.004)).toBe(0);
    expect(Object.is(roundMoney(-0.004), 0)).toBe(true);
  });
  it('honours the decimals argument and non-finite input', () => {
    expect(roundMoney(2.5, 0)).toBe(3);
    expect(roundMoney(-2.5, 0)).toBe(-3);
    expect(roundMoney(1.0005, 3)).toBe(1.001);
    expect(roundMoney(Number.NaN)).toBe(0);
  });
});

describe('lineAmounts', () => {
  it('rounds taxable, tax and total per line', () => {
    // 3 x 3.335 = 10.005 -> 10.01; tax 5% of 10.01 = 0.5005 -> 0.5; total 10.51
    expect(lineAmounts(3, 3.335, 0, 5)).toEqual({ gross: 10.01, taxable: 10.01, tax: 0.5, total: 10.51 });
  });
});

describe('discount bound', () => {
  it('flags discount above qty x price', () => {
    expect(discountExceedsGross(2, 10, 20)).toBe(false);
    expect(discountExceedsGross(2, 10, 20.01)).toBe(true);
    expect(discountExceedsGross(0, 10, 1)).toBe(true);
  });
  it('group validator reports discountExceedsGross', () => {
    const g = (discount: number) => new FormGroup({ qty: new FormControl(2), unitPrice: new FormControl(10), discount: new FormControl(discount) });
    expect(discountWithinGross(g(20))).toBeNull();
    expect(discountWithinGross(g(25))).toEqual({ discountExceedsGross: true });
    expect(discountWithinGross(g(1.005))).toEqual({ discountDecimals: true });
  });
  it('compares against the unrounded gross, like the server', () => {
    // 3 x 3.335 = 10.005: a 10.01 discount is above it although the rounded gross is 10.01.
    expect(discountExceedsGross(3, 3.335, 10.01)).toBe(true);
    expect(discountExceedsGross(3, 3.335, 10)).toBe(false);
  });
  it('flags discounts with more decimals than the currency', () => {
    expect(hasTooManyDecimals(1.005)).toBe(true);
    expect(hasTooManyDecimals(12.34567)).toBe(true);
    expect(hasTooManyDecimals(1.01)).toBe(false);
    expect(hasTooManyDecimals(0.1 + 0.2)).toBe(false); // float noise is not "more decimals"
    expect(hasTooManyDecimals(null)).toBe(false);
  });
});

describe('percent discount', () => {
  it('converts a percentage to a rounded amount', () => {
    expect(discountFromPercent(3, 3.33, 10)).toBe(1);
    expect(discountFromPercent(1, 10.05, 50)).toBe(5.03);
    expect(discountFromPercent(2, 10, 100)).toBe(20);
    expect(discountFromPercent(2, 10, 0)).toBe(0);
  });
  it('validates range and never exceeds gross', () => {
    expect(isValidPercent(-1)).toBe(false);
    expect(isValidPercent(100.5)).toBe(false);
    expect(isValidPercent(null)).toBe(false);
    expect(isValidPercent(0)).toBe(true);
    expect(discountFromPercent(2, 10, 150)).toBe(0);
  });
});
