import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ProductUnitsLookup } from '../../stock-transfers/product-units-lookup';
import type { ConsignmentDetail, ConsignmentReceipt } from '../../shipments/shipments.models';
import { ConsignmentsService } from '../consignments.service';
import { ConsignmentDetailPage } from './consignment-detail';

const loc = (id: string, name: string) => ({ id, code: id.toUpperCase(), name, type: 'BranchStore' });
function detail(over: Partial<ConsignmentDetail> = {}): ConsignmentDetail {
  return {
    id: 'c1', consignmentNo: 'CON-0001', status: 'Loaded', direction: 'incoming', shipmentId: null, shipmentNo: 'SHP-0001', shipmentStatus: 'Loading',
    transportMode: 'Road', transportVia: 'OwnFleet', providerName: null, transportReference: null, plannedDispatchDate: null,
    source: loc('store', 'Main Store'), destination: loc('van', 'Van 1'), destinationBranch: { id: 'b2', code: 'B2', name: 'Branch 2' },
    leg: { vehicleNumber: 'KA-01', vehicleName: 'Tempo', driverName: 'Ravi', driverPhone: '999' }, loadedAt: null, dispatchedAt: null, receiptCount: 0,
    lines: [{ id: 'l1', transferNo: 'TRF-1', productId: 'p', productSku: 'PEN', productName: 'Pen', baseUnitCode: 'pc', productUnitId: 'box', unitCode: 'box', enteredQty: 5, allocatedBaseQty: 50, loadedBaseQty: 20, loadedQty: 2, dispatchedBaseQty: 0, acceptedBaseQty: 0, damagedBaseQty: 0, rejectedBaseQty: 0, missingBaseQty: 0, postedBaseQty: 0, conversionFactorToBase: 10, dispositionedDamagedBaseQty: 0, dispositionedRejectedBaseQty: 0, dispositionedMissingBaseQty: 0, openDiscrepancyBaseQty: 0 }],
    dispositionCount: 0, hasOpenDiscrepancy: false,
    ...over,
  };
}

const actor = { userId: 'u', userName: 'dest', at: '2026-11-16T12:00:00Z' };
const rcpt = (over: Partial<ConsignmentReceipt> = {}): ConsignmentReceipt => ({
  id: 'r1', consignmentId: 'c1', consignmentNo: 'CON-0001', consignmentStatus: 'PartiallyReceived', shipmentStatus: 'PartiallyDelivered', sequence: 1,
  status: 'Verified', verified: actor, posted: null, notes: null,
  policyMode: 'Direct', requireSeparation: false, rejected: null, rejectionReason: null, approvedBy: null,
  lines: [{ consignmentLineId: 'l1', productId: 'p', productSku: 'PEN', productName: 'Pen', baseUnitCode: 'pc', productUnitId: 'box', unitCode: 'box', conversionFactorToBase: 10,
    acceptedQty: 3, acceptedBaseQty: 30, damagedQty: 1, damagedBaseQty: 10, rejectedQty: 0, rejectedBaseQty: 0, missingQty: 0, missingBaseQty: 0 }],
  ...over,
});
const inTransit = (over: Partial<ConsignmentDetail> = {}) => {
  const base = detail();
  return detail({
    status: 'InTransit', shipmentStatus: 'Dispatched', dispatchedAt: '2026-11-16T10:00:00Z',
    lines: [{ ...base.lines[0], dispatchedBaseQty: 50 }], ...over,
  });
};

function render(d: ConsignmentDetail, permissions: string[] = [], receipts: ConsignmentReceipt[] = [], extra: Record<string, unknown> = {}) {
  const service = {
    get: vi.fn(() => of(d)), receipts: vi.fn(() => of(receipts)), postReceipt: vi.fn(() => of(rcpt({ status: 'Posted' }))), recordReceipt: vi.fn(),
    ...extra,
  };
  const ask = vi.fn(() => Promise.resolve(true));
  TestBed.configureTestingModule({
    imports: [ConsignmentDetailPage],
    providers: [
      provideRouter([]),
      { provide: ConsignmentsService, useValue: service },
      { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
      { provide: ConfirmDialogService, useValue: { ask } },
      { provide: ToastService, useValue: { show: vi.fn() } },
      { provide: ProductUnitsLookup, useValue: { units: () => of([]) } },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 'c1' } } } },
    ],
  });
  const fixture = TestBed.createComponent(ConsignmentDetailPage);
  fixture.detectChanges();
  return Object.assign(fixture.nativeElement as HTMLElement, { fixture, service, ask });
}

describe('ConsignmentDetailPage', () => {
  it('incoming: shows only this consignment and its own leg, with no shipment link', () => {
    const el = render(detail());
    expect(el.querySelector('[data-testid="shipment-link"]')).toBeNull();
    expect(el.querySelector('[data-testid="shipment-no"]')?.textContent).toContain('SHP-0001');
    expect(el.querySelector('[data-testid="leg"]')?.textContent).toContain('KA-01');
    expect(el.querySelectorAll('[data-testid="line"]')).toHaveLength(1);
    expect(el.textContent).not.toContain('Cost');
  });

  it('outgoing: links to the shipment planner', () => {
    const el = render(detail({ direction: 'outgoing', shipmentId: 's1' }));
    expect(el.querySelector('[data-testid="shipment-link"]')?.getAttribute('href')).toBe('/inventory/shipments/s1');
  });

  it('shows dispatched / accepted / damaged / rejected / missing / posted and the outstanding quantity per line', () => {
    const base = inTransit();
    const el = render(
      inTransit({ lines: [{ ...base.lines[0], dispatchedBaseQty: 50, acceptedBaseQty: 20, damagedBaseQty: 5, rejectedBaseQty: 5, missingBaseQty: 10, postedBaseQty: 10 }] }),
    );
    const text = (id: string) => el.querySelector(`[data-testid="${id}"]`)?.textContent?.trim();
    expect(text('q-dispatched')).toBe('50');
    expect(text('q-accepted')).toBe('20');
    expect(text('q-damaged')).toBe('5');
    expect(text('q-rejected')).toBe('5');
    expect(text('q-missing')).toBe('10');
    expect(text('q-posted')).toBe('10');
    expect(text('q-outstanding')).toContain('10'); // 50 - 20 - 5 - 5 - 10 = 10
    expect(el.querySelector('[data-testid="q-outstanding"] .sub')?.textContent).toContain('in transit 40');
    expect(el.querySelector('[data-testid="base-note"]')?.textContent).toMatch(/stay in transit/i);
    expect(el.querySelector('[data-testid="dispatched-at"]')?.textContent).toContain('2026-11-16');
  });

  describe('record receipt gating', () => {
    const cases: [string, ConsignmentDetail['status'], string[], boolean][] = [
      ['InTransit with Receive', 'InTransit', ['StockTransfers.Receive'], true],
      ['PartiallyReceived with Receive', 'PartiallyReceived', ['StockTransfers.Receive'], true],
      ['InTransit without Receive', 'InTransit', ['StockTransfers.View', 'Shipments.View'], false],
      ['Loaded with Receive', 'Loaded', ['StockTransfers.Receive'], false],
      ['Received with Receive', 'Received', ['StockTransfers.Receive'], false],
    ];
    it.each(cases)('%s -> %s', (_name, status, perms, shown) => {
      const el = render(inTransit({ status }), perms);
      expect(el.querySelector('[data-testid="act-receipt"]') !== null).toBe(shown);
    });

    it('opens the receipt dialog', () => {
      const el = render(inTransit(), ['StockTransfers.Receive']);
      (el.querySelector('[data-testid="act-receipt"] button') as HTMLButtonElement).click();
      el.fixture.detectChanges();
      expect(el.querySelector('app-receipt-dialog')).not.toBeNull();
    });
  });

  describe('receipts list and posting', () => {
    const withReceipts = (perms: string[], list: ConsignmentReceipt[]) =>
      render(inTransit({ status: 'PartiallyReceived', receiptCount: list.length }), perms, list);

    it('lists receipts; Post only on Verified receipts for StockTransfers.Receive holders', () => {
      const el = withReceipts(['StockTransfers.Receive'], [rcpt(), rcpt({ id: 'r2', sequence: 2, status: 'Posted', posted: actor })]);
      expect(el.querySelector('[data-testid="receipt-1"]')).not.toBeNull();
      expect(el.querySelector('[data-testid="post-1"]')).not.toBeNull();
      expect(el.querySelector('[data-testid="post-2"]')).toBeNull();
    });

    it('hides Post without the permission', () => {
      const el = withReceipts(['StockTransfers.View'], [rcpt()]);
      expect(el.querySelector('[data-testid="receipt-1"]')).not.toBeNull();
      expect(el.querySelector('[data-testid="post-1"]')).toBeNull();
    });

    it('confirms, then posts with an idempotency key and re-reads the consignment', async () => {
      const el = withReceipts(['StockTransfers.Receive'], [rcpt()]);
      (el.querySelector('[data-testid="post-1"] button') as HTMLButtonElement).click();
      await el.fixture.whenStable();
      expect(el.ask).toHaveBeenCalled();
      expect(el.service.postReceipt).toHaveBeenCalledWith('c1', 'r1', expect.any(String));
      expect(el.service.get.mock.calls.length).toBeGreaterThan(1);
    });

    it('does not post when the confirmation is declined', async () => {
      const el = withReceipts(['StockTransfers.Receive'], [rcpt()]);
      el.ask.mockResolvedValueOnce(false);
      (el.querySelector('[data-testid="post-1"] button') as HTMLButtonElement).click();
      await el.fixture.whenStable();
      expect(el.service.postReceipt).not.toHaveBeenCalled();
    });
  });
});
