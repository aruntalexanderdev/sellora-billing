import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { SalesReturnForm } from './sales-return-form';
import { SalesService } from '../../sales.service';
import { CashBankService } from '../../../accounts/cash-bank/cash-bank.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import type { SalesInvoiceDetail, SalesInvoiceLine, SaleReturnUnit } from '../../sales.models';

const ru = (id: string, code: string, factor: number, maxQty: number, isLineUnit = false, decimalPlaces = 0): SaleReturnUnit => ({
  productUnitId: id, unitCode: code, displayName: code, decimalPlaces, conversionFactorToBase: factor, isLineUnit, maxQty,
});

// 3 Box (30 pc) sold for 270; 4 pc already returned, so 26 pc can still come back.
const LINE: SalesInvoiceLine = {
  id: 'l1', productId: 'pen', productName: 'Pen', productUnitId: 'pu-box', unitLabel: 'Box', unitCode: 'box',
  conversionFactorToBase: 10, unitDecimalPlaces: 0, qty: 3, baseQty: 30, baseUnitLabel: 'pc', baseUnitDecimalPlaces: 0,
  unitPrice: 90, listUnitPrice: 90, priceOverridden: false, taxRatePercent: 0, discount: 0, taxableValue: 270, taxAmount: 0,
  lineTotal: 270, costOfGoodsSold: 150, returnedBaseQty: 4, returnableBaseQty: 26, batchConsumptions: [],
  returnUnits: [ru('pu-pc', 'pc', 1, 26), ru('pu-box', 'box', 10, 2, true)],
};

function setup() {
  const createReturn = vi.fn(() => of({ id: 'r1', returnNo: 'SR-1' }));
  const invoice = {
    id: 'inv', invoiceNo: 'INV-1', customerName: 'Walk-in', invoiceDate: '2026-11-01', paymentMethod: 'Cash', paidAmount: 270,
    netTotal: 270, lines: [LINE],
  } as unknown as SalesInvoiceDetail;
  TestBed.configureTestingModule({
    imports: [SalesReturnForm],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'inv' }) } } },
      { provide: SalesService, useValue: { get: () => of(invoice), createReturn } },
      { provide: CashBankService, useValue: { options: () => of([]) } },
      { provide: ToastService, useValue: { show: vi.fn() } },
    ],
  });
  vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(SalesReturnForm);
  fixture.detectChanges();
  return { fixture, c: fixture.componentInstance, createReturn };
}

describe('SalesReturnForm', () => {
  it('starts in the line unit and switching the unit changes the step and the max', () => {
    const { fixture, c } = setup();
    expect(c.unitFor(LINE)?.productUnitId).toBe('pu-box');
    expect(c.quantityStep(c.unitFor(LINE)?.decimalPlaces)).toBe('1');
    c.setUnit('l1', 'pu-pc');
    fixture.detectChanges();
    expect(c.unitFor(LINE)?.maxQty).toBe(26);
    const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('[data-testid="return-qty"]')!;
    expect(input.max).toBe('26');
    c.setQty('l1', '27');
    expect(c.qtyError(LINE)).toContain('At most 26');
    c.setQty('l1', '26');
    expect(c.qtyError(LINE)).toBeNull();
    c.setUnit('l1', 'pu-box');
    expect(c.qtyError(LINE)).toContain('At most 2');
  });

  it('prorates the credit by base quantity', () => {
    const { c } = setup();
    c.setUnit('l1', 'pu-pc');
    c.setQty('l1', '5');
    // 5 pc of 30 pc worth 270.
    expect(c.credit()).toBeCloseTo(45);
    c.setUnit('l1', 'pu-box');
    c.setQty('l1', '2');
    expect(c.credit()).toBeCloseTo(180);
  });

  it('sends the chosen productUnitId and quantity', () => {
    const { c, createReturn } = setup();
    c.setUnit('l1', 'pu-pc');
    c.setQty('l1', '6');
    c.submit();
    expect(createReturn).toHaveBeenCalledOnce();
    const body = (createReturn.mock.calls[0] as unknown[])[0] as { lines: unknown[] };
    expect(body.lines).toEqual([{ salesInvoiceLineId: 'l1', productUnitId: 'pu-pc', qty: 6 }]);
  });

  it('"return everything" picks a unit that fits the remaining base quantity', () => {
    const { c } = setup();
    c.returnAll();
    // 26 pc is not a whole number of Box, so it falls back to the base unit.
    expect(c.unitFor(LINE)?.productUnitId).toBe('pu-pc');
    expect(c.qty()['l1']).toBe(26);
  });

  it('sends one Idempotency-Key per form and no location (the server resolves the restock location)', () => {
    const { c, createReturn } = setup();
    c.setQty('l1', '2');
    c.submit();
    const [req, key] = createReturn.mock.calls[0] as unknown as [Record<string, unknown>, string];
    expect(req).not.toHaveProperty('stockLocationId');
    expect(typeof key).toBe('string');
    expect(key.length).toBeGreaterThan(8);
  });
});
