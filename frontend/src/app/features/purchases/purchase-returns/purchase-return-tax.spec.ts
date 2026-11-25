import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { PurchaseReturnForm } from './purchase-return-form/purchase-return-form';
import { PurchaseReturnDetail } from './purchase-return-detail/purchase-return-detail';
import { PurchasesService } from '../purchases.service';
import { CashBankService } from '../../accounts/cash-bank/cash-bank.service';
import { AuthService } from '../../../core/auth/auth.service';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import type { PurchaseDetail, PurchaseReturnDetail as ReturnDetail } from '../purchases.models';

const unit = { productUnitId: 'pu-pc', unitCode: 'pc', displayName: 'pc', decimalPlaces: 0, conversionFactorToBase: 1, isLineUnit: true, maxQty: 10 };
const rowOf = (code: string, kind: string, taxAmount: number, extra = {}) => ({
  code, kind, method: 'Percent', ratePercent: 9, amountPerBaseUnit: null, baseAmount: 100, taxAmount, originalBaseAmount: null, originalTaxAmount: null, ...extra,
});

function purchase(taxes: unknown[] | null): PurchaseDetail {
  return {
    id: 'po1', purchaseNo: 'PO-1', supplierName: 'Acme', purchaseDate: '2026-11-01', paymentMethod: 'Cash', grandTotal: 118, originalGrandTotal: 118, originalPaidAmount: 0, originalNetTotal: 118,
    currencyDecimalPrecision: 2, baseCurrencyDecimalPrecision: 2, baseCurrencySymbol: '₹', currencySymbol: '₹', currencyCode: 'INR', baseCurrencyCode: 'INR',
    lines: [{
      id: 'l1', productName: 'Pen', qty: 10, unitCode: 'pc', unitDecimalPlaces: 0, baseQty: 10, baseUnitLabel: 'pc', baseUnitDecimalPlaces: 0, taxableValue: 100, taxAmount: 18, lineTotal: 118,
      returnedBaseQty: 0, returnableBaseQty: 10, returnUnits: [unit], taxes,
    }],
  } as unknown as PurchaseDetail;
}

function form(taxes: unknown[] | null) {
  TestBed.configureTestingModule({
    imports: [PurchaseReturnForm],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'po1' }) } } },
      { provide: PurchasesService, useValue: { get: () => of(purchase(taxes)) } },
      { provide: CashBankService, useValue: { options: () => of([]) } },
      { provide: AuthService, useValue: { hasPermission: () => false } },
      { provide: ToastService, useValue: { show: vi.fn() } },
    ],
  });
  vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(PurchaseReturnForm);
  fixture.detectChanges();
  return { fixture, c: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
}

describe('PurchaseReturnForm tax estimate (TP-3)', () => {
  it('shows an estimated tax prorated from the original rows, labelled as an estimate', () => {
    const { c, fixture, el } = form([rowOf('CGST', 'Cgst', 9), rowOf('SGST', 'Sgst', 9)]);
    expect(el.querySelector('[data-testid="return-tax-estimate"]')?.textContent).toContain('—');
    c.setQty('l1', '4');
    fixture.detectChanges();
    const cell = el.querySelector('[data-testid="return-tax-estimate"]')!.textContent!.replace(/\s+/g, ' ');
    expect(cell).toContain('₹7.20');
    expect(cell).toContain('estimate — calculated on save');
    expect(c.estimate(c.lines()[0])?.rows.map((r) => [r.code, r.taxAmount])).toEqual([['CGST', 3.6], ['SGST', 3.6]]);
  });

  it('expands the estimated rows, legacy as one "Tax" row', () => {
    const { c, fixture, el } = form([rowOf('GST', 'Other', 18, { isLegacy: true, ratePercent: 18 })]);
    c.setQty('l1', '5');
    c.toggleTax('l1');
    fixture.detectChanges();
    const rows = Array.from(el.querySelectorAll('[data-testid="return-tax-rows"] tbody tr')).map((r) => r.textContent!.replace(/\s+/g, ' ').trim());
    expect(rows).toEqual(['Tax18%₹50.00₹9.00']);
  });

  it('estimates from the line tax for history without rows', () => {
    const { c } = form(null);
    c.setQty('l1', '5');
    expect(c.estimate(c.lines()[0])).toMatchObject({ tax: 9, taxable: 50, rows: [] });
  });
});

describe('PurchaseReturnDetail tax rows (TP-3)', () => {
  it('shows each line\'s reversed rows and the tax by component', () => {
    const ret = {
      id: 'r1', returnNo: 'PR-1', returnDate: '2026-11-06', purchaseOrderId: 'p1', purchaseNo: 'PUR-1', supplierName: 'Acme', reason: null, taxableTotal: 40, taxTotal: 7.2, total: 47.2, originalTotal: 47.2,
      refundedAmount: 0, currencyCode: 'INR', currencySymbol: '₹', currencyDecimalPrecision: 2, baseCurrencyCode: 'INR', baseCurrencySymbol: '₹', baseCurrencyDecimalPrecision: 2,
      createdByName: 'admin', createdAt: '2026-11-06T10:00:00Z', refunds: [], branchId: 'b',
      lines: [{
        id: 'rl1', productName: 'Pen', productUnitId: 'u', unitLabel: 'pc', unitCode: 'pc', conversionFactorToBase: 1, qty: 4, baseQty: 4, baseUnitLabel: 'pc', baseUnitDecimalPlaces: 0,
        taxableValue: 40, taxAmount: 7.2, lineTotal: 47.2, taxes: [rowOf('CGST', 'Cgst', 3.6, { baseAmount: 40 }), rowOf('SGST', 'Sgst', 3.6, { baseAmount: 40 })],
      }],
    } as unknown as ReturnDetail;
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'r1' }) } } },
        { provide: PurchasesService, useValue: { getReturn: () => of(ret) } },
      ],
    });
    const fixture = TestBed.createComponent(PurchaseReturnDetail);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(Array.from(el.querySelectorAll('[data-testid="tax-component"]')).map((e) => e.textContent!.replace(/\s+/g, ' ').trim())).toEqual(['CGST₹3.60', 'SGST₹3.60']);
    fixture.componentInstance.toggle('rl1');
    fixture.detectChanges();
    expect(el.querySelectorAll('[data-testid="return-tax-rows"] tbody tr')).toHaveLength(2);
  });
});
