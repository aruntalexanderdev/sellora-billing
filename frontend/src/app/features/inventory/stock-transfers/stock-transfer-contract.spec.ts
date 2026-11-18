import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../shared/scope/scope-testing';
import { ProductUnitsLookup } from './product-units-lookup';
import { StockTransferDetail } from './stock-transfer-detail/stock-transfer-detail';
import { StockTransfersService } from './stock-transfers.service';
import type { StockTransfer } from './stock-transfers.models';
import { TransferReceiveDialog } from './transfer-dialogs/transfer-receive-dialog';
import { TransferResolveDialog } from './transfer-dialogs/transfer-resolve-dialog';

/**
 * Wire-shaped fixtures: the JSON ASP.NET serialises for StockTransferDto (backend/src/Sellora.Api/Contracts/StockTransferContracts.cs),
 * camelCase, enums as strings, nullable members as null. A kg product (3 dp) so base quantities exercise decimals.
 */
const RICE_LINE = {
  id: 'ln-rice', lineNo: 1, productId: 'p-rice', productName: 'Rice', sku: 'RICE-1', baseUnitCode: 'kg', baseDecimalPlaces: 3,
  productUnitId: 'pu-bag', unitCode: 'bag', conversionFactorToBase: 25, requestedQty: 2, requestedBaseQty: 50,
  dispatchProductUnitId: 'pu-kg', dispatchUnitCode: 'kg', dispatchFactor: 1, dispatchedQty: 40, dispatchedBaseQty: 40,
  receivedBaseQty: 30, damagedBaseQty: 2.5, writtenOffBaseQty: 7.5, returnedBaseQty: 0, outstandingBaseQty: 0,
  batches: [{ purchaseBatchId: 'pb-1', internalBatchNo: 'PB-0001', unitCost: 1.2, dispatchedBaseQty: 40, receivedBaseQty: 30, damagedBaseQty: 2.5, writtenOffBaseQty: 7.5, returnedBaseQty: 0, outstandingBaseQty: 0 }],
};
const PEN_LINE = {
  id: 'ln-pen', lineNo: 2, productId: 'p-pen', productName: 'Pen', sku: 'PEN-1', baseUnitCode: 'pc', baseDecimalPlaces: 0,
  productUnitId: 'pu-box', unitCode: 'box', conversionFactorToBase: 10, requestedQty: 5, requestedBaseQty: 50,
  dispatchProductUnitId: 'pu-box', dispatchUnitCode: 'box', dispatchFactor: 10, dispatchedQty: 5, dispatchedBaseQty: 50,
  receivedBaseQty: 20, damagedBaseQty: 10, writtenOffBaseQty: 0, returnedBaseQty: 20, outstandingBaseQty: 0, batches: [],
};
const WIRE: unknown = {
  id: 'c0a80101-0000-4000-8000-000000000001', transferNo: 'TRF-0007', status: 'Completed', notes: null,
  branch: { id: 'b1', code: 'MAIN', name: 'Main' },
  from: { id: 'l-wh', code: 'WH', name: 'Warehouse', type: 'BranchWarehouse' },
  to: { id: 'l-van', code: 'VAN1', name: 'Van 1', type: 'Vehicle' },
  transit: { id: 'l-tr', code: 'TRANSIT', name: 'In transit', type: 'Transit' },
  sourceBranch: { id: 'b1', code: 'MAIN', name: 'Main' }, destinationBranch: { id: 'b1', code: 'MAIN', name: 'Main' },
  created: { userId: 'u1', userName: 'admin', at: '2026-11-06T10:00:00+00:00' },
  submitted: { userId: 'u1', userName: 'admin', at: '2026-11-06T10:01:00+00:00' },
  approved: { userId: 'u2', userName: 'boss', at: '2026-11-06T10:02:00+00:00' },
  rejected: null, rejectionReason: null,
  dispatched: { userId: 'u2', userName: 'boss', at: '2026-11-06T10:03:00+00:00' },
  completed: { userId: 'u3', userName: null, at: '2026-11-07T09:00:00+00:00' },
  cancelled: null, cancellationReason: null,
  lines: [RICE_LINE, PEN_LINE],
  receipts: [
    {
      id: 'r1', sequence: 1, kind: 'Receipt', at: '2026-11-06T12:00:00+00:00', byUserId: 'u3', byUserName: 'sam', notes: 'Two sacks split',
      lines: [
        { transferLineId: 'ln-rice', productId: 'p-rice', productUnitId: 'pu-kg', unitCode: 'kg', conversionFactorToBase: 1, acceptedQty: 30, acceptedBaseQty: 30, damagedQty: 2.5, damagedBaseQty: 2.5, writtenOffBaseQty: 0, returnedBaseQty: 0 },
        { transferLineId: 'ln-pen', productId: 'p-pen', productUnitId: 'pu-box', unitCode: 'box', conversionFactorToBase: 10, acceptedQty: 2, acceptedBaseQty: 20, damagedQty: 1, damagedBaseQty: 10, writtenOffBaseQty: 0, returnedBaseQty: 0 },
      ],
    },
    {
      id: 'r2', sequence: 2, kind: 'Resolution', at: '2026-11-07T09:00:00+00:00', byUserId: 'u1', byUserName: 'admin', notes: 'Shortfall settled',
      lines: [
        { transferLineId: 'ln-rice', productId: 'p-rice', productUnitId: 'pu-kg', unitCode: 'kg', conversionFactorToBase: 1, acceptedQty: 0, acceptedBaseQty: 0, damagedQty: 0, damagedBaseQty: 0, writtenOffBaseQty: 7.5, returnedBaseQty: 0 },
        { transferLineId: 'ln-pen', productId: 'p-pen', productUnitId: 'pu-box', unitCode: 'box', conversionFactorToBase: 10, acceptedQty: 0, acceptedBaseQty: 0, damagedQty: 0, damagedBaseQty: 0, writtenOffBaseQty: 0, returnedBaseQty: 20 },
      ],
    },
  ],
};

function mountDetail(t: StockTransfer) {
  TestBed.configureTestingModule({
    imports: [StockTransferDetail],
    providers: [
      provideRouter([]),
      ...provideScopeStubs(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: t.id }) } } },
      { provide: StockTransfersService, useValue: { get: () => of(t), history: () => of([]) } },
      { provide: ProductUnitsLookup, useValue: { units: () => of([]) } },
      { provide: AuthService, useValue: { hasPermission: () => true } },
    ],
  });
  const fixture = TestBed.createComponent(StockTransferDetail);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('StockTransferDetail against the API wire shape', () => {
  it('renders a receipt with entered quantities in the received unit', () => {
    const el = mountDetail(WIRE as StockTransfer);
    const receipt = el.querySelector('[data-testid="receipt-1"]')!.textContent!;
    expect(receipt).toContain('Receipt');
    expect(receipt).toContain('sam');
    expect(receipt).toContain('Two sacks split');
    expect(receipt).toMatch(/Rice:\s*accepted 30 kg;\s*damaged 2\.5 kg;/);
    expect(receipt).toMatch(/Pen:\s*accepted 2 box;\s*damaged 1 box;/);
  });

  it('renders a resolution with written-off and returned base quantities in each product base unit and precision', () => {
    const el = mountDetail(WIRE as StockTransfer);
    const resolution = el.querySelector('[data-testid="receipt-2"]')!.textContent!;
    expect(resolution).toContain('Resolution');
    expect(resolution).toContain('Shortfall settled');
    expect(resolution).toContain('written off 7.500 kg');
    expect(resolution).toContain('returned to source 20 pc');
    expect(resolution).not.toContain('accepted');
  });

  it('shows the dispatch snapshot and the line counters in base units with the base unit precision', () => {
    const el = mountDetail(WIRE as StockTransfer);
    const rice = el.querySelector('[data-testid="line-1"]')!.textContent!;
    expect(rice).toContain('2 bag');
    expect(rice).toContain('= 50.000 kg');
    expect(rice).toContain('40 kg');
    expect(rice).toContain('7.500 kg');
    expect(rice).toContain('2.500 kg');
  });

  it('tolerates an actor whose name cannot be resolved (userName null) in the timeline', () => {
    const el = mountDetail(WIRE as StockTransfer);
    expect(el.querySelector('[data-testid="timeline"]')!.textContent).toContain('Completed');
  });
});

const KG_UNITS = [
  { id: 'pu-kg', unitCode: 'kg', displayName: 'Kg', decimalPlaces: 3, conversionFactorToBase: 1, isBaseUnit: true, isActive: true },
  { id: 'pu-bag', unitCode: 'bag', displayName: 'Bag', decimalPlaces: 0, conversionFactorToBase: 25, isBaseUnit: false, isActive: true },
];
const problem = (errors: Record<string, string[]>) => new HttpErrorResponse({ status: 400, error: { title: 'One or more validation errors occurred.', status: 400, errors } });

function mountDialog<T>(component: new () => T, t: StockTransfer, service: Record<string, unknown>) {
  TestBed.configureTestingModule({
    imports: [component],
    providers: [
      { provide: StockTransfersService, useValue: service },
      { provide: ProductUnitsLookup, useValue: { units: () => of(KG_UNITS) } },
    ],
  });
  const fixture = TestBed.createComponent(component);
  fixture.componentRef.setInput('transfer', t);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const submit = () => {
    const b = Array.from(el.querySelectorAll('.actions ui-button button')) as HTMLButtonElement[];
    b.find((x) => /Receive|Resolve/.test(x.textContent ?? '') && !/All:/.test(x.textContent ?? ''))!.click();
    fixture.detectChanges();
  };
  const input = (id: string, value: string) => {
    const e = el.querySelector(`[data-testid="${id}"]`) as HTMLInputElement | HTMLSelectElement;
    e.value = value;
    e.dispatchEvent(new Event(e instanceof HTMLSelectElement ? 'change' : 'input'));
    fixture.detectChanges();
  };
  return { fixture, component: fixture.componentInstance, el, submit, input };
}

const DISPATCHED = { ...(WIRE as StockTransfer), status: 'Dispatched' as const, completed: null, receipts: [],
  lines: [{ ...(RICE_LINE as unknown as StockTransfer['lines'][0]), receivedBaseQty: 0, damagedBaseQty: 0, writtenOffBaseQty: 0, outstandingBaseQty: 40 }] };

describe('Transfer dialogs against API error shapes', () => {
  it('receive: a server excess error keyed lines[0].acceptedQuantity lands on the row, not the banner, and the dialog stays open', () => {
    const msg = 'Only 40.000 kg of Rice is still outstanding on this transfer.';
    const receive = vi.fn(() => throwError(() => problem({ 'lines[0].acceptedQuantity': [msg] })));
    const { component, el, submit } = mountDialog(TransferReceiveDialog, DISPATCHED, { receive });
    const done = vi.fn();
    component.done.subscribe(done);
    submit();
    expect(receive).toHaveBeenCalledTimes(1);
    expect(el.querySelector('[data-testid="receive-row-error-0"]')!.textContent).toContain(msg);
    expect(el.querySelector('[data-testid="receive-error"]')).toBeNull();
    expect(done).not.toHaveBeenCalled();
  });

  it('receive: an unmapped server field is shown in the banner', () => {
    const receive = vi.fn(() => throwError(() => problem({ status: ['Transfer is not dispatched.'] })));
    const { el, submit } = mountDialog(TransferReceiveDialog, DISPATCHED, { receive });
    submit();
    expect(el.querySelector('[data-testid="receive-error"]')!.textContent).toContain('Transfer is not dispatched.');
  });

  it('resolve: the company-level refusal (400, field stockLocationId) is shown in the banner and nothing is posted twice', () => {
    const msg = 'Resolving a transfer requires the permission company-wide.';
    const resolve = vi.fn(() => throwError(() => problem({ stockLocationId: [msg] })));
    const { component, el, input, submit } = mountDialog(TransferResolveDialog, DISPATCHED, { resolve });
    const done = vi.fn();
    component.done.subscribe(done);
    input('resolve-action-0', 'WriteOff');
    input('resolve-reason', 'Lost');
    submit();
    expect(resolve).toHaveBeenCalledTimes(1);
    expect(el.querySelector('[data-testid="resolve-error"]')!.textContent).toContain(msg);
    expect(done).not.toHaveBeenCalled();
  });
});
