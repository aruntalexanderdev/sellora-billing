import { FormControl } from '@angular/forms';
import { hsnOrNull, hsnValidator, isValidHsn } from './hsn';

describe('HSN validator', () => {
  it.each(['', '  ', '1006', '100630', '10063090'])('accepts %j', (v) => {
    expect(isValidHsn(v)).toBe(true);
    expect(hsnValidator(new FormControl(v))).toBeNull();
  });

  it.each(['100', '10063', '1006309', '100630901', 'ABCD', '10 06', '10.06'])('rejects %j', (v) => {
    expect(isValidHsn(v)).toBe(false);
    expect(hsnValidator(new FormControl(v))).toEqual({ hsn: 'The HSN/SAC code must be 4, 6 or 8 digits.' });
  });

  it('trims and maps empty to null', () => {
    expect(hsnOrNull(' 1006 ')).toBe('1006');
    expect(hsnOrNull('  ')).toBeNull();
    expect(hsnOrNull(null)).toBeNull();
  });
});
