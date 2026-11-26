import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../../core/auth/auth.service';
import type { TaxProfile, TaxResolution } from '../../../tax-setup/tax-setup.models';
import { TaxSetupService } from '../../../tax-setup/tax-setup.service';
import { ProductTaxSection, createProductTaxGroup, taxProblemPath, taxRequestFields } from './product-tax-section';

const ref = (id: string, name: string) => ({ id, code: id, name, regime: 'Gst' as const, treatment: 'Taxable' as const, isActive: true });
const profile = (id: string, name: string, isActive = true) => ({ id, name, code: id, isActive, countryId: 'in' }) as TaxProfile;

function resolution(over: Partial<TaxResolution> = {}): TaxResolution {
  return {
    resolved: true, countryId: 'in', salesProfile: ref('gst18', 'GST 18%'), purchaseProfile: ref('gst18', 'GST 18%'), purchaseSameAsSales: true,
    source: 'Subcategory', sourceName: 'Soft Drinks', hsnCode: '2202', hsnSource: 'Subcategory', hsnWarning: false, hsnWarningMessage: null, ...over,
  };
}

@Component({
  imports: [ProductTaxSection],
  template: `<app-product-tax-section [group]="group" [categoryId]="categoryId()" [subcategoryId]="subcategoryId()" [isEdit]="isEdit" [initialBasis]="initialBasis" />`,
})
class Host {
  group = createProductTaxGroup();
  categoryId = signal('');
  subcategoryId = signal('');
  isEdit = false;
  initialBasis: string | null = null;
}

function render(resolve = vi.fn(() => of(resolution())), host: Partial<Host> = {}, canViewTax = true) {
  TestBed.configureTestingModule({
    imports: [Host],
    providers: [
      provideRouter([]),
      { provide: TaxSetupService, useValue: { resolve, listProfiles: () => of([profile('gst5', 'GST 5%'), profile('gst18', 'GST 18%'), profile('old', 'Old 12%', false)]) } },
      { provide: AuthService, useValue: { hasPermission: () => canViewTax } },
    ],
  });
  const fixture = TestBed.createComponent(Host);
  Object.assign(fixture.componentInstance, host);
  fixture.detectChanges();
  return { fixture, host: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, resolve };
}
const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

describe('ProductTaxSection', () => {
  it('shows the resolved sales and purchase profile with its source', () => {
    const { el } = render();
    expect(q(el, 'tax-resolved')?.textContent).toContain('GST 18%');
    expect(q(el, 'tax-resolved')?.textContent).toContain('Same as sales');
    expect(q(el, 'tax-source')?.textContent?.trim()).toBe('From subcategory Soft Drinks');
  });

  it('without Tax.View shows a notice, hides the override and never calls resolve', () => {
    const { el, resolve } = render(undefined, {}, false);
    expect(q(el, 'tax-no-permission')?.textContent).toContain('Tax view permission');
    expect(q(el, 'override-toggle')).toBeNull();
    expect(q(el, 'tax-resolved')).toBeNull();
    expect(resolve).not.toHaveBeenCalled();
    expect(q(el, 'hsn-input')).not.toBeNull();
  });

  it('labels the company default source', () => {
    const { el } = render(vi.fn(() => of(resolution({ source: 'Company', sourceName: 'Company' }))));
    expect(q(el, 'tax-source')?.textContent?.trim()).toBe('From company default');
  });

  it('explains when no profile applies', () => {
    const { el } = render(vi.fn(() => of(resolution({ resolved: false, salesProfile: null, purchaseProfile: null, source: null }))));
    expect(q(el, 'tax-unresolved')?.textContent).toContain('No tax profile applies yet');
  });

  it('re-resolves with the new category and subcategory (new product preview)', () => {
    const { fixture, host, resolve } = render();
    host.categoryId.set('c1');
    host.subcategoryId.set('s1');
    fixture.detectChanges();
    expect(resolve).toHaveBeenLastCalledWith(expect.objectContaining({ categoryId: 'c1', subcategoryId: 's1' }));
  });

  it('shows the inherited HSN as placeholder and the warning from the resolution', () => {
    const { el } = render(vi.fn(() => of(resolution({ hsnWarning: true, hsnWarningMessage: 'Taxable GST products should have an HSN code.' }))));
    expect((q(el, 'hsn-input') as HTMLInputElement).placeholder).toBe('Inherited: 2202');
    expect(q(el, 'hsn-warning')?.textContent).toContain('should have an HSN');
  });

  it('hides the warning when the resolution has none', () => {
    const { el } = render();
    expect(q(el, 'hsn-warning')).toBeNull();
  });

  it('inherit mode sends no override ids; override mode previews and sends them', () => {
    const { fixture, host, el, resolve } = render();
    expect(q(el, 'override-fields')).toBeNull();
    expect(taxRequestFields(host.group)).toMatchObject({ salesTaxProfileId: null, purchaseTaxProfileId: null });

    host.group.controls.overrideEnabled.setValue(true);
    fixture.detectChanges();
    expect(q(el, 'override-fields')).not.toBeNull();
    host.group.controls.salesProfileId.setValue('gst5');
    fixture.detectChanges();
    expect(resolve).toHaveBeenLastCalledWith(expect.objectContaining({ salesProfileId: 'gst5', purchaseProfileId: null }));
    expect(taxRequestFields(host.group)).toMatchObject({ salesTaxProfileId: 'gst5', purchaseTaxProfileId: null });

    host.group.controls.purchaseSameAsSales.setValue(false);
    host.group.controls.purchaseProfileId.setValue('gst18');
    expect(taxRequestFields(host.group)).toMatchObject({ salesTaxProfileId: 'gst5', purchaseTaxProfileId: 'gst18' });
  });

  it('requires the profile once the override is on, and drops the override when turned off', () => {
    const { host } = render();
    host.group.controls.overrideEnabled.setValue(true);
    expect(host.group.controls.salesProfileId.invalid).toBe(true);
    host.group.controls.salesProfileId.setValue('gst5');
    host.group.controls.overrideEnabled.setValue(false);
    expect(host.group.valid).toBe(true);
    expect(taxRequestFields(host.group).salesTaxProfileId).toBeNull();
  });

  it('offers only active profiles plus the current selection', () => {
    const { host, fixture } = render();
    host.group.controls.overrideEnabled.setValue(true);
    host.group.controls.salesProfileId.setValue('old');
    fixture.detectChanges();
    const names = Array.from(fixture.nativeElement.querySelectorAll('#pt-sales option')).map((o) => (o as HTMLElement).textContent?.trim());
    expect(names).toEqual(['Select a profile…', 'GST 5%', 'GST 18%', 'Old 12%']);
  });

  it('does not preview an invalid HSN', () => {
    const { fixture, host, resolve } = render();
    host.group.controls.hsnCode.setValue('12');
    fixture.detectChanges();
    expect(resolve).toHaveBeenLastCalledWith(expect.objectContaining({ hsnCode: null }));
    host.group.controls.hsnCode.setValue('1006');
    fixture.detectChanges();
    expect(resolve).toHaveBeenLastCalledWith(expect.objectContaining({ hsnCode: '1006' }));
  });

  it('shows the HSN error', () => {
    const { fixture, host, el } = render();
    host.group.controls.hsnCode.setValue('12');
    fixture.detectChanges();
    expect(q(el, 'hsn-error')?.textContent).toContain('4, 6 or 8 digits');
  });

  it('warns that existing prices are not converted when an edited product changes basis (T16)', () => {
    const { fixture, host, el } = render(undefined, { isEdit: true, initialBasis: '' });
    expect(q(el, 'basis-warning')).toBeNull();
    host.group.controls.priceBasis.setValue('false');
    fixture.detectChanges();
    expect(q(el, 'basis-warning')?.textContent).toContain('not converted');
    host.group.controls.priceBasis.setValue('');
    fixture.detectChanges();
    expect(q(el, 'basis-warning')).toBeNull();
  });

  it('does not warn on a new product', () => {
    const { fixture, host, el } = render();
    host.group.controls.priceBasis.setValue('true');
    fixture.detectChanges();
    expect(q(el, 'basis-warning')).toBeNull();
    expect(taxRequestFields(host.group).priceIncludesTax).toBe(true);
  });
});

describe('tax request helpers', () => {
  it('maps server keys onto tax controls', () => {
    expect(taxProblemPath('HsnCode')).toBe('tax.hsnCode');
    expect(taxProblemPath('salesTaxProfileId')).toBe('tax.salesProfileId');
    expect(taxProblemPath('purchaseTaxProfileId')).toBe('tax.purchaseProfileId');
    expect(taxProblemPath('taxProfile')).toBe('tax');
    expect(taxProblemPath('Units[0].UnitId')).toBeNull();
  });
});
