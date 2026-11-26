import { TestBed } from '@angular/core/testing';
import { LineTaxBreakdown } from './line-tax-breakdown';
import type { TaxRowView } from '../tax-preview.models';

const base = { componentId: null, amountPerBaseUnit: null, baseAmount: 1000 };
function render(rows: TaxRowView[] | null, label = 'pc') {
  const fixture = TestBed.createComponent(LineTaxBreakdown);
  fixture.componentRef.setInput('rows', rows);
  fixture.componentRef.setInput('symbol', '₹');
  fixture.componentRef.setInput('baseUnitLabel', label);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const cells = Array.from(el.querySelectorAll('tbody tr')).map((tr) => Array.from(tr.querySelectorAll('td')).map((td) => td.textContent!.trim()));
  return { el, cells, text: el.textContent!.replace(/\s+/g, ' ') };
}

describe('LineTaxBreakdown', () => {
  it('shows code, rate, base and tax for each row and a total', () => {
    const { el, cells, text } = render([
      { ...base, code: 'CGST', kind: 'Cgst', method: 'Percent', ratePercent: 9, taxAmount: 90 },
      { ...base, code: 'SGST', kind: 'Sgst', method: 'Percent', ratePercent: 9, taxAmount: 90 },
    ]);
    expect(el.querySelectorAll('tbody tr')).toHaveLength(2);
    expect(cells[0]).toEqual(['CGST', '9%', '₹1,000.00', '₹90.00']);
    expect(text).toContain('Total tax');
    expect(el.querySelector('tfoot')?.textContent).toContain('₹180.00');
  });

  it('shows an amount-per-unit row with its quantity base', () => {
    const { cells } = render([{ componentId: 'c', code: 'CESS', kind: 'Cess', method: 'AmountPerBaseUnit', ratePercent: null, amountPerBaseUnit: 2.5, baseAmount: 40, taxAmount: 100 }]);
    expect(cells[0]).toEqual(['CESS', '₹2.50 / pc', '40 pc', '₹100.00']);
  });

  it('shows legacy rows as one "Tax" row', () => {
    const { el, cells, text } = render([
      { ...base, code: 'GST-18', kind: 'Other', method: 'Percent', ratePercent: 18, taxAmount: 180, isLegacy: true },
    ]);
    expect(el.querySelectorAll('tbody tr')).toHaveLength(1);
    expect(cells[0]).toEqual(['Tax', '18%', '₹1,000.00', '₹180.00']);
    expect(text).not.toContain('GST-18');
  });

  it('says so when there are no rows', () => {
    expect(render([]).text).toContain('No tax on this line.');
  });
});
