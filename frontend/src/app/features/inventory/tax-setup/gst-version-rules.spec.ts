import {
  defaultApplicability, splitGstRate, todayIso, totalPercent, validateEffectiveFrom, validateVersionLines, type VersionLineDraft,
} from './gst-version-rules';

const line = (kind: VersionLineDraft['kind'], rate: number | null, extra: Partial<VersionLineDraft> = {}): VersionLineDraft => ({
  code: kind.toUpperCase(), kind, method: 'Percent', applicability: defaultApplicability(kind), ratePercent: rate, amountPerBaseUnit: null, ...extra,
});
const gst18 = () => [line('Cgst', 9), line('Sgst', 9), line('Igst', 18)];

describe('gst-version-rules', () => {
  it('maps component kinds to their only valid applicability', () => {
    expect(['Cgst', 'Sgst', 'Utgst'].map((k) => defaultApplicability(k as never))).toEqual(['IntraState', 'IntraState', 'IntraState']);
    expect(defaultApplicability('Igst')).toBe('InterState');
    expect(['Cess', 'Vat', 'Other'].map((k) => defaultApplicability(k as never))).toEqual(['Always', 'Always', 'Always']);
  });

  it('accepts a complete GST set and sums intra-state percent lines only', () => {
    expect(validateVersionLines('Gst', 'Taxable', gst18())).toEqual([]);
    expect(totalPercent(gst18())).toBe(18);
  });

  it('accepts UTGST in place of SGST and a cess line', () => {
    const lines = [line('Cgst', 2.5), line('Utgst', 2.5), line('Igst', 5), line('Cess', 12, { code: 'CESS' })];
    expect(validateVersionLines('Gst', 'Taxable', lines)).toEqual([]);
    expect(totalPercent(lines)).toBe(17);
  });

  it('accepts a cess given per base unit', () => {
    const cess = line('Cess', null, { method: 'AmountPerBaseUnit', amountPerBaseUnit: 1.5 });
    expect(validateVersionLines('Gst', 'Taxable', [...gst18(), cess])).toEqual([]);
  });

  it('requires one CGST and one SGST/UTGST', () => {
    expect(validateVersionLines('Gst', 'Taxable', [line('Cgst', 9), line('Igst', 9)])[0]).toMatch(/exactly one CGST and one SGST/);
  });

  it('requires CGST = SGST', () => {
    const errors = validateVersionLines('Gst', 'Taxable', [line('Cgst', 9), line('Sgst', 6), line('Igst', 15)]);
    expect(errors).toContain('CGST and SGST (or UTGST) must have the same rate.');
  });

  it('requires one IGST equal to CGST + SGST', () => {
    expect(validateVersionLines('Gst', 'Taxable', [line('Cgst', 9), line('Sgst', 9)])).toContain('An inter-state set needs exactly one IGST component.');
    expect(validateVersionLines('Gst', 'Taxable', [line('Cgst', 9), line('Sgst', 9), line('Igst', 17)])).toContain('IGST must equal CGST plus SGST (or UTGST).');
  });

  it('rejects a zero total ("never 0% taxable")', () => {
    const errors = validateVersionLines('Gst', 'Taxable', [line('Cgst', 0), line('Sgst', 0), line('Igst', 0)]);
    expect(errors.join(' ')).toMatch(/rate above zero/);
  });

  it('rejects missing rates, rates above 100 and too many decimals', () => {
    expect(validateVersionLines('Gst', 'Taxable', [line('Cgst', null), line('Sgst', 9), line('Igst', 18)])[0]).toMatch(/enter the rate/);
    expect(validateVersionLines('Vat', 'Taxable', [line('Vat', 101)])[0]).toMatch(/between 0 and 100/);
    expect(validateVersionLines('Vat', 'Taxable', [line('Vat', 5.12345)])[0]).toMatch(/4 decimal places/);
  });

  it('VAT taxable needs exactly one VAT line', () => {
    expect(validateVersionLines('Vat', 'Taxable', [line('Vat', 15)])).toEqual([]);
    expect(validateVersionLines('Vat', 'Taxable', [line('Vat', 15), line('Vat', 5)])).toContain('A taxable VAT profile needs exactly one VAT component.');
    expect(validateVersionLines('Vat', 'Taxable', [line('Cess', 5)])).toContain('A taxable VAT profile needs exactly one VAT component.');
  });

  it('non-taxable treatments carry no components; taxable needs some', () => {
    expect(validateVersionLines('Gst', 'NilRated', [])).toEqual([]);
    expect(validateVersionLines('Gst', 'Exempt', gst18())[0]).toMatch(/carries no tax components/);
    expect(validateVersionLines('Gst', 'Taxable', [])[0]).toMatch(/at least one/);
  });

  it('splits a total GST rate into halves and IGST', () => {
    expect(splitGstRate(18)).toEqual({ half: 9, igst: 18 });
    expect(splitGstRate(5)).toEqual({ half: 2.5, igst: 5 });
  });

  describe('effective from (T19)', () => {
    it('cannot be after today', () => {
      expect(validateEffectiveFrom('2026-11-25', '2026-11-24')).toMatch(/cannot start after today/);
      expect(validateEffectiveFrom('2026-11-24', '2026-11-24')).toBeNull();
    });
    it('is required and not before the latest version', () => {
      expect(validateEffectiveFrom('', '2026-11-24')).toMatch(/Choose the date/);
      expect(validateEffectiveFrom('2026-01-01', '2026-11-24', '2026-06-01')).toMatch(/on or after 2026-06-01/);
      expect(validateEffectiveFrom('2026-06-01', '2026-11-24', '2026-06-01')).toBeNull();
    });
    it('formats today as a local ISO date', () => {
      expect(todayIso(new Date(2026, 0, 5))).toBe('2026-01-05');
    });
  });
});
