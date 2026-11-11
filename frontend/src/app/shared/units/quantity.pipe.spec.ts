import { QuantityPipe } from './quantity.pipe';

describe('QuantityPipe', () => {
  const pipe = new QuantityPipe();

  it('transform_nullOrUndefined_returnsDash', () => {
    expect(pipe.transform(null, 0)).toBe('—');
    expect(pipe.transform(undefined, 3)).toBe('—');
  });

  it('transform_zeroDp_rendersInteger', () => {
    expect(pipe.transform(300, 0)).toBe('300');
    expect(pipe.transform(0, 0)).toBe('0');
  });

  it('transform_threeDp_padsDecimals', () => {
    expect(pipe.transform(12.5, 3)).toBe('12.500');
    expect(pipe.transform(12, 3)).toBe('12.000');
  });

  it('transform_fractionalWithZeroDp_widensInsteadOfHidingPrecision', () => {
    expect(pipe.transform(2.5, 0)).toBe('2.5');
    expect(pipe.transform(2.125, 0)).toBe('2.125');
  });

  it('transform_nullDp_treatedAsZero', () => {
    expect(pipe.transform(7, null)).toBe('7');
    expect(pipe.transform(7, undefined)).toBe('7');
  });

  it('transform_floatNoise_isStripped', () => {
    expect(pipe.transform(0.1 + 0.2, 3)).toBe('0.300');
  });

  it('transform_negativeZero_isNotShownAsMinusZero', () => {
    expect(pipe.transform(-0, 0)).toBe('0');
  });
});
