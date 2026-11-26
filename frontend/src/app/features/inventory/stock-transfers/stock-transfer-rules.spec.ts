import { HttpErrorResponse } from '@angular/common/http';
import {
  IdempotentSubmission, availableActions, buildApproveRequest, validateApprove, closeProblemMessage, statusFromLabel, statusLabel, validateCloseReason, buildReceiveRequest, buildResolveRequest, mapRowProblem,
  validateReceive, validateResolve, type ReceiveRow, type UnitRef,
} from './stock-transfer-rules';
import { STOCK_TRANSFER_STATUSES } from './stock-transfers.models';
import { dispatchedLine, line } from './test-fixtures';

const box: UnitRef = { id: 'box', unitCode: 'box', displayName: 'Box', decimalPlaces: 0, conversionFactorToBase: 10 };
const pc: UnitRef = { id: 'pc', unitCode: 'pc', displayName: 'Piece', decimalPlaces: 0, conversionFactorToBase: 1 };
const kg: UnitRef = { id: 'kg', unitCode: 'kg', displayName: 'Kg', decimalPlaces: 3, conversionFactorToBase: 1 };
const ALL = ['StockTransfers.View', 'StockTransfers.Create', 'StockTransfers.Approve', 'StockTransfers.Dispatch', 'StockTransfers.Receive', 'StockTransfers.Cancel', 'StockTransfers.Resolve'];
const has = (perms: string[]) => (c: string) => perms.includes(c);

describe('availableActions (status x permission)', () => {
  const lines = [dispatchedLine()];
  const expected: Record<string, string[]> = {
    Draft: ['edit', 'submit', 'cancel'],
    Requested: ['approve', 'reject', 'cancel'],
    Approved: ['cancel', 'close'],
    PartiallyFulfilled: ['close'],
    Fulfilled: [],
    Closed: [],
    Rejected: [],
    Dispatched: ['receive', 'resolve'],
    PartiallyReceived: ['receive', 'resolve'],
    Completed: [],
    Cancelled: [],
  };

  for (const status of STOCK_TRANSFER_STATUSES) {
    it(`${status}: full permissions offer ${expected[status].join(', ') || 'nothing'}`, () => {
      expect([...availableActions({ status, lines }, has(ALL))].sort()).toEqual([...expected[status]].sort());
    });
  }

  it('hides every action without its permission', () => {
    expect(availableActions({ status: 'Requested', lines }, has(['StockTransfers.View']))).toEqual([]);
    expect(availableActions({ status: 'Requested', lines }, has(['StockTransfers.Approve']))).toEqual(['approve', 'reject']);
    expect(availableActions({ status: 'Approved', lines }, has(['StockTransfers.Cancel']))).toEqual(['cancel']);
    expect(availableActions({ status: 'Dispatched', lines }, has(['StockTransfers.Receive']))).toEqual(['receive']);
  });

  it('offers approve / reject / close only where the server says the active branch fits the source', () => {
    const approver = has(['StockTransfers.Approve', 'StockTransfers.Cancel']);
    expect(availableActions({ status: 'Requested', canApprove: false, lines }, approver)).toEqual(['cancel']);
    expect(availableActions({ status: 'Requested', canApprove: true, lines }, approver)).toEqual(['approve', 'reject', 'cancel']);
    const dispatched = [{ outstandingBaseQty: 0, dispatchedBaseQty: 1 }];
    expect(availableActions({ status: 'PartiallyFulfilled', canApprove: false, lines: dispatched }, approver)).not.toContain('close');
    expect(availableActions({ status: 'PartiallyFulfilled', canApprove: true, lines: dispatched }, approver)).toContain('close');
  });

  it('resolve is only offered with StockTransfers.Resolve (company-level)', () => {
    expect(availableActions({ status: 'Dispatched', lines }, has(['StockTransfers.Receive', 'StockTransfers.Dispatch']))).not.toContain('resolve');
    expect(availableActions({ status: 'Dispatched', lines }, has(['StockTransfers.Resolve']))).toEqual(['resolve']);
  });

  it('receive / resolve need goods still in transit', () => {
    expect(availableActions({ status: 'PartiallyReceived', lines: [dispatchedLine({ outstandingBaseQty: 0 })] }, has(ALL))).toEqual([]);
  });
});

describe('validateReceive', () => {
  const row = (over: Partial<ReceiveRow> = {}): ReceiveRow => ({ lineId: 'l1', unit: box, accepted: 3, damaged: 1, outstandingBaseQty: 50, ...over });

  it('allows a partial receipt (accepted + damaged <= outstanding)', () => {
    expect(validateReceive([row()])).toEqual({ rows: {}, form: null });
    expect(buildReceiveRequest([row()], '  short pallet ')).toEqual({
      notes: 'short pallet',
      lines: [{ lineId: 'l1', productUnitId: 'box', acceptedQuantity: 3, damagedQuantity: 1 }],
    });
  });

  it('refuses excess, measured in base units', () => {
    expect(validateReceive([row({ accepted: 5, damaged: 1 })]).rows[0]).toMatch(/exceeds the outstanding/i);
    expect(validateReceive([row({ unit: pc, accepted: 40, damaged: 10 })]).rows).toEqual({});
    expect(validateReceive([row({ unit: pc, accepted: 40, damaged: 11 })]).rows[0]).toMatch(/exceeds/i);
  });

  it('needs something received, and sends 0 for blank fields', () => {
    expect(validateReceive([row({ accepted: 0, damaged: 0 })]).form).toMatch(/at least one line/i);
    expect(buildReceiveRequest([row({ accepted: null, damaged: null })], null).lines[0]).toMatchObject({ acceptedQuantity: 0, damagedQuantity: 0 });
    expect(buildReceiveRequest([row()], '   ').notes).toBeNull();
  });
});

describe('validateResolve', () => {
  it('needs an action per line and a reason', () => {
    const e = validateResolve([{ lineId: 'l1', action: null }, { lineId: 'l2', action: 'WriteOff' }], '  ');
    expect(e.rows[0]).toBeDefined();
    expect(e.rows[1]).toBeUndefined();
    expect(e.form).toMatch(/reason is required/i);
  });

  it('caps the reason at 300 characters and builds the body', () => {
    expect(validateResolve([], 'x'.repeat(301)).form).toMatch(/300/);
    expect(validateResolve([{ lineId: 'l1', action: 'ReturnToSource' }], 'ok').form).toBeNull();
    expect(buildResolveRequest([{ lineId: 'l1', action: 'ReturnToSource' }, { lineId: 'l2', action: 'WriteOff' }], ' broken '))
      .toEqual({ reason: 'broken', lines: [{ lineId: 'l1', action: 'ReturnToSource' }, { lineId: 'l2', action: 'WriteOff' }] });
  });
});

describe('IdempotentSubmission', () => {
  const http = (status: number) => new HttpErrorResponse({ status });

  it('keeps the key across network / 5xx / conflict retries', () => {
    const s = new IdempotentSubmission();
    const key = s.key;
    s.failed(http(0));
    s.failed(http(503));
    s.failed(http(409));
    expect(s.key).toBe(key);
  });

  it('rotates after a definitive rejection and after success', () => {
    const s = new IdempotentSubmission();
    let key = s.key;
    s.failed(http(400));
    expect(s.key).not.toBe(key);
    key = s.key;
    s.succeeded();
    expect(s.key).not.toBe(key);
  });
});

describe('IdempotentSubmission.keyFor (body fingerprint)', () => {
  it('reuses the key for an identical body and rotates when the body changes', () => {
    const s = new IdempotentSubmission();
    const k1 = s.keyFor({ a: 1, lines: [{ q: 2 }] });
    s.failed(new HttpErrorResponse({ status: 0 }));
    expect(s.keyFor({ a: 1, lines: [{ q: 2 }] })).toBe(k1);
    const k2 = s.keyFor({ a: 1, lines: [{ q: 3 }] });
    expect(k2).not.toBe(k1);
    expect(s.keyFor({ a: 1, lines: [{ q: 3 }] })).toBe(k2);
  });

  it('starts fresh after success and after a definitive rejection', () => {
    const s = new IdempotentSubmission();
    const k1 = s.keyFor({ a: 1 });
    s.succeeded();
    const k2 = s.keyFor({ a: 1 });
    expect(k2).not.toBe(k1);
    s.failed(new HttpErrorResponse({ status: 422 }));
    expect(s.keyFor({ a: 1 })).not.toBe(k2);
  });

  it('tracks an ambiguous failure until acknowledged; definitive errors and success clear it', () => {
    const s = new IdempotentSubmission();
    s.failed(new HttpErrorResponse({ status: 503 }));
    expect(s.hasAmbiguousFailure).toBe(true);
    s.acknowledged();
    expect(s.hasAmbiguousFailure).toBe(false);
    s.failed(new HttpErrorResponse({ status: 0 }));
    s.failed(new HttpErrorResponse({ status: 400 }));
    expect(s.hasAmbiguousFailure).toBe(false);
  });
});

describe('mapRowProblem', () => {
  it('maps lines[i] errors to rows and the rest to the banner', () => {
    const out = mapRowProblem({ status: 400, errors: { 'lines[1].quantity': ['Too many.'], 'Lines[1].ProductUnitId': ['Bad unit.'], status: ['Must be Approved.'] } } as never);
    expect(out.rows[1]).toBe('Too many. Bad unit.');
    expect(out.form).toBe('Must be Approved.');
  });

  it('falls back to the problem message when there are no field errors', () => {
    expect(mapRowProblem({ status: 400, title: 'Nope' } as never).form).toBe('Nope');
  });
});

describe('fixtures', () => {
  it('line() has no outstanding by default', () => {
    expect(line().outstandingBaseQty).toBe(0);
  });
});

describe('Phase 03 status labels and close rule', () => {
  it('maps every status to its display label', () => {
    expect(statusLabel('Approved')).toBe('Ready for shipment');
    expect(statusLabel('PartiallyFulfilled')).toBe('Partially fulfilled');
    expect(statusLabel('Fulfilled')).toBe('Fulfilled');
    expect(statusLabel('Closed')).toBe('Closed (short)');
    expect(statusLabel('Dispatched')).toBe('Dispatched');
    expect(statusLabel('PartiallyReceived')).toBe('PartiallyReceived');
    expect(statusLabel('Completed')).toBe('Completed');
    expect(STOCK_TRANSFER_STATUSES.map((s) => statusFromLabel(statusLabel(s)))).toEqual(STOCK_TRANSFER_STATUSES);
  });

  it('validates the close reason (required, <= 500)', () => {
    expect(validateCloseReason('  ')).toBe('A reason is required.');
    expect(validateCloseReason('x'.repeat(501))).toContain('500');
    expect(validateCloseReason('ok')).toBeNull();
  });

  it('close needs StockTransfers.Approve', () => {
    const t = { status: 'PartiallyFulfilled' as const, lines: [{ outstandingBaseQty: 0, dispatchedBaseQty: 5 }] };
    expect(availableActions(t, () => true)).toContain('close');
    expect(availableActions(t, (c) => c !== 'StockTransfers.Approve')).not.toContain('close');
  });

  it('closeProblemMessage joins reason and status errors', () => {
    expect(closeProblemMessage({ errors: { reason: ['a'], status: ['b'] } } as never)).toBe('a b');
    expect(closeProblemMessage({} as never)).toBeNull();
  });
});

describe('approve controls', () => {
  const has = (codes: string[]) => (c: string) => codes.includes(c);
  const lines = [{ outstandingBaseQty: 0, dispatchedBaseQty: 0 }];

  it('hides approve (not reject) from the maker under approval separation', () => {
    const approver = has(['StockTransfers.Approve']);
    expect(availableActions({ status: 'Requested', canApprove: true, approvalBlockedForYou: true, lines }, approver)).toEqual(['reject']);
    expect(availableActions({ status: 'Requested', canApprove: true, approvalBlockedForYou: false, lines }, approver)).toEqual(['approve', 'reject']);
  });

  const row = (quantity: string, requestedQty = 3, lineId = 'l1') => ({ lineId, requestedQty, quantity });

  it('accepts approving as requested without a reason', () => {
    expect(validateApprove([row('3')], '')).toEqual({ rows: {}, form: null });
    expect(buildApproveRequest([row('3')], 'ignored')).toEqual({ lines: [{ lineId: 'l1', quantity: 3 }], reason: null });
  });

  it('flags row quantities that are empty, negative, non-numeric or above the request', () => {
    expect(validateApprove([row(''), row('-1'), row('abc'), row('4')], 'r').rows).toEqual({
      0: 'Enter a quantity of 0 or more.', 1: 'Enter a quantity of 0 or more.', 2: 'Enter a quantity of 0 or more.', 3: 'At most the requested 3.',
    });
  });

  it('needs a reason when reduced, and refuses every line at 0', () => {
    expect(validateApprove([row('1')], ' ').form).toBe('Give a reason for approving less than requested.');
    expect(validateApprove([row('0'), row('0', 2, 'l2')], 'r').form).toBe('Every line is at 0 — reject the request instead.');
    expect(validateApprove([row('0'), row('2', 2, 'l2')], 'out of stock').form).toBeNull();
  });

  it('sends every line in row order with the trimmed reason when something is reduced', () => {
    expect(buildApproveRequest([row('1'), row('2', 2, 'l2')], '  only one  ')).toEqual({
      lines: [{ lineId: 'l1', quantity: 1 }, { lineId: 'l2', quantity: 2 }], reason: 'only one',
    });
  });
});
