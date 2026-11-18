import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { ContextService } from '../../../../core/context/context.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ProductUnitsLookup } from '../../stock-transfers/product-units-lookup';
import type { ConsignmentDetail, ConsignmentDisposition, ConsignmentReceipt } from '../../shipments/shipments.models';
import { ConsignmentsService } from '../consignments.service';
import { ConsignmentDetailPage } from './consignment-detail';

const loc = (id: string) => ({ id, code: id.toUpperCase(), name: id, type: 'BranchStore' });
const D0 = { dispositionedDamagedBaseQty: 0, dispositionedRejectedBaseQty: 0, dispositionedMissingBaseQty: 0, openDiscrepancyBaseQty: 0 };
const detail = (over: Partial<ConsignmentDetail> = {}): ConsignmentDetail => ({
  id: 'c1', consignmentNo: 'CON-1', status: 'Received', direction: 'incoming', shipmentId: null, shipmentNo: 'SHP-1', shipmentStatus: 'Delivered',
  transportMode: 'Road', transportVia: 'OwnFleet', providerName: null, transportReference: null, plannedDispatchDate: null,
  source: loc('store'), destination: loc('van'), destinationBranch: { id: 'b', code: 'B', name: 'B' }, leg: null, loadedAt: null, dispatchedAt: '2026-11-16T10:00:00Z',
  receiptCount: 1, dispositionCount: 0, hasOpenDiscrepancy: true,
  lines: [{
    id: 'l1', transferNo: 'TRF-1', productId: 'p', productSku: 'PEN', productName: 'Pen', baseUnitCode: 'pc', productUnitId: 'box', unitCode: 'box', enteredQty: 5,
    allocatedBaseQty: 50, loadedBaseQty: 50, loadedQty: 5, dispatchedBaseQty: 50, acceptedBaseQty: 40, damagedBaseQty: 10, rejectedBaseQty: 0, missingBaseQty: 0, postedBaseQty: 40,
    conversionFactorToBase: 10, ...D0, dispositionedDamagedBaseQty: 4, openDiscrepancyBaseQty: 6,
  }],
  ...over,
});
const actor = (id: string) => ({ userId: id, userName: id, at: '2026-11-17T10:00:00Z' });
const rcpt = (over: Partial<ConsignmentReceipt> = {}): ConsignmentReceipt => ({
  id: 'r1', consignmentId: 'c1', consignmentNo: 'CON-1', consignmentStatus: 'Received', shipmentStatus: 'Delivered', sequence: 1, status: 'Verified', verified: actor('rec'),
  posted: null, notes: null, lines: [], policyMode: 'Direct', requireSeparation: false, rejected: null, rejectionReason: null, approvedBy: null, ...over,
});

function render(d: ConsignmentDetail, permissions: string[], receipts: ConsignmentReceipt[], opts: { userId?: string; dispositions?: ConsignmentDisposition[]; companyPermissions?: string[] } = {}) {
  const service = {
    get: vi.fn(() => of(d)), receipts: vi.fn(() => of(receipts)), dispositions: vi.fn(() => of(opts.dispositions ?? [])),
    postReceipt: vi.fn(() => of(rcpt({ status: 'Posted' }))), approveReceipt: vi.fn(() => of(rcpt({ status: 'Posted' }))), rejectReceipt: vi.fn(() => of(rcpt({ status: 'RejectedForCorrection' }))),
    recordReceipt: vi.fn(), recordDisposition: vi.fn(),
  };
  const ask = vi.fn(() => Promise.resolve(true));
  TestBed.configureTestingModule({
    imports: [ConsignmentDetailPage],
    providers: [
      provideRouter([]),
      { provide: ConsignmentsService, useValue: service },
      { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c), currentUser: () => ({ id: opts.userId ?? 'me' }) } },
      { provide: ContextService, useValue: { canCompanyView: (c: string) => (opts.companyPermissions ?? permissions).includes(c) } },
      { provide: ConfirmDialogService, useValue: { ask } },
      { provide: ToastService, useValue: { show: vi.fn() } },
      { provide: ProductUnitsLookup, useValue: { units: () => of([{ id: 'box', decimalPlaces: 0 }]) } },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 'c1' } } } },
    ],
  });
  const fixture = TestBed.createComponent(ConsignmentDetailPage);
  fixture.detectChanges();
  return Object.assign(fixture.nativeElement as HTMLElement, { fixture, service, ask });
}
const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`);
const btn = (el: HTMLElement, id: string) => q(el, id)?.querySelector('button') as HTMLButtonElement | null;
const R = 'StockTransfers.Receive';
const A = 'StockTransfers.ApproveReceipt';

describe('ConsignmentDetailPage receipt policy', () => {
  it('shows a badge for each of the four receipt states', () => {
    const el = render(detail(), [], [
      rcpt({ sequence: 1, status: 'Verified' }), rcpt({ id: 'r2', sequence: 2, status: 'PendingApproval' }),
      rcpt({ id: 'r3', sequence: 3, status: 'RejectedForCorrection' }), rcpt({ id: 'r4', sequence: 4, status: 'Posted', posted: actor('p') }),
    ]);
    expect(q(el, 'receipt-status-1')?.textContent).toContain('Verified');
    expect(q(el, 'receipt-status-2')?.textContent).toContain('Pending approval');
    expect(q(el, 'receipt-status-3')?.textContent).toContain('Rejected for correction');
    expect(q(el, 'receipt-status-4')?.textContent).toContain('Posted');
  });

  it('Direct: Post for Verified + Receive; Reject needs Receive; Approve never shown', () => {
    const el = render(detail(), [R], [rcpt()]);
    expect(q(el, 'post-1')).not.toBeNull();
    expect(q(el, 'reject-1')).not.toBeNull();
    expect(q(el, 'approve-1')).toBeNull();
  });

  it('Supervisor: Approve for PendingApproval + ApproveReceipt; no Post; Reject gated by ApproveReceipt', () => {
    const pending = rcpt({ status: 'PendingApproval', policyMode: 'SupervisorApproval' });
    const sup = render(detail(), [A], [pending]);
    expect(q(sup, 'approve-1')).not.toBeNull();
    expect(q(sup, 'post-1')).toBeNull();
    expect(q(sup, 'reject-1')).not.toBeNull();
    expect(q(sup, 'pending-note-1')?.textContent).toMatch(/awaiting supervisor approval/i);

    TestBed.resetTestingModule();
    const receiver = render(detail(), [R], [pending]);
    expect(q(receiver, 'approve-1')).toBeNull();
    expect(q(receiver, 'reject-1')).toBeNull();
  });

  it('separation: the recorder sees Post / Approve disabled with a tooltip; another user can act', () => {
    const verified = rcpt({ requireSeparation: true });
    const own = render(detail(), [R], [verified], { userId: 'rec' });
    expect(btn(own, 'post-1')!.disabled).toBe(true);
    expect(btn(own, 'post-1')!.title).toMatch(/separation of duties/i);

    TestBed.resetTestingModule();
    const other = render(detail(), [R], [verified], { userId: 'someone' });
    expect(btn(other, 'post-1')!.disabled).toBe(false);

    TestBed.resetTestingModule();
    const pending = rcpt({ status: 'PendingApproval', policyMode: 'SupervisorApproval', requireSeparation: true });
    const approver = render(detail(), [A], [pending], { userId: 'rec' });
    expect(btn(approver, 'approve-1')!.disabled).toBe(true);
    expect(btn(approver, 'approve-1')!.title).toMatch(/cannot approve/i);
  });

  it('Approve confirms, calls the service with a key and re-reads', async () => {
    const el = render(detail(), [A], [rcpt({ status: 'PendingApproval', policyMode: 'SupervisorApproval' })]);
    btn(el, 'approve-1')!.click();
    await el.fixture.whenStable();
    expect(el.ask).toHaveBeenCalled();
    expect(el.service.approveReceipt).toHaveBeenCalledWith('c1', 'r1', expect.any(String));
    expect(el.service.get.mock.calls.length).toBeGreaterThan(1);
  });

  it('Reject opens a reason dialog and requires the reason before calling the service', () => {
    const el = render(detail(), [R], [rcpt()]);
    btn(el, 'reject-1')!.click();
    el.fixture.detectChanges();
    expect(el.querySelector('app-receipt-reject-dialog')).not.toBeNull();
    btn(el, 'reject-submit')!.click();
    el.fixture.detectChanges();
    expect(el.service.rejectReceipt).not.toHaveBeenCalled();
    expect(q(el, 'reject-reason-error')).not.toBeNull();

    const reason = q(el, 'reject-reason') as HTMLTextAreaElement;
    reason.value = 'wrong count';
    reason.dispatchEvent(new Event('input'));
    btn(el, 'reject-submit')!.click();
    expect(el.service.rejectReceipt).toHaveBeenCalledWith('c1', 'r1', 'wrong count', expect.any(String));
  });

  it('shows the rejection reason and the approvedBy stamp', () => {
    const el = render(detail(), [], [
      rcpt({ status: 'RejectedForCorrection', rejected: actor('boss'), rejectionReason: 'wrong count' }),
      rcpt({ id: 'r2', sequence: 2, status: 'Posted', policyMode: 'SupervisorApproval', posted: actor('boss'), approvedBy: actor('boss') }),
    ]);
    expect(q(el, 'rejected-1')?.textContent).toContain('wrong count');
    expect(q(el, 'approved-by-2')?.textContent).toContain('Approved by boss');
  });
});

describe('ConsignmentDetailPage discrepancies', () => {
  it('shows Reconciled status and the discrepancy table with resolved and open amounts', () => {
    const el = render(detail({ status: 'Reconciled', shipmentStatus: 'Completed', hasOpenDiscrepancy: false }), [], [rcpt({ status: 'Posted' })]);
    expect(q(el, 'consignment-status')?.textContent).toContain('Reconciled');
    expect(q(el, 'shipment-status')?.textContent).toContain('Completed');
    const damaged = q(el, 'd-damaged')!.textContent!;
    expect(damaged).toContain('10');
    expect(damaged).toContain('resolved 4');
    expect(damaged).toContain('open 6');
    expect(q(el, 'd-open')?.textContent?.trim()).toBe('6');
  });

  it('explains that only posted quantity can be resolved', () => {
    const el = render(detail(), ['StockTransfers.Resolve'], [rcpt({ status: 'Posted' })]);
    expect(q(el, 'discrepancy-open')?.textContent).toMatch(/only quantity from posted receipts/i);
  });

  it('Resolve is shown only with company-level StockTransfers.Resolve and posted quantity still open to a disposition', () => {
    // Posted receipt: 10 damaged on l1, 4 already dispositioned -> 6 open.
    const posted = [rcpt({ status: 'Posted', lines: [{ consignmentLineId: 'l1', acceptedBaseQty: 40, damagedBaseQty: 10, rejectedBaseQty: 0, missingBaseQty: 0 } as ConsignmentReceipt['lines'][number]] })];
    expect(q(render(detail(), ['StockTransfers.Resolve'], posted), 'act-resolve')).not.toBeNull();
    TestBed.resetTestingModule();
    expect(q(render(detail(), [R, A], posted), 'act-resolve')).toBeNull();
    TestBed.resetTestingModule();
    // Branch-level grant only: the API answers 404, so the action is not offered.
    expect(q(render(detail(), ['StockTransfers.Resolve'], posted, { companyPermissions: [] }), 'act-resolve')).toBeNull();
    TestBed.resetTestingModule();
    // Damage only on an unposted (Verified / PendingApproval) receipt: nothing is dispositionable yet.
    const unposted = [rcpt({ status: 'Verified', lines: posted[0].lines })];
    expect(q(render(detail(), ['StockTransfers.Resolve'], unposted), 'act-resolve')).toBeNull();
  });

  it('lists the disposition history', () => {
    const d: ConsignmentDisposition = {
      id: 'd1', consignmentId: 'c1', consignmentNo: 'CON-1', consignmentStatus: 'Received', shipmentStatus: 'Delivered', sequence: 1, reason: 'broken in transit', by: actor('boss'),
      lines: [{ consignmentLineId: 'l1', productId: 'p', productSku: 'PEN', productName: 'Pen', baseUnitCode: 'pc', productUnitId: 'box', unitCode: 'box', conversionFactorToBase: 10, bucket: 'Damaged', action: 'WriteOff', quantity: 0.4, baseQty: 4 }],
    };
    const el = render(detail({ dispositionCount: 1 }), [], [rcpt({ status: 'Posted' })], { dispositions: [d] });
    expect(q(el, 'disposition-1')?.textContent).toContain('broken in transit');
    expect(q(el, 'disposition-1')?.textContent).toContain('Write off');
  });
});
