import {
  SEPARATION_APPROVE_REASON, SEPARATION_POST_REASON, bucketOpenBase, buildDispositionRequest, canResolve, consignmentStatusTone, dispositionableBase, mapDispositionProblem,
  postedBucketBase, receiptActions, receiptStatusTone, shipmentStatusLabel, shipmentStatusTone, validateDisposition, validateRejectReason, type DispositionRow,
} from './shipment-rules';
import type { ConsignmentReceipt, ConsignmentReceiptStatus, ReceiptPostingMode } from './shipments.models';

const actor = (userId: string) => ({ userId, userName: userId, at: '2026-11-17T10:00:00Z' });
const receipt = (over: Partial<ConsignmentReceipt> = {}): ConsignmentReceipt => ({
  id: 'r1', consignmentId: 'c1', consignmentNo: 'CON-1', consignmentStatus: 'PartiallyReceived', shipmentStatus: 'PartiallyDelivered', sequence: 1,
  status: 'Verified', verified: actor('rec'), posted: null, notes: null, lines: [], policyMode: 'Direct', requireSeparation: false, rejected: null, rejectionReason: null, approvedBy: null,
  ...over,
});
const perms = (...p: string[]) => (c: string) => p.includes(c);

describe('receiptActions: mode x permission x recorder', () => {
  const R = 'StockTransfers.Receive';
  const A = 'StockTransfers.ApproveReceipt';
  type Case = [string, ReceiptPostingMode, ConsignmentReceiptStatus, string[], boolean, [string, string, string]];
  // [name, mode, status, permissions, separation applies && current user is the recorder, [post, approve, reject]]
  const cases: Case[] = [
    ['Direct, Verified, Receive', 'Direct', 'Verified', [R], false, ['enabled', 'hidden', 'enabled']],
    ['Direct, Verified, no permission', 'Direct', 'Verified', [], false, ['hidden', 'hidden', 'hidden']],
    ['Direct, Verified, only ApproveReceipt', 'Direct', 'Verified', [A], false, ['hidden', 'hidden', 'hidden']],
    ['Direct, Verified, recorder under separation', 'Direct', 'Verified', [R], true, ['blocked', 'hidden', 'enabled']],
    ['Supervisor, Pending, ApproveReceipt', 'SupervisorApproval', 'PendingApproval', [A], false, ['hidden', 'enabled', 'enabled']],
    ['Supervisor, Pending, Receive only', 'SupervisorApproval', 'PendingApproval', [R], false, ['hidden', 'hidden', 'hidden']],
    ['Supervisor, Pending, recorder under separation', 'SupervisorApproval', 'PendingApproval', [A, R], true, ['hidden', 'blocked', 'enabled']],
    ['Supervisor, Pending, both permissions', 'SupervisorApproval', 'PendingApproval', [A, R], false, ['hidden', 'enabled', 'enabled']],
    ['Posted receipt offers nothing', 'Direct', 'Posted', [R, A], false, ['hidden', 'hidden', 'hidden']],
    ['Rejected receipt offers nothing', 'SupervisorApproval', 'RejectedForCorrection', [R, A], false, ['hidden', 'hidden', 'hidden']],
  ];
  it.each(cases)('%s', (_n, mode, status, permissions, recorderBlocked, [post, approve, reject]) => {
    const r = receipt({ policyMode: mode, status, requireSeparation: recorderBlocked });
    const a = receiptActions(r, perms(...permissions), recorderBlocked ? 'rec' : 'someone-else');
    expect([a.post.state, a.approve.state, a.reject.state]).toEqual([post, approve, reject]);
  });

  it('separation only blocks the recorder, and only when the receipt snapshot requires it', () => {
    const sep = receipt({ requireSeparation: true });
    expect(receiptActions(sep, perms('StockTransfers.Receive'), 'other').post.state).toBe('enabled');
    expect(receiptActions(sep, perms('StockTransfers.Receive'), 'rec').post).toEqual({ state: 'blocked', reason: SEPARATION_POST_REASON });
    expect(receiptActions(receipt(), perms('StockTransfers.Receive'), 'rec').post.state).toBe('enabled');
    const pending = receipt({ status: 'PendingApproval', policyMode: 'SupervisorApproval', requireSeparation: true });
    expect(receiptActions(pending, perms('StockTransfers.ApproveReceipt'), 'rec').approve.reason).toBe(SEPARATION_APPROVE_REASON);
  });

  it('an unknown current user never blocks (the server stays authoritative)', () => {
    expect(receiptActions(receipt({ requireSeparation: true }), perms('StockTransfers.Receive'), null).post.state).toBe('enabled');
  });
});

describe('statuses', () => {
  it('Reconciled / Completed are good, labelled and receipt states have tones', () => {
    expect(consignmentStatusTone('Reconciled')).toBe('good');
    expect(shipmentStatusTone('Completed')).toBe('good');
    expect(shipmentStatusLabel('Completed')).toBe('Completed');
    expect(receiptStatusTone('Posted')).toBe('good');
    expect(receiptStatusTone('RejectedForCorrection')).toBe('bad');
    expect(receiptStatusTone('PendingApproval')).toBe('warn');
  });
  it('reject reason is required and max 500', () => {
    expect(validateRejectReason('  ')).not.toBeNull();
    expect(validateRejectReason('x'.repeat(501))).not.toBeNull();
    expect(validateRejectReason('recount')).toBeNull();
  });
});

describe('dispositions', () => {
  const line = { id: 'l1', damagedBaseQty: 10, rejectedBaseQty: 0, missingBaseQty: 5, dispositionedDamagedBaseQty: 4, dispositionedRejectedBaseQty: 0, dispositionedMissingBaseQty: 0 };
  const rl = (damaged: number, missing: number) => ({ consignmentLineId: 'l1', damagedBaseQty: damaged, rejectedBaseQty: 0, missingBaseQty: missing }) as ConsignmentReceipt['lines'][number];
  const receipts = [receipt({ status: 'Posted', lines: [rl(6, 0)] }), receipt({ id: 'r2', status: 'Verified', lines: [rl(4, 5)] }), receipt({ id: 'r3', status: 'RejectedForCorrection', lines: [rl(9, 9)] })];

  it('open = recorded minus dispositioned; dispositionable counts POSTED receipts only', () => {
    expect(bucketOpenBase(line, 'Damaged')).toBe(6);
    expect(postedBucketBase(receipts, 'l1', 'Damaged')).toBe(6);
    expect(postedBucketBase(receipts, 'l1', 'Missing')).toBe(0);
    expect(dispositionableBase(line, receipts, 'Damaged')).toBe(2); // 6 posted - 4 done
    expect(dispositionableBase(line, receipts, 'Missing')).toBe(0); // unposted missing cannot be resolved
  });

  const row = (over: Partial<DispositionRow> = {}): DispositionRow => ({
    lineId: 'l1', bucket: 'Damaged', unitCode: 'box', factor: 10, decimalPlaces: 0, openBase: 30, action: 'WriteOff', quantity: null, ...over,
  });

  it('requires a reason and at least one quantity', () => {
    expect(validateDisposition([row()], '').form).toBe('Enter a reason.');
    expect(validateDisposition([row()], 'why').form).toBe('Enter at least one quantity.');
    expect(validateDisposition([row({ quantity: 1 })], 'why')).toEqual({ rows: {}, form: null });
    expect(validateDisposition([], 'why').form).toMatch(/Nothing/);
  });

  it('quantity is in the line unit: at most the open bucket quantity (30 pc = 3 box)', () => {
    expect(validateDisposition([row({ quantity: 3 })], 'why').rows).toEqual({});
    expect(validateDisposition([row({ quantity: 4 })], 'why').rows[0]).toMatch(/More than the 3 box damaged/);
  });

  it('honours the unit decimals and rejects negatives', () => {
    expect(validateDisposition([row({ quantity: 1.5 })], 'why').rows[0]).toBe('Whole numbers only.');
    expect(validateDisposition([row({ quantity: 1.25, decimalPlaces: 2, factor: 1, openBase: 5 })], 'why').rows).toEqual({});
    expect(validateDisposition([row({ quantity: 1.255, decimalPlaces: 2, factor: 1, openBase: 5 })], 'why').rows[0]).toBe('At most 2 decimal places.');
    expect(validateDisposition([row({ quantity: -1 })], 'why').rows[0]).toMatch(/negative/);
  });

  it('builds only rows with a quantity and maps server line errors back to dialog rows', () => {
    const rows = [row({ bucket: 'Damaged' }), row({ bucket: 'Missing', quantity: 2, action: 'ReturnToSource' })];
    expect(buildDispositionRequest(rows, ' broken ')).toEqual({ reason: 'broken', lines: [{ consignmentLineId: 'l1', bucket: 'Missing', action: 'ReturnToSource', quantity: 2 }] });
    const mapped = mapDispositionProblem({ title: 't', status: 400, errors: { 'lines[0].quantity': ['Only 1 pc is posted.'] } } as never, rows);
    expect(mapped.rows[1]).toBe('Only 1 pc is posted.');
  });

  it('Resolve needs StockTransfers.Resolve and an open discrepancy', () => {
    expect(canResolve(true, perms('StockTransfers.Resolve'))).toBe(true);
    expect(canResolve(true, perms('StockTransfers.Receive'))).toBe(false);
    expect(canResolve(false, perms('StockTransfers.Resolve'))).toBe(false);
  });
});
