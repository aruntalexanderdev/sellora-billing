import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../../shared/scope/scope-testing';
import { ShipmentsService } from '../shipments.service';
import { shipment } from '../shipment-fixtures';
import { ShipmentForm } from './shipment-form';

function setup(existing: ReturnType<typeof shipment> | null = null) {
  const create = vi.fn(() => of(shipment()));
  const update = vi.fn(() => of(shipment()));
  TestBed.configureTestingModule({
    imports: [ShipmentForm],
    providers: [
      provideRouter([]),
      ...provideScopeStubs(),
      { provide: ShipmentsService, useValue: { create, update } },
      { provide: AuthService, useValue: { hasPermission: () => true } },
    ],
  });
  vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(ShipmentForm);
  if (existing) fixture.componentRef.setInput('shipment', existing);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const set = (id: string, value: string) => {
    const e = el.querySelector(`[data-testid="${id}"]`) as HTMLInputElement | HTMLSelectElement;
    e.value = value;
    e.dispatchEvent(new Event(e instanceof HTMLSelectElement ? 'change' : 'input'));
    fixture.detectChanges();
  };
  const save = () => {
    (el.querySelector('[data-testid="save-header"] button') as HTMLButtonElement).click();
    fixture.detectChanges();
  };
  return { fixture, component: fixture.componentInstance, el, set, save, create, update };
}

describe('ShipmentForm', () => {
  it('relabels the reference per mode', () => {
    const { el, set } = setup();
    const label = () => el.querySelector('[data-testid="ref-label"]')?.textContent?.trim();
    expect(label()).toContain('optional');
    set('mode', 'Air');
    expect(label()).toBe('AWB number');
    set('mode', 'Sea');
    expect(label()).toBe('Bill of lading');
    set('mode', 'Rail');
    expect(label()).toBe('Railway receipt');
    set('mode', 'Courier');
    expect(label()).toBe('Tracking number');
    set('mode', 'HandCarry');
    expect(label()).toBe('Carrier name');
  });

  it('blocks a non-road shipment without its reference, and an external one without a provider', () => {
    const { component, el, set, save, create } = setup();
    component.form.controls.sourceLocationId.setValue('store');
    set('mode', 'Air');
    save();
    expect(create).not.toHaveBeenCalled();
    expect(el.querySelector('[data-testid="reference-error"]')?.textContent).toContain('AWB');
    set('reference', 'AWB-1');
    set('via', 'ExternalProvider');
    save();
    expect(create).not.toHaveBeenCalled();
    expect(el.querySelector('[data-testid="provider-error"]')).not.toBeNull();
    set('provider-name', 'DHL');
    save();
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ sourceLocationId: 'store', transportMode: 'Air', transportVia: 'ExternalProvider', providerName: 'DHL', transportReference: 'AWB-1' }), expect.any(String));
  });

  it('road shipments need no reference; edit mode keeps the source and uses update', () => {
    const { component, update, save, el } = setup(shipment('Draft', { transportMode: 'Road', transportReference: null }));
    expect(el.querySelector('[data-testid="source-select"]')).toBeNull();
    save();
    expect(update).toHaveBeenCalledWith('s1', expect.objectContaining({ transportMode: 'Road', transportReference: null }));
    expect(component.editing()).toBe(true);
  });
});
