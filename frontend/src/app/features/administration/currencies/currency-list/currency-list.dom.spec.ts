import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { CurrenciesService } from '../currencies.service';
import type { CurrencyItem } from '../currencies.models';
import { CurrencyList } from './currency-list';

const cur = (o: Partial<CurrencyItem> = {}): CurrencyItem => ({ id: 'k1', code: 'USD', name: 'US Dollar', symbol: '$', decimalPrecision: 2, isActive: true, isBase: false, ...o });

function mount(list: () => Observable<CurrencyItem[]>) {
  const service = { list: vi.fn(list), create: vi.fn(), update: vi.fn(), setActive: vi.fn() };
  TestBed.configureTestingModule({ imports: [CurrencyList], providers: [{ provide: CurrenciesService, useValue: service }] });
  const fixture = TestBed.createComponent(CurrencyList);
  fixture.detectChanges();
  return { fixture, el: fixture.nativeElement as HTMLElement, service };
}

describe('Company currency list is read-only (D12/O7)', () => {
  it('renders code, name, symbol, decimals and status (Base / Active / Inactive) without any action', () => {
    const { el } = mount(() => of([cur({ code: 'INR', name: 'Rupee', symbol: '₹', isBase: true }), cur(), cur({ id: 'k3', code: 'EUR', name: 'Euro', symbol: '€', isActive: false })]));
    const rows = Array.from(el.querySelectorAll('tbody tr')).map((r) => r.textContent ?? '');
    expect(rows[0]).toContain('Base');
    expect(rows[1]).toContain('Active');
    expect(rows[2]).toContain('Inactive');
    expect(el.querySelectorAll('button')).toHaveLength(0);
    expect(el.textContent).not.toMatch(/\+ New|Edit|Deactivate|Activate/);
    expect(el.textContent).toContain('managed by the platform administrator');
  });

  it('empty state has no add action', () => {
    const { el } = mount(() => of([]));
    expect(el.querySelector('.dt-state-title')?.textContent).toContain('No currencies yet');
    expect(el.querySelector('.dt-primary-btn')).toBeNull();
  });

  it('error state offers Retry which reloads', () => {
    const { fixture, el, service } = mount(() => throwError(() => new Error('x')));
    expect(el.querySelector('.dt-state-title')?.textContent).toContain('Could not load this list');
    service.list.mockImplementation(() => of([cur()]));
    el.querySelector<HTMLButtonElement>('.dt-primary-btn')!.click();
    fixture.detectChanges();
    expect(el.querySelector('tbody')?.textContent).toContain('US Dollar');
  });

  it('shows the loading skeleton while pending', () => {
    expect(mount(() => new Observable<CurrencyItem[]>(() => undefined)).el.querySelector('.dt-loading')).not.toBeNull();
  });
});
