import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import type { TaxComponent } from '../tax-setup.models';
import { TaxSetupService } from '../tax-setup.service';
import { ComponentsTab } from './components-tab';

const c = (over: Partial<TaxComponent>): TaxComponent => ({ id: 'c1', countryId: 'in', countryCode: 'IN', code: 'CGST', name: 'Central GST', kind: 'Cgst', method: 'Percent', isActive: true, isUsed: false, ...over });

function render(items: TaxComponent[], canManage = true) {
  const service = {
    listComponents: vi.fn(() => of(items)),
    createComponent: vi.fn(() => of(items[0])),
    updateComponent: vi.fn(() => of(items[0])),
    setComponentActive: vi.fn(() => of(items[0])),
  };
  TestBed.configureTestingModule({ imports: [ComponentsTab], providers: [{ provide: TaxSetupService, useValue: service }, { provide: ToastService, useValue: { show: vi.fn() } }] });
  const fixture = TestBed.createComponent(ComponentsTab);
  fixture.componentRef.setInput('countries', [{ id: 'in', code: 'IN', name: 'India' }]);
  fixture.componentRef.setInput('canManage', canManage);
  fixture.detectChanges();
  return { fixture, el: fixture.nativeElement as HTMLElement, service, cmp: fixture.componentInstance };
}

describe('ComponentsTab', () => {
  it('lists components', () => {
    const { el } = render([c({}), c({ id: 'c2', code: 'CESS', kind: 'Cess', isActive: false })]);
    expect(el.querySelectorAll('[data-testid="component-row"]').length).toBe(2);
  });

  it('locks kind and method once a component is used', () => {
    const { fixture, cmp, el } = render([c({ isUsed: true })]);
    cmp.openEditor(c({ isUsed: true }));
    fixture.detectChanges();
    expect(cmp.form.controls.kind.disabled).toBe(true);
    expect(cmp.form.controls.method.disabled).toBe(true);
    expect(el.querySelector('[data-testid="locked-hint"]')).not.toBeNull();
  });

  it('allows changing kind and method while unused, and creates with the country', () => {
    const { fixture, cmp, service } = render([c({})]);
    cmp.openEditor(null);
    fixture.detectChanges();
    expect(cmp.form.controls.kind.enabled).toBe(true);
    cmp.form.patchValue({ code: 'vat', name: 'VAT', kind: 'Vat' });
    cmp.submit();
    expect(service.createComponent).toHaveBeenCalledWith({ countryId: 'in', code: 'VAT', name: 'VAT', kind: 'Vat', method: 'Percent' });
  });

  it('hides write actions without Tax.Manage', () => {
    const { el } = render([c({})], false);
    expect(el.textContent).not.toContain('New component');
    expect(el.textContent).not.toContain('Deactivate');
  });
});
