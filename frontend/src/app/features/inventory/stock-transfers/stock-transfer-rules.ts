import type { ApiProblem } from '../../../core/auth/auth.models';
import { problemMessage } from '../../../core/http/api-errors';
import { IdempotentSubmission } from '../../../core/idempotency';
import { convert, formatQuantity, hasAtMostDecimals, round10 } from '../../../shared/units/unit-conversion';
import { STOCK_TRANSFER_STATUS_LABELS } from './stock-transfers.models';
import type {
  ReceiveStockTransferRequest,
  ResolveStockTransferRequest,
  StockTransferLine,
  StockTransferResolution,
  StockTransferStatus,
} from './stock-transfers.models';

/** Phase 9 D2 state machine + D8 permissions, as the UI needs them (the server stays authoritative). */
export type TransferAction = 'edit' | 'submit' | 'approve' | 'reject' | 'cancel' | 'receive' | 'resolve' | 'close';

const ACTION_PERMISSION: Record<TransferAction, string> = {
  edit: 'StockTransfers.Create',
  submit: 'StockTransfers.Create',
  approve: 'StockTransfers.Approve',
  reject: 'StockTransfers.Approve',
  cancel: 'StockTransfers.Cancel',
  receive: 'StockTransfers.Receive',
  resolve: 'StockTransfers.Resolve',
  close: 'StockTransfers.Approve',
};

const ACTION_STATUSES: Record<TransferAction, readonly StockTransferStatus[]> = {
  edit: ['Draft'],
  submit: ['Draft'],
  approve: ['Requested'],
  reject: ['Requested'],
  cancel: ['Draft', 'Requested', 'Approved'],
  receive: ['Dispatched', 'PartiallyReceived'],
  resolve: ['Dispatched', 'PartiallyReceived'],
  close: ['Approved', 'PartiallyFulfilled'],
};

export const ACTION_ORDER: readonly TransferAction[] = ['edit', 'submit', 'approve', 'reject', 'receive', 'resolve', 'close', 'cancel'];

export function hasOutstanding(lines: readonly Pick<StockTransferLine, 'outstandingBaseQty'>[]): boolean {
  return lines.some((l) => l.outstandingBaseQty > 0);
}

/** Decisions taken by the source branch (FI-D26): offered only where the server says the active branch fits the source. */
const SOURCE_DECISIONS: readonly TransferAction[] = ['approve', 'reject', 'close'];

/**
 * Actions to offer: permission AND status (receive / resolve additionally need goods still in transit); approve / reject / close also
 * need `canApprove` from the server (absent = not yet known, treated as allowed so older payloads keep working; the server re-checks).
 */
export function availableActions(
  transfer: { status: StockTransferStatus; canApprove?: boolean; lines: readonly Pick<StockTransferLine, 'outstandingBaseQty' | 'dispatchedBaseQty'>[] },
  hasPermission: (code: string) => boolean,
): TransferAction[] {
  return ACTION_ORDER.filter((a) => {
    if (!ACTION_STATUSES[a].includes(transfer.status)) return false;
    if (!hasPermission(ACTION_PERMISSION[a])) return false;
    if (SOURCE_DECISIONS.includes(a) && transfer.canApprove === false) return false;
    if ((a === 'receive' || a === 'resolve') && !hasOutstanding(transfer.lines)) return false;
    // Close remaining: PartiallyFulfilled, or Approved with something already dispatched (the server checks allocations).
    if (a === 'close' && transfer.status === 'Approved' && !transfer.lines.some((l) => l.dispatchedBaseQty > 0)) return false;
    return true;
  });
}

export function actionPermission(action: TransferAction): string {
  return ACTION_PERMISSION[action];
}

export function statusLabel(status: StockTransferStatus): string {
  return STOCK_TRANSFER_STATUS_LABELS[status] ?? status;
}

/** Inverse of statusLabel for the list filter dropdown; null when the text is not a status label. */
export function statusFromLabel(label: string): StockTransferStatus | null {
  const hit = (Object.entries(STOCK_TRANSFER_STATUS_LABELS) as [StockTransferStatus, string][]).find(([, l]) => l === label);
  return hit ? hit[0] : null;
}

export const CLOSE_REASON_MAX = 500;

export function validateCloseReason(reason: string | null | undefined): string | null {
  const text = (reason ?? '').trim();
  if (!text) return 'A reason is required.';
  if (text.length > CLOSE_REASON_MAX) return `Reason can be at most ${CLOSE_REASON_MAX} characters.`;
  return null;
}

/** Server 400 on close (keys `reason`, `status`) -> one message; null when the problem has no field errors. */
export function closeProblemMessage(problem: ApiProblem | null | undefined): string | null {
  const errors = problem?.errors ?? {};
  const msgs = ['reason', 'status'].flatMap((k) => Object.entries(errors).filter(([f]) => f.toLowerCase() === k).flatMap(([, m]) => m));
  return msgs.length > 0 ? msgs.join(' ') : null;
}

export function statusTone(status: StockTransferStatus): 'good' | 'warn' | 'bad' | 'info' | 'mute' {
  switch (status) {
    case 'Completed':
    case 'Fulfilled':
      return 'good';
    case 'Rejected':
    case 'Cancelled':
      return 'bad';
    case 'Dispatched':
    case 'PartiallyReceived':
    case 'PartiallyFulfilled':
    case 'Closed':
      return 'warn';
    case 'Requested':
    case 'Approved':
      return 'info';
    default:
      return 'mute';
  }
}

/** Label of a transfer endpoint: "Name (CODE)". */
export function endpointLabel(e: { code: string | null; name: string | null } | null | undefined): string {
  if (!e) return '—';
  if (e.name && e.code) return `${e.name} (${e.code})`;
  return e.name || e.code || '—';
}

// ---- Idempotency ----

export { IdempotentSubmission };

// ---- Shared row maths ----

export interface UnitRef {
  id: string;
  unitCode: string;
  displayName: string;
  decimalPlaces: number;
  conversionFactorToBase: number;
}

const isNum = (v: number | null | undefined): v is number => typeof v === 'number' && Number.isFinite(v);

/** Quantity of `baseQty` expressed in a unit with the given factor, rounded to the unit's decimals; null when not exactly representable. */
export function baseToUnitQty(baseQty: number, unit: Pick<UnitRef, 'decimalPlaces' | 'conversionFactorToBase'>): number | null {
  const qty = round10(convert(baseQty, 1, unit.conversionFactorToBase));
  return hasAtMostDecimals(qty, unit.decimalPlaces) ? qty : null;
}

/** Unit a line was dispatched in (falls back to the request unit before dispatch). */
export function lineUnitId(line: StockTransferLine): string {
  return line.dispatchProductUnitId ?? line.productUnitId;
}

export function formatBase(line: Pick<StockTransferLine, 'baseDecimalPlaces'>, qty: number): string {
  return formatQuantity(qty, line.baseDecimalPlaces ?? 3);
}

/** Zero-or-more quantity entered in `unit` (0 allowed): whole-number / decimal-place checks follow the unit. */
function qtyError(qty: number | null, unit: UnitRef | null, emptyMessage: string): string | null {
  if (!unit) return 'Select a unit.';
  if (!isNum(qty)) return emptyMessage;
  if (qty < 0) return 'Quantity cannot be negative.';
  if (!hasAtMostDecimals(qty, unit.decimalPlaces)) {
    return unit.decimalPlaces === 0 ? `${unit.displayName} takes whole numbers only.` : `${unit.displayName} allows at most ${unit.decimalPlaces} decimal places.`;
  }
  return null;
}

export interface RowErrors {
  /** Per-row messages keyed by row index. */
  rows: Record<number, string>;
  /** Whole-form message, or null. */
  form: string | null;
}

export const hasErrors = (e: RowErrors): boolean => Object.keys(e.rows).length > 0 || e.form !== null;

// ---- Receive ----

export interface ReceiveRow {
  lineId: string;
  unit: UnitRef | null;
  accepted: number | null;
  damaged: number | null;
  outstandingBaseQty: number;
}

/** accepted + damaged <= outstanding (base); excess refused; partial allowed; something must be received. */
export function validateReceive(rows: readonly ReceiveRow[]): RowErrors {
  const errors: RowErrors = { rows: {}, form: null };
  rows.forEach((r, i) => {
    const accepted = r.accepted ?? 0;
    const damaged = r.damaged ?? 0;
    const e = qtyError(r.accepted ?? 0, r.unit, '') ?? qtyError(r.damaged ?? 0, r.unit, '');
    if (e) errors.rows[i] = e;
    else if (r.unit && round10((accepted + damaged) * r.unit.conversionFactorToBase) > r.outstandingBaseQty) {
      errors.rows[i] = 'Accepted + damaged exceeds the outstanding quantity.';
    }
  });
  if (!hasErrors(errors) && !rows.some((r) => (r.accepted ?? 0) > 0 || (r.damaged ?? 0) > 0)) errors.form = 'Enter an accepted or damaged quantity on at least one line.';
  return errors;
}

export function buildReceiveRequest(rows: readonly ReceiveRow[], notes: string | null): ReceiveStockTransferRequest {
  return {
    notes: notes?.trim() ? notes.trim() : null,
    lines: rows.map((r) => ({ lineId: r.lineId, productUnitId: r.unit!.id, acceptedQuantity: r.accepted ?? 0, damagedQuantity: r.damaged ?? 0 })),
  };
}

// ---- Resolve ----

export interface ResolveRow {
  lineId: string;
  action: StockTransferResolution | null;
}

export const RESOLVE_REASON_MAX = 300;

export function validateResolve(rows: readonly ResolveRow[], reason: string | null | undefined): RowErrors {
  const errors: RowErrors = { rows: {}, form: null };
  rows.forEach((r, i) => {
    if (!r.action) errors.rows[i] = 'Choose write-off or return to source.';
  });
  const text = (reason ?? '').trim();
  if (!text) errors.form = 'A reason is required.';
  else if (text.length > RESOLVE_REASON_MAX) errors.form = `Reason can be at most ${RESOLVE_REASON_MAX} characters.`;
  return errors;
}

export function buildResolveRequest(rows: readonly ResolveRow[], reason: string): ResolveStockTransferRequest {
  return { reason: reason.trim(), lines: rows.map((r) => ({ lineId: r.lineId, action: r.action! })) };
}

// ---- Server problem -> row errors ----

/**
 * Maps a ProblemDetails onto dialog rows: `lines[i].*` field errors -> row i (request order = row order); anything else
 * (`status`, `IdempotencyKey`, `reason`, unmatched) becomes the form banner.
 */
export function mapRowProblem(problem: ApiProblem | null | undefined): RowErrors {
  const out: RowErrors = { rows: {}, form: null };
  const entries = Object.entries(problem?.errors ?? {});
  if (entries.length === 0) {
    out.form = problemMessage(problem);
    return out;
  }
  const banner: string[] = [];
  for (const [field, messages] of entries) {
    const m = /lines\[(\d+)\]/i.exec(field);
    const text = messages.join(' ');
    if (m) {
      const i = Number(m[1]);
      out.rows[i] = out.rows[i] ? `${out.rows[i]} ${text}` : text;
    } else banner.push(text);
  }
  if (banner.length > 0) out.form = banner.join(' ');
  return out;
}
