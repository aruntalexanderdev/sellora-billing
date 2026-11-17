import type { ApiProblem } from '../../../core/auth/auth.models';
import { problemMessage } from '../../../core/http/api-errors';
import { convert, formatQuantity, hasAtMostDecimals, round10 } from '../../../shared/units/unit-conversion';
import type { BadgeTone } from '../../../shared/ui/badge/badge';
import type {
  Consignment, ConsignmentDetailLine, ConsignmentLine, ConsignmentStatus, LoadConsignmentRequest, RecordReceiptRequest, ShipmentStatus, StockReservationStatus, TransportMode, TransportVia,
} from './shipments.models';

// ---- Labels ----

export const SHIPMENT_STATUS_LABELS: Record<ShipmentStatus, string> = {
  Draft: 'Draft',
  Loading: 'Loading',
  ReadyToDispatch: 'Ready to dispatch',
  Dispatched: 'Dispatched',
  PartiallyDelivered: 'Partially delivered',
  Delivered: 'Delivered',
  Cancelled: 'Cancelled',
};

export const TRANSPORT_MODE_LABELS: Record<TransportMode, string> = {
  Road: 'Road', Air: 'Air', Sea: 'Sea', Rail: 'Rail', Courier: 'Courier', HandCarry: 'Hand carry',
};

export const TRANSPORT_VIA_LABELS: Record<TransportVia, string> = { OwnFleet: 'Own fleet', ExternalProvider: 'External provider' };

/** FI-D29 per-mode label of the transport reference. */
export const REFERENCE_LABELS: Record<TransportMode, string> = {
  Road: 'Transport reference (optional)',
  Air: 'AWB number',
  Sea: 'Bill of lading',
  Rail: 'Railway receipt',
  Courier: 'Tracking number',
  HandCarry: 'Carrier name',
};

export const REFERENCE_MAX = 100;
export const REASON_MAX = 300;

export const shipmentStatusLabel = (s: ShipmentStatus): string => SHIPMENT_STATUS_LABELS[s] ?? s;
export const modeLabel = (m: TransportMode): string => TRANSPORT_MODE_LABELS[m] ?? m;
export const referenceLabel = (m: TransportMode): string => REFERENCE_LABELS[m] ?? 'Transport reference';

export function shipmentStatusTone(s: ShipmentStatus): BadgeTone {
  switch (s) {
    case 'ReadyToDispatch': case 'Delivered': return 'good';
    case 'Loading': case 'PartiallyDelivered': return 'warn';
    case 'Dispatched': return 'info';
    case 'Cancelled': return 'bad';
    default: return 'mute';
  }
}

export function consignmentStatusTone(s: ConsignmentStatus): BadgeTone {
  switch (s) {
    case 'Loaded': case 'Received': return 'good';
    case 'PartiallyReceived': return 'warn';
    case 'Cancelled': return 'bad';
    default: return 'info';
  }
}

export function reservationStatusTone(s: StockReservationStatus): BadgeTone {
  return s === 'Active' ? 'info' : s === 'Consumed' ? 'good' : 'mute';
}

// ---- Header validation (mirrors NormalizeHeader on the server) ----

const REFERENCE_REQUIRED_MESSAGES: Record<TransportMode, string> = {
  Road: '',
  Air: 'Enter the air waybill (AWB) number.',
  Sea: 'Enter the bill of lading number.',
  Rail: 'Enter the railway receipt number.',
  Courier: 'Enter the courier tracking number.',
  HandCarry: 'Enter the name of the person carrying the goods.',
};

export const referenceRequired = (mode: TransportMode): boolean => mode !== 'Road';

export function validateReference(mode: TransportMode, reference: string | null | undefined): string | null {
  const text = (reference ?? '').trim();
  if (!text) return referenceRequired(mode) ? REFERENCE_REQUIRED_MESSAGES[mode] : null;
  if (text.length > REFERENCE_MAX) return `Use at most ${REFERENCE_MAX} characters.`;
  return null;
}

export interface HeaderValue {
  mode: TransportMode;
  via: TransportVia;
  providerName: string | null;
  reference: string | null;
}

export type HeaderErrors = Partial<Record<'transportReference' | 'providerName', string>>;

export function validateHeader(v: HeaderValue): HeaderErrors {
  const errors: HeaderErrors = {};
  const ref = validateReference(v.mode, v.reference);
  if (ref) errors.transportReference = ref;
  if (v.via === 'ExternalProvider' && !(v.providerName ?? '').trim()) errors.providerName = "Enter the transport provider's name.";
  return errors;
}

export function isValidReason(reason: string | null | undefined): boolean {
  const t = (reason ?? '').trim();
  return t.length > 0 && t.length <= REASON_MAX;
}

// ---- Status-based enablement (the server stays authoritative) ----

export interface ShipmentCapabilities {
  /** Header, vehicles, allocations and vehicle legs: Draft / Loading with Shipments.Plan. */
  edit: boolean;
  /** Load / unload: Draft / Loading / ReadyToDispatch with Shipments.Load. */
  load: boolean;
  /** Cancel the shipment or a consignment: Draft / Loading / ReadyToDispatch with Shipments.Plan. */
  cancel: boolean;
  /** Dispatch: ReadyToDispatch only, with StockTransfers.Dispatch. Cancel/edit/load vanish once dispatched. */
  dispatch: boolean;
}

export function shipmentCapabilities(status: ShipmentStatus, hasPermission: (code: string) => boolean): ShipmentCapabilities {
  const open = status === 'Draft' || status === 'Loading';
  const live = open || status === 'ReadyToDispatch';
  return {
    edit: open && hasPermission('Shipments.Plan'),
    load: live && hasPermission('Shipments.Load'),
    cancel: live && hasPermission('Shipments.Plan'),
    dispatch: status === 'ReadyToDispatch' && hasPermission('StockTransfers.Dispatch'),
  };
}

export const liveConsignments = (consignments: readonly Consignment[]): Consignment[] =>
  consignments.filter((c) => c.status !== 'Cancelled');

export const activeLines = (c: Consignment): ConsignmentLine[] => c.lines.filter((l) => l.status === 'Active');

/** Own-fleet consignments need a vehicle leg before dispatch (FI-D29). */
export function missingLegs(via: TransportVia, mode: TransportMode, consignments: readonly Consignment[]): Consignment[] {
  if (via !== 'OwnFleet' || mode !== 'Road') return [];
  return liveConsignments(consignments).filter((c) => !c.shipmentVehicleId && activeLines(c).length > 0);
}

// ---- Allocation maths (all conversion goes through unit-conversion.ts) ----

export interface AllocUnit {
  id: string;
  unitCode: string;
  displayName: string;
  decimalPlaces: number;
  conversionFactorToBase: number;
}

export interface AllocationLimits {
  /** Base units still allocatable on the request line (requested - closed - allocated). */
  remainingBase: number;
  /** On hand at the source, base units; null when unknown. */
  availableBase: number | null;
}

/** The binding limit in base units: the smaller of remaining and available. */
export function maxAllocatableBase(l: AllocationLimits): number {
  return Math.max(0, l.availableBase === null ? l.remainingBase : Math.min(l.remainingBase, l.availableBase));
}

/** A base quantity expressed in `unit`, rounded to its decimals; null when it is not exactly representable. */
export function toUnitQty(baseQty: number, unit: Pick<AllocUnit, 'decimalPlaces' | 'conversionFactorToBase'>): number | null {
  const qty = convert(baseQty, 1, unit.conversionFactorToBase);
  return hasAtMostDecimals(qty, unit.decimalPlaces) ? qty : null;
}

/** Largest whole-precision quantity of `unit` that fits within `baseQty` (floored to the unit's decimals). */
export function floorUnitQty(baseQty: number, unit: Pick<AllocUnit, 'decimalPlaces' | 'conversionFactorToBase'>): number {
  const scale = 10 ** unit.decimalPlaces;
  return Math.floor(round10(convert(baseQty, 1, unit.conversionFactorToBase) * scale)) / scale;
}

export function baseOf(qty: number, unit: Pick<AllocUnit, 'conversionFactorToBase'>): number {
  return round10(qty * unit.conversionFactorToBase);
}

/** Validation of a quantity entered to allocate (a positive add) in a unit against the limits. */
export function validateAllocation(qty: number | null, unit: AllocUnit | null, limits: AllocationLimits): string | null {
  if (!unit) return 'Select a unit.';
  if (qty === null || !Number.isFinite(qty) || qty <= 0) return 'Enter a quantity greater than zero.';
  if (!hasAtMostDecimals(qty, unit.decimalPlaces)) {
    return unit.decimalPlaces === 0 ? `${unit.displayName} takes whole numbers only.` : `${unit.displayName} allows at most ${unit.decimalPlaces} decimal places.`;
  }
  const base = baseOf(qty, unit);
  if (base > limits.remainingBase) return `Only ${formatQuantity(limits.remainingBase, 3)} remains to be allocated on this request line (base units).`;
  if (limits.availableBase !== null && base > limits.availableBase) return `Only ${formatQuantity(Math.max(0, limits.availableBase), 3)} is available at the source (base units).`;
  return null;
}

/** New TOTAL for an allocation (unit fixed, 0 = remove): must respect decimals, loaded qty and the extra the limits allow. */
export function validateAllocationTotal(total: number | null, line: ConsignmentLine, decimalPlaces: number, extraAllowedBase: number): string | null {
  if (total === null || !Number.isFinite(total) || total < 0) return 'Enter 0 or more.';
  if (!hasAtMostDecimals(total, decimalPlaces)) return decimalPlaces === 0 ? 'Whole numbers only.' : `At most ${decimalPlaces} decimal places.`;
  const base = round10(total * line.conversionFactorToBase);
  if (total > 0 && base < line.loadedBaseQty) return `${formatQuantity(line.loadedQty, 0)} ${line.unitCode} is already loaded; unload before reducing below it.`;
  if (total === 0 && line.loadedBaseQty > 0) return 'Unload this line before removing the allocation.';
  if (base - line.allocatedBaseQty > extraAllowedBase) return 'More than the remaining or available quantity allows.';
  return null;
}

// ---- Load dialog ----

export interface LoadRow {
  lineId: string;
  allocatedQty: number;
  loadedQty: number | null;
  decimalPlaces: number;
}

export interface LoadErrors {
  rows: Record<number, string>;
  form: string | null;
}

/** Loaded quantity per line: 0 <= loaded <= allocated, within the unit's decimals. Lowering a previously loaded line is an unload. */
export function validateLoad(rows: readonly LoadRow[]): LoadErrors {
  const out: LoadErrors = { rows: {}, form: null };
  rows.forEach((r, i) => {
    if (r.loadedQty === null || !Number.isFinite(r.loadedQty)) out.rows[i] = 'Enter the loaded quantity (0 = nothing loaded).';
    else if (r.loadedQty < 0) out.rows[i] = 'Loaded quantity cannot be negative.';
    else if (!hasAtMostDecimals(r.loadedQty, r.decimalPlaces)) out.rows[i] = r.decimalPlaces === 0 ? 'Whole numbers only.' : `At most ${r.decimalPlaces} decimal places.`;
    else if (r.loadedQty > r.allocatedQty) out.rows[i] = `Cannot load more than was allocated (${formatQuantity(r.allocatedQty, 0)}).`;
  });
  if (rows.length === 0) out.form = 'This consignment has no active lines to load.';
  return out;
}

export const hasLoadErrors = (e: LoadErrors): boolean => Object.keys(e.rows).length > 0 || e.form !== null;

export function buildLoadRequest(rows: readonly LoadRow[]): LoadConsignmentRequest {
  return { lines: rows.map((r) => ({ consignmentLineId: r.lineId, loadedQuantity: r.loadedQty! })) };
}

/** Maps a load 400 (`lines[i].loadedQuantity`, `status`, ...) onto dialog rows / banner. */
export function mapLoadProblem(problem: ApiProblem | null | undefined): LoadErrors {
  const out: LoadErrors = { rows: {}, form: null };
  const entries = Object.entries(problem?.errors ?? {});
  if (entries.length === 0) {
    out.form = problemMessage(problem);
    return out;
  }
  const banner: string[] = [];
  for (const [field, messages] of entries) {
    const m = /lines\[(\d+)\]/i.exec(field);
    const text = messages.join(' ');
    if (m) out.rows[Number(m[1])] = text;
    else banner.push(text);
  }
  if (banner.length > 0) out.form = banner.join(' ');
  return out;
}

// ---- Server problem -> one message (planner actions) ----

/** Field keys the shipment API raises on planner actions (ShipmentService). */
const PLANNER_KEYS = [
  'status', 'transportReference', 'transportMode', 'transportVia', 'providerName', 'vehicleId', 'externalVehicleNumber', 'shipmentVehicleId',
  'stockTransferLineId', 'productUnitId', 'quantity', 'consignmentId', 'consignmentLineId', 'lines', 'reason', 'IdempotencyKey', 'notes', 'sourceLocationId',
];

/** All field messages of a 400 as one text (known keys first), or the generic problem message. */
export function plannerProblemMessage(problem: ApiProblem | null | undefined): string {
  const entries = Object.entries(problem?.errors ?? {});
  if (entries.length === 0) return problemMessage(problem);
  const rank = (f: string) => {
    const i = PLANNER_KEYS.findIndex((k) => k.toLowerCase() === f.toLowerCase().replace(/\[\d+\].*$/, ''));
    return i < 0 ? PLANNER_KEYS.length : i;
  };
  return [...entries].sort((a, b) => rank(a[0]) - rank(b[0])).flatMap(([, m]) => m).join(' ');
}

/** Server error for one control key (case-insensitive), joined; null if absent. */
export function problemFor(problem: ApiProblem | null | undefined, key: string): string | null {
  const hit = Object.entries(problem?.errors ?? {}).filter(([f]) => f.toLowerCase() === key.toLowerCase()).flatMap(([, m]) => m);
  return hit.length > 0 ? hit.join(' ') : null;
}

// ---- Vehicles ----

export interface VehicleLike {
  id: string;
  vehicleNumber: string;
  vehicleName: string;
  assignedBranchId: string | null;
  isActive: boolean;
}

/** Own-fleet picker: active vehicles that are central or assigned to the source branch, minus those already on the shipment. */
export function eligibleVehicles<T extends VehicleLike>(vehicles: readonly T[], sourceBranchId: string | null, onShipment: readonly (string | null)[]): T[] {
  return vehicles.filter((v) => v.isActive && (v.assignedBranchId === null || v.assignedBranchId === sourceBranchId) && !onShipment.includes(v.id));
}

export function vehicleLabel(v: { vehicleId: string | null; vehicleNumber: string | null; vehicleName: string | null; externalVehicleNumber: string | null; externalDescription: string | null }): string {
  if (v.vehicleId) return [v.vehicleNumber, v.vehicleName].filter(Boolean).join(' – ') || 'Vehicle';
  return [v.externalVehicleNumber, v.externalDescription].filter(Boolean).join(' – ') || 'External vehicle';
}

// ---- Request-line limits ----

/** Base units of a request line that can still be allocated: Requested - Closed - Allocated (allocated includes dispatched). */
export function requestRemainingBase(l: { requestedBaseQty: number; closedBaseQty: number; allocatedBaseQty: number }): number {
  return Math.max(0, round10(l.requestedBaseQty - l.closedBaseQty - l.allocatedBaseQty));
}

export interface LimitsView {
  requested: string;
  remaining: string;
  available: string;
}

/** Requested / remaining / available shown in the selected unit; falls back to base units when not exactly representable. */
export function limitsView(
  requestedBase: number, limits: AllocationLimits, unit: AllocUnit | null, baseUnitCode: string | null, baseDecimals = 3,
): LimitsView {
  const show = (base: number): string => {
    const asUnit = unit && unit.conversionFactorToBase !== 1 ? toUnitQty(base, unit) : null;
    const baseText = `${formatQuantity(base, baseDecimals)} ${baseUnitCode ?? ''}`.trim();
    return asUnit === null || !unit || unit.conversionFactorToBase === 1 ? baseText : `${formatQuantity(asUnit, unit.decimalPlaces)} ${unit.unitCode} (${baseText})`;
  };
  return {
    requested: show(requestedBase),
    remaining: show(limits.remainingBase),
    available: limits.availableBase === null ? 'unknown' : show(Math.max(0, limits.availableBase)),
  };
}

// ---- Receiving (Phase 05, FI-D35) ----

/** Receiving is possible while goods are in transit or partly received. */
export const isReceivable = (status: ConsignmentStatus): boolean => status === 'InTransit' || status === 'PartiallyReceived';

export function canReceive(status: ConsignmentStatus, hasPermission: (code: string) => boolean): boolean {
  return isReceivable(status) && hasPermission('StockTransfers.Receive');
}

/** Dispatched minus every quantity already accounted (accepted + damaged + rejected + missing), base units. */
export function remainingToAccountBase(l: Pick<ConsignmentDetailLine, 'dispatchedBaseQty' | 'acceptedBaseQty' | 'damagedBaseQty' | 'rejectedBaseQty' | 'missingBaseQty'>): number {
  return Math.max(0, round10(l.dispatchedBaseQty - l.acceptedBaseQty - l.damagedBaseQty - l.rejectedBaseQty - l.missingBaseQty));
}

/** Dispatched minus posted: still physically in Transit (includes damaged / rejected / missing awaiting Phase 06). */
export const inTransitBase = (l: Pick<ConsignmentDetailLine, 'dispatchedBaseQty' | 'postedBaseQty'>): number =>
  Math.max(0, round10(l.dispatchedBaseQty - l.postedBaseQty));

export type ReceiptField = 'accepted' | 'damaged' | 'rejected' | 'missing';
export const RECEIPT_FIELDS: readonly ReceiptField[] = ['accepted', 'damaged', 'rejected', 'missing'];

export interface ReceiptRow {
  lineId: string;
  unitCode: string;
  /** conversionFactorToBase of the consignment line. */
  factor: number;
  decimalPlaces: number;
  /** Base units that may still be accounted for on this line. */
  remainingBase: number;
  accepted: number | null;
  damaged: number | null;
  rejected: number | null;
  missing: number | null;
}

const rowTotal = (r: ReceiptRow): number => RECEIPT_FIELDS.reduce((sum, f) => sum + (r[f] ?? 0), 0);

/** Remaining quantity of the row expressed in the line's unit (for hints). */
export const remainingInUnit = (r: Pick<ReceiptRow, 'remainingBase' | 'factor'>): number => convert(r.remainingBase, 1, r.factor);

/** Each quantity >= 0 within the unit's decimals; accepted + damaged + rejected + missing <= remaining; at least one quantity overall. */
export function validateReceipt(rows: readonly ReceiptRow[]): LoadErrors {
  const out: LoadErrors = { rows: {}, form: null };
  rows.forEach((r, i) => {
    const values = RECEIPT_FIELDS.map((f) => r[f]);
    if (values.some((v) => v !== null && (!Number.isFinite(v) || v < 0))) out.rows[i] = 'Quantities cannot be negative.';
    else if (values.some((v) => v !== null && !hasAtMostDecimals(v, r.decimalPlaces))) out.rows[i] = r.decimalPlaces === 0 ? 'Whole numbers only.' : `At most ${r.decimalPlaces} decimal places.`;
    else if (baseOf(rowTotal(r), { conversionFactorToBase: r.factor }) > r.remainingBase) {
      out.rows[i] = `Accepted + damaged + rejected + missing is more than the ${formatQuantity(remainingInUnit(r), r.decimalPlaces)} ${r.unitCode} still to account for.`;
    }
  });
  if (rows.length === 0) out.form = 'Nothing is left to receive on this consignment.';
  else if (Object.keys(out.rows).length === 0 && !rows.some((r) => rowTotal(r) > 0)) out.form = 'Enter at least one quantity.';
  return out;
}

export function buildReceiptRequest(rows: readonly ReceiptRow[], notes: string | null): RecordReceiptRequest {
  const text = (notes ?? '').trim();
  return {
    lines: rows
      .filter((r) => rowTotal(r) > 0)
      .map((r) => ({ consignmentLineId: r.lineId, acceptedQuantity: r.accepted ?? 0, damagedQuantity: r.damaged ?? 0, rejectedQuantity: r.rejected ?? 0, missingQuantity: r.missing ?? 0 })),
    ...(text ? { notes: text } : {}),
  };
}

/** `lines[i]` errors (index of the SUBMITTED, filtered lines) mapped back onto dialog rows. */
export function mapReceiptProblem(problem: ApiProblem | null | undefined, rows: readonly ReceiptRow[]): LoadErrors {
  const raw = mapLoadProblem(problem);
  const sent = rows.map((r, i) => ({ r, i })).filter((x) => rowTotal(x.r) > 0).map((x) => x.i);
  const mapped: LoadErrors = { rows: {}, form: raw.form };
  for (const [k, v] of Object.entries(raw.rows)) mapped.rows[sent[Number(k)] ?? Number(k)] = v;
  return mapped;
}

export const RECEIPT_SHORTFALL_NOTE =
  'Damaged, rejected and missing quantities are recorded but stay in transit until they are resolved (Phase 06). Only accepted quantity reaches stock when the receipt is posted.';
