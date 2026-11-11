import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { PurchaseReturnForm } from './purchase-return-form';
import { PurchasesService } from '../../purchases.service';
import { CashBankService } from '../../../accounts/cash-bank/cash-bank.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import type { PurchaseDetail } from '../../purchases.models';

const unit = { productUnitId: 'pu-pc', unitCode: 'pc', displayName: 'pc', decimalPlaces: 0, conversionFactorToBase: 1, isLineUnit: true, maxQty: 10 };
const bal = (id: string, code: string, baseQty: number) => ({ stockLocationId: id, code, name: code + ' name', baseQty });

function purchase(returnable: number): PurchaseDetail {
  return {
    id: 'po1', purchaseNo: 'PO-1', supplierName: 'Acme', purchaseDate: '2026-11-01', paymentMethod: 'Cash', grandTotal: 100,
    originalGrandTotal: 100, originalPaidAmount: 100, originalNetTotal: 100, currencyDecimalPrecision: 2, baseCurrencyDecimalPrecision: 2,
    lines: [{
      id: 'l1', productName: 'Pen', qty: 10, unitCode: 'pc', unitDecimalPlaces: 0, baseQty: 10, baseUnitLabel: 'pc', baseUnitDecimalPlaces: 0,
      lineTotal: 100, returnedBaseQty: 0, returnableBaseQty: returnable, returnUnits: [{ ...unit, maxQty: returnable }],
      locationBalances: [bal('store', 'STORE', 6), bal('van', 'VAN', 4), bal('empty', 'EMP', 0)],
    }],
  } as unknown as PurchaseDetail;
}

function setup(opts: { view?: boolean; create?: () => unknown } = {}) {
  const get = vi.fn((_id: string, loc?: string | null) => of(purchase(loc === 'van' ? 4 : 10)));
  const createReturn = vi.fn<(...args: unknown[]) => unknown>(opts.create ?? (() => of({ id: 'r1', returnNo: 'PR-1' })));
  TestBed.configureTestingModule({
    imports: [PurchaseReturnForm],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'po1' }) } } },
      { provide: PurchasesService, useValue: { get, createReturn } },
      { provide: CashBankService, useValue: { options: () => of([]) } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => (opts.view ?? true) && c === 'StockLocations.View' } },
      { provide: ToastService, useValue: { show: vi.fn() } },
    ],
  });
  vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(PurchaseReturnForm);
  fixture.detectChanges();
  return { fixture, c: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, get, createReturn };
}

describe('PurchaseReturnForm location', () => {
  it('feeds the picker from locationBalances (only locations holding stock) for StockLocations.View holders', () => {
    const { c, el } = setup();
    expect(c.locationOptions().map((o) => o.id)).toEqual(['store', 'van']);
    const names = Array.from(el.querySelectorAll('[data-testid="return-location"] option')).map((o) => o.textContent?.trim());
    expect(names).toEqual(['Default location', 'STORE name (STORE)', 'VAN name (VAN)']);
  });

  it('hides the picker without StockLocations.View', () => {
    const { el } = setup({ view: false });
    expect(el.querySelector('[data-testid="return-location"]')).toBeNull();
  });

  it('reloads the purchase at the chosen location and clears entered quantities', () => {
    const { c, get } = setup();
    c.setQty('l1', '3');
    c.onLocationChange('van');
    expect(get).toHaveBeenLastCalledWith('po1', 'van');
    expect(c.lines()[0].returnableBaseQty).toBe(4);
    expect(c.qty()).toEqual({});
  });

  it('posts stockLocationId and an Idempotency-Key argument', () => {
    const { c, createReturn } = setup();
    c.onLocationChange('van');
    c.setQty('l1', '2');
    c.submit();
    const [req, key] = createReturn.mock.calls[0] as [{ stockLocationId: string }, string];
    expect(req.stockLocationId).toBe('van');
    expect(typeof key).toBe('string');
    expect(key.length).toBeGreaterThan(8);
  });

  it('omits stockLocationId (null) when no location was chosen', () => {
    const { c, createReturn } = setup();
    c.setQty('l1', '2');
    c.submit();
    expect((createReturn.mock.calls[0][0] as { stockLocationId: unknown }).stockLocationId).toBeNull();
  });

  it('maps a stockLocationId field error next to the picker', () => {
    const problem = { title: 'Validation failed', errors: { StockLocationId: ['Not enough stock at that location.'] } };
    const { c, fixture, el } = setup({ create: () => throwError(() => new HttpErrorResponse({ status: 400, error: problem })) });
    c.setQty('l1', '2');
    c.submit();
    fixture.detectChanges();
    expect(c.locationError()).toBe('Not enough stock at that location.');
    expect(el.querySelector('[data-testid="return-location"] .field-error')?.textContent).toContain('Not enough stock');
  });
});
