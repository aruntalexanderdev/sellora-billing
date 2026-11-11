import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { PurchaseForm } from './purchase-form';
import { PurchasesService } from '../purchases.service';
import { SuppliersService } from '../suppliers/suppliers.service';
import { ProductsService } from '../../inventory/products/products.service';
import { ExchangeRatesService } from '../../administration/exchange-rates/exchange-rates.service';
import { CashBankService } from '../../accounts/cash-bank/cash-bank.service';
import { AuthService } from '../../../core/auth/auth.service';
import { ContextService } from '../../../core/context/context.service';
import { StockLocationsService } from '../../../shared/locations/stock-locations.service';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import type { StockLocation } from '../../../shared/locations/locations.models';
import type { AccountOption } from '../../accounts/cash-bank/cash-bank.models';

const loc = (id: string, type: StockLocation['locationType'], name: string): StockLocation => ({
  id, code: id.toUpperCase(), name, locationType: type, branchId: 'b1', branchCode: 'MAIN', branchName: 'Main', isActive: true,
  canSell: type !== 'BranchWarehouse', canReceivePurchase: type !== 'Vehicle', isSystemManaged: false,
});
const STORE = loc('store', 'BranchStore', 'Main Store');
const WAREHOUSE = loc('wh', 'BranchWarehouse', 'Back Warehouse');
const VAN = loc('van', 'Vehicle', 'Van 1');

function setup(opts: { view?: boolean; locations?: StockLocation[]; create?: () => unknown; accounts?: AccountOption[] } = {}) {
  const create = vi.fn<(...args: unknown[]) => unknown>(opts.create ?? (() => of({ id: 'po-1', purchaseNo: 'PO-1' })));
  TestBed.configureTestingModule({
    imports: [PurchaseForm],
    providers: [
      provideRouter([]),
      { provide: PurchasesService, useValue: { create } },
      { provide: SuppliersService, useValue: { list: () => of({ items: [] }) } },
      { provide: ProductsService, useValue: { list: () => of({ items: [] }), get: () => of() } },
      { provide: ExchangeRatesService, useValue: { resolve: () => of() } },
      { provide: CashBankService, useValue: { options: () => of(opts.accounts ?? []) } },
      { provide: StockLocationsService, useValue: { list: () => of(opts.locations ?? []) } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => (opts.view ?? true) && c === 'StockLocations.View' } },
      { provide: ContextService, useValue: { contextVersion: signal(0).asReadonly(), workingBranch: signal({ id: 'b1' }) } },
      { provide: ToastService, useValue: { show: vi.fn() } },
    ],
  });
  vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(PurchaseForm);
  fixture.detectChanges();
  const c = fixture.componentInstance;
  const fill = () => {
    c.form.patchValue({ supplierId: 's1' });
    c.lines.at(0).patchValue({ productId: 'pen', productUnitId: 'pu', qty: 2, rate: 5 });
  };
  return { fixture, c, el: fixture.nativeElement as HTMLElement, create, fill };
}

const body = (create: { mock: { calls: unknown[][] } }) => create.mock.calls[0][0] as { stockLocationId: unknown };

describe('PurchaseForm receiving location', () => {
  it('shows the picker for StockLocations.View holders with more than one receiving location, listing only CanReceivePurchase', () => {
    const { el } = setup({ locations: [STORE, WAREHOUSE, VAN] });
    const names = Array.from(el.querySelectorAll('[data-testid="purchase-location"] option')).map((o) => o.textContent?.trim());
    expect(names).toEqual(['Main Store (STORE)', 'Back Warehouse (WH)']);
  });

  it.each([
    ['no StockLocations.View', { view: false, locations: [STORE, WAREHOUSE] }],
    ['a single eligible location', { view: true, locations: [STORE, VAN] }],
  ])('hides the picker and omits stockLocationId with %s', (_n, o) => {
    const { el, c, create, fill } = setup(o);
    expect(el.querySelector('[data-testid="purchase-location"] select')).toBeNull();
    fill();
    c.submit();
    expect(body(create).stockLocationId).toBeNull();
  });

  it('posts the chosen stockLocationId with an idempotency key', () => {
    const { c, create, fill } = setup({ locations: [STORE, WAREHOUSE] });
    expect(c.locationId()).toBe('store');
    c.locationId.set('wh');
    fill();
    c.submit();
    expect(body(create).stockLocationId).toBe('wh');
    expect(typeof create.mock.calls[0][1]).toBe('string');
  });

  it('maps a stockLocationId field error next to the picker', () => {
    const problem = { title: 'Validation failed', errors: { StockLocationId: ['This location cannot receive purchases.'] } };
    const { c, fixture, el, fill } = setup({ locations: [STORE, WAREHOUSE], create: () => throwError(() => new HttpErrorResponse({ status: 400, error: problem })) });
    fill();
    c.submit();
    fixture.detectChanges();
    expect(c.locationError()).toBe('This location cannot receive purchases.');
    expect(el.querySelector('[data-testid="purchase-location"] .field-error')?.textContent).toContain('cannot receive purchases');
  });

  it('offers company-level and working-branch accounts only', () => {
    const { c } = setup({
      accounts: [
        { id: 'co', name: 'Company', kind: 'Bank', branchId: null },
        { id: 'b1', name: 'Main', kind: 'Cash', branchId: 'b1' },
        { id: 'b2', name: 'North', kind: 'Cash', branchId: 'b2' },
      ],
    });
    expect(c.accountOptions().map((a) => a.id)).toEqual(['co', 'b1']);
  });
});
