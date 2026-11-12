import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { DashboardPage } from './dashboard-page';
import { DashboardService } from '../dashboard.service';
import { AuthService } from '../../../core/auth/auth.service';
import type { Dashboard } from '../dashboard.models';

function mount(lowStockItems: NonNullable<Dashboard['lowStockItems']>) {
  const dash: Dashboard = {
    todaysSales: null, todaysPurchases: null, currentStockValue: null, outstandingCustomerDue: null,
    lowStockItems, recentSales: null, recentPurchases: null,
  };
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: DashboardService, useValue: { get: () => of(dash) } },
      { provide: AuthService, useValue: { hasPermission: () => false } },
    ],
  });
  const f = TestBed.createComponent(DashboardPage);
  f.detectChanges();
  return f.nativeElement as HTMLElement;
}

describe('DashboardPage low stock', () => {
  it('formats available and minimum qty with the base decimal places', () => {
    const el = mount([
      { productId: 'r', name: 'Rice', baseUnitLabel: 'kg', baseDecimalPlaces: 3, quantityOnHand: 2, reorderLevel: 5, status: 'Low' },
      { productId: 'p', name: 'Pen', baseUnitLabel: 'pc', baseDecimalPlaces: 0, quantityOnHand: 3, reorderLevel: 10, status: 'Low' },
    ]);
    const rows = Array.from(el.querySelectorAll('tr.dash-row-link')).map((r) => r.textContent?.replace(/\s+/g, ' ').trim() ?? '');
    expect(rows[0]).toContain('2.000 kg');
    expect(rows[0]).toContain('5.000 kg');
    expect(rows[1]).toContain('3 pc');
    expect(rows[1]).toContain('10 pc');
    expect(rows[1]).not.toContain('3.000');
  });

  it('does not hide stored precision when a 0 dp product carries a fraction', () => {
    const el = mount([{ productId: 'p', name: 'Pen', baseUnitLabel: 'pc', baseDecimalPlaces: 0, quantityOnHand: 2.5, reorderLevel: 10, status: 'Low' }]);
    expect(el.querySelector('tr.dash-row-link')?.textContent).toContain('2.5 pc');
  });
});
