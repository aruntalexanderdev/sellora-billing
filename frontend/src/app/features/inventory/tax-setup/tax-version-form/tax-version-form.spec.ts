import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { todayIso } from '../gst-version-rules';
import type { TaxComponent, TaxRegime, TaxTreatment } from '../tax-setup.models';
import { TaxVersionForm } from './tax-version-form';

const comp = (id: string, kind: TaxComponent['kind'], method: TaxComponent['method'] = 'Percent'): TaxComponent => ({
  id, countryId: 'in', countryCode: 'IN', code: id.toUpperCase(), name: id, kind, method, isActive: true, isUsed: false,
});
const GST = [comp('cgst', 'Cgst'), comp('sgst', 'Sgst'), comp('igst', 'Igst'), comp('cess', 'Cess')];

@Component({
  imports: [TaxVersionForm],
  template: `<app-tax-version-form [regime]="regime()" [treatment]="treatment()" [components]="components()" [latestFrom]="latestFrom()" />`,
})
class Host {
  regime = signal<TaxRegime>('Gst');
  treatment = signal<TaxTreatment>('Taxable');
  components = signal<TaxComponent[]>(GST);
  latestFrom = signal<string | null>(null);
}

function render() {
  TestBed.configureTestingModule({ imports: [Host] });
  const fixture = TestBed.createComponent(Host);
  fixture.detectChanges();
  const form = fixture.debugElement.children[0].componentInstance as TaxVersionForm;
  return { fixture, host: fixture.componentInstance, form, el: fixture.nativeElement as HTMLElement };
}
const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLInputElement | null;

describe('TaxVersionForm (add-version form)', () => {
  it('seeds CGST, SGST and IGST rows for a GST taxable profile', () => {
    const { el } = render();
    expect(el.querySelectorAll('[data-testid="version-row"]').length).toBe(3);
  });

  it('"Fill CGST / SGST / IGST" splits the total and builds a valid request with the fixed applicabilities', () => {
    const { fixture, form, el } = render();
    q(el, 'quick-total')!.value = '18';
    q(el, 'quick-total')!.dispatchEvent(new Event('input'));
    form.fillFromTotal();
    fixture.detectChanges();

    expect(form.build()).toEqual({
      effectiveFrom: todayIso(),
      components: [
        { taxComponentId: 'cgst', applicability: 'IntraState', ratePercent: 9, amountPerBaseUnit: null },
        { taxComponentId: 'sgst', applicability: 'IntraState', ratePercent: 9, amountPerBaseUnit: null },
        { taxComponentId: 'igst', applicability: 'InterState', ratePercent: 18, amountPerBaseUnit: null },
      ],
    });
    expect(q(el, 'version-total')?.textContent).toContain('18%');
  });

  it('shows the BR3 error and returns null when IGST does not equal CGST + SGST', () => {
    const { fixture, form, el } = render();
    form.rows.at(0).controls.ratePercent.setValue(9);
    form.rows.at(1).controls.ratePercent.setValue(9);
    form.rows.at(2).controls.ratePercent.setValue(17);
    expect(form.build()).toBeNull();
    fixture.detectChanges();
    expect(el.textContent).toContain('IGST must equal CGST plus SGST');
  });

  it('rejects a start date after today (T19) and one before the latest version', () => {
    const { fixture, host, form, el } = render();
    form.quickTotal.set('5');
    form.fillFromTotal();
    form.form.controls.effectiveFrom.setValue('2999-01-01');
    expect(form.build()).toBeNull();
    fixture.detectChanges();
    expect(q(el, 'version-from-error')?.textContent).toContain('cannot start after today');

    host.latestFrom.set('2026-06-01');
    fixture.detectChanges();
    form.form.controls.effectiveFrom.setValue('2026-01-01');
    expect(form.build()).toBeNull();
    expect(form.fieldErrors()['effectiveFrom']).toContain('on or after 2026-06-01');
  });

  it('a cess component given per unit sends the amount, not a rate', () => {
    const { host, fixture, form } = render();
    host.components.set([...GST.slice(0, 3), comp('cess', 'Cess', 'AmountPerBaseUnit')]);
    fixture.detectChanges();
    form.quickTotal.set('18');
    form.fillFromTotal();
    form.addRow('cess');
    form.rows.at(3).controls.amountPerBaseUnit.setValue(1.5);
    expect(form.build()?.components[3]).toEqual({ taxComponentId: 'cess', applicability: 'Always', ratePercent: null, amountPerBaseUnit: 1.5 });
  });

  it('a non-taxable treatment needs only the date', () => {
    const { host, fixture, form, el } = render();
    host.treatment.set('Exempt');
    fixture.detectChanges();
    expect(el.querySelectorAll('[data-testid="version-row"]').length).toBe(0);
    expect(form.build()).toEqual({ effectiveFrom: todayIso(), components: [] });
  });

  it('VAT seeds one VAT row', () => {
    const { host, fixture, el } = render();
    host.regime.set('Vat');
    host.components.set([comp('vat', 'Vat')]);
    fixture.detectChanges();
    expect(el.querySelectorAll('[data-testid="version-row"]').length).toBe(1);
  });

  it('shows server errors on the date and the rows', () => {
    const { fixture, form, el } = render();
    form.applyServerErrors({ errors: { EffectiveFrom: ['Used by documents.'], 'components[0].ratePercent': ['Too many decimals.'] } });
    fixture.detectChanges();
    expect(q(el, 'version-from-error')?.textContent).toContain('Used by documents.');
    expect(el.textContent).toContain('Too many decimals.');
  });
});
