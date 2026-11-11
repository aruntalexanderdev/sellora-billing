import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { UnitList } from './unit-list';
import { UnitsService } from '../units.service';
import type { UnitItem } from '../units.models';

const unit = (o: Partial<UnitItem> = {}): UnitItem => ({
  id: 'u1', name: 'Kilogram', shortCode: 'kg', measurementCategory: 'Weight', decimalPlaces: 3, standardFactor: 1, isActive: true, isInUse: true, canDeactivate: false, ...o,
});

function mount(list: () => Observable<UnitItem[]>) {
  const service = { list: vi.fn(list), create: vi.fn(), update: vi.fn(), setActive: vi.fn() };
  TestBed.configureTestingModule({ imports: [UnitList], providers: [{ provide: UnitsService, useValue: service }] });
  const fixture = TestBed.createComponent(UnitList);
  fixture.detectChanges();
  return { fixture, el: fixture.nativeElement as HTMLElement, service };
}

describe('Company unit list is read-only (D12/O7)', () => {
  it('renders the units with category, decimals, factor and status but no create, edit or status action', () => {
    const { el, service } = mount(() => of([unit(), unit({ id: 'u2', name: 'Piece', shortCode: 'pc', measurementCategory: 'Count', decimalPlaces: 0, standardFactor: null, isActive: false, canDeactivate: true })]));
    const rows = Array.from(el.querySelectorAll('tbody tr')).map((r) => r.textContent ?? '');
    expect(rows[0]).toContain('Kilogram');
    expect(rows[0]).toContain('1 kg');
    expect(rows[0]).toContain('Active');
    expect(rows[1]).toContain('Inactive');
    expect(el.querySelectorAll('button')).toHaveLength(0);
    expect(el.textContent).not.toMatch(/\+ New|Edit|Deactivate|Activate/);
    expect(el.textContent).toContain('Managed by the platform administrator');
    expect(service.create).not.toHaveBeenCalled();
  });

  it('shows the empty state pointing at the platform administrator, with no add action', () => {
    const { el } = mount(() => of([]));
    expect(el.querySelector('.dt-state-title')?.textContent).toContain('No units yet');
    expect(el.querySelector('.dt-state-body')?.textContent).toContain('platform administrator');
    expect(el.querySelector('.dt-primary-btn')).toBeNull();
  });

  it('shows the error state and Retry reloads', () => {
    const { fixture, el, service } = mount(() => throwError(() => new Error('x')));
    expect(el.querySelector('.dt-state-title')?.textContent).toContain('Could not load this list');
    service.list.mockImplementation(() => of([unit()]));
    el.querySelector<HTMLButtonElement>('.dt-primary-btn')!.click();
    fixture.detectChanges();
    expect(el.querySelector('tbody')?.textContent).toContain('Kilogram');
  });

  it('shows the loading skeleton while pending', () => {
    expect(mount(() => new Observable<UnitItem[]>(() => undefined)).el.querySelector('.dt-loading')).not.toBeNull();
  });
});
