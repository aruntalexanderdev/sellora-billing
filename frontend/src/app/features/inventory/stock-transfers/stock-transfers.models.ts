/** Mirrors Domain/Entities/StockTransferStatus.cs (string enum). */
export type StockTransferStatus =
  | 'Draft'
  | 'AwaitingRequesterApproval'
  | 'Requested'
  | 'Approved'
  | 'PartiallyFulfilled'
  | 'Fulfilled'
  | 'Closed'
  | 'Rejected'
  | 'Dispatched'
  | 'PartiallyReceived'
  | 'Completed'
  | 'Cancelled';

export const STOCK_TRANSFER_STATUSES: StockTransferStatus[] = [
  'Draft', 'AwaitingRequesterApproval', 'Requested', 'Approved', 'PartiallyFulfilled', 'Fulfilled', 'Closed', 'Rejected', 'Dispatched', 'PartiallyReceived', 'Completed', 'Cancelled',
];

/** Display labels (FI-D26); legacy statuses keep their names. */
export const STOCK_TRANSFER_STATUS_LABELS: Record<StockTransferStatus, string> = {
  Draft: 'Draft',
  AwaitingRequesterApproval: 'Awaiting requesting branch', // FI-D48 level 1
  Requested: 'Requested',
  Approved: 'Ready for shipment',
  PartiallyFulfilled: 'Partially fulfilled',
  Fulfilled: 'Fulfilled',
  Closed: 'Closed (short)',
  Rejected: 'Rejected',
  Dispatched: 'Dispatched',
  PartiallyReceived: 'PartiallyReceived',
  Completed: 'Completed',
  Cancelled: 'Cancelled',
};

/** Mirrors Domain/Stock/StockTransferResolution (string enum). */
export type StockTransferResolution = 'WriteOff' | 'ReturnToSource';

export type StockTransferReceiptKind = 'Receipt' | 'Resolution';

export type StockTransferDirection = 'incoming' | 'outgoing';

// ---- Requests (Api/Contracts/StockTransferContracts.cs) ----

export interface StockTransferLineRequest {
  productId: string;
  productUnitId: string;
  quantity: number;
}

export interface CreateStockTransferRequest {
  sourceLocationId: string;
  destinationLocationId: string;
  notes: string | null;
  submit: boolean;
  lines: StockTransferLineRequest[];
}

export interface UpdateStockTransferRequest {
  sourceLocationId: string;
  destinationLocationId: string;
  notes: string | null;
  lines: StockTransferLineRequest[];
}

export interface StockTransferReasonRequest {
  reason: string;
}

export interface ReceiveStockTransferLineRequest {
  lineId: string;
  productUnitId: string;
  acceptedQuantity: number;
  damagedQuantity: number;
}

export interface ReceiveStockTransferRequest {
  notes: string | null;
  lines: ReceiveStockTransferLineRequest[];
}

export interface ResolveStockTransferLineRequest {
  lineId: string;
  action: StockTransferResolution;
}

export interface ResolveStockTransferRequest {
  reason: string;
  lines: ResolveStockTransferLineRequest[];
}

// ---- Responses ----

export interface StockTransferLocation {
  id: string;
  code: string | null;
  name: string | null;
  type: string | null;
}

export interface StockTransferBranch {
  id: string | null;
  code: string | null;
  name: string | null;
}

export interface StockTransferListItem {
  id: string;
  transferNo: string;
  status: StockTransferStatus;
  createdAt: string;
  branch: StockTransferBranch;
  from: StockTransferLocation;
  to: StockTransferLocation;
  sourceBranch: StockTransferBranch;
  destinationBranch: StockTransferBranch;
  lineCount: number;
  outstandingLineCount: number;
  dispatchedAt: string | null;
  completedAt: string | null;
}

export interface StockTransferActor {
  userId: string | null;
  userName: string | null;
  at: string | null;
}

export interface StockTransferLineBatch {
  purchaseBatchId: string;
  internalBatchNo: string;
  /** Null when the reader cannot access the source branch. */
  unitCost: number | null;
  dispatchedBaseQty: number;
  receivedBaseQty: number;
  damagedBaseQty: number;
  writtenOffBaseQty: number;
  returnedBaseQty: number;
  /** Phase 06 (FI-D40): accepted into the destination by a disposition; already subtracted from outstandingBaseQty. Absent on older payloads. */
  acceptedCorrectionBaseQty?: number;
  outstandingBaseQty: number;
}

export interface StockTransferLine {
  id: string;
  lineNo: number;
  productId: string;
  productName: string | null;
  sku: string | null;
  baseUnitCode: string | null;
  baseDecimalPlaces: number | null;
  productUnitId: string;
  unitCode: string;
  conversionFactorToBase: number;
  requestedQty: number;
  requestedBaseQty: number;
  dispatchProductUnitId: string | null;
  dispatchUnitCode: string | null;
  dispatchFactor: number | null;
  dispatchedQty: number | null;
  dispatchedBaseQty: number;
  receivedBaseQty: number;
  damagedBaseQty: number;
  writtenOffBaseQty: number;
  returnedBaseQty: number;
  acceptedCorrectionBaseQty?: number;
  outstandingBaseQty: number;
  allocatedBaseQty: number;
  closedBaseQty: number;
  remainingBaseQty: number;
  batches: StockTransferLineBatch[];
  /** Approved quantity in the requested unit (and base); null until approved or for requests approved before approval controls. */
  approvedQty?: number | null;
  approvedBaseQty?: number | null;
}

export interface StockTransferReceiptLine {
  transferLineId: string;
  productId: string;
  productUnitId: string;
  unitCode: string;
  conversionFactorToBase: number;
  acceptedQty: number;
  acceptedBaseQty: number;
  damagedQty: number;
  damagedBaseQty: number;
  writtenOffBaseQty: number;
  returnedBaseQty: number;
}

export interface StockTransferReceipt {
  id: string;
  sequence: number;
  kind: StockTransferReceiptKind;
  at: string;
  byUserId: string;
  byUserName: string | null;
  notes: string | null;
  lines: StockTransferReceiptLine[];
}

export interface StockTransfer {
  id: string;
  transferNo: string;
  status: StockTransferStatus;
  notes: string | null;
  branch: StockTransferBranch;
  from: StockTransferLocation;
  to: StockTransferLocation;
  transit: StockTransferLocation;
  sourceBranch: StockTransferBranch;
  destinationBranch: StockTransferBranch;
  created: StockTransferActor;
  submitted: StockTransferActor | null;
  approved: StockTransferActor | null;
  rejected: StockTransferActor | null;
  rejectionReason: string | null;
  dispatched: StockTransferActor | null;
  completed: StockTransferActor | null;
  cancelled: StockTransferActor | null;
  cancellationReason: string | null;
  closedAt: string | null;
  closedByName: string | null;
  closureReason: string | null;
  lines: StockTransferLine[];
  receipts: StockTransferReceipt[];
  /** Server-computed: the active branch fits the source for approve / reject / close (the source branch decides). */
  canApprove: boolean;
  /** Maker-checker: the caller created or submitted this request, which was submitted under approval separation. */
  approvalBlockedForYou?: boolean;
  /** Why the approver approved less than requested (null when approved as requested). */
  approvalAdjustmentReason?: string | null;
  /** FI-D49: estimated value at submit (base currency); null when the reader may not see the source's costs or for older requests. */
  estimatedValue?: number | null;
  /** FI-D48: the request needed the requesting branch's approval (level 1) first. */
  requiresRequesterApproval?: boolean;
  requesterApproved?: StockTransferActor | null;
  /** Server-computed: awaiting level 1 and the active branch fits the destination for StockTransfers.ApproveRequest. */
  canRequesterApprove?: boolean;
}

export interface StockTransferReceiptResult {
  receiptId: string;
  transfer: StockTransfer;
}

export interface StockTransferListQuery {
  status?: StockTransferStatus;
  sourceLocationId?: string;
  destinationLocationId?: string;
  direction?: StockTransferDirection;
  dateFrom?: string;
  dateTo?: string;
  page?: number;
  pageSize?: number;
}

/** GET /stock-transfers/ready-for-shipment item. */
export interface ReadyForShipmentItem {
  id: string;
  transferNo: string;
  status: StockTransferStatus;
  sourceLocationId: string;
  sourceLocationName: string | null;
  destinationLocationId: string;
  destinationLocationName: string | null;
  destinationBranchName: string | null;
  approvedAt: string | null;
  lineCount: number;
  remainingLineCount: number;
}

export interface ReadyForShipmentQuery {
  sourceLocationId?: string | null;
  search?: string;
  page?: number;
  pageSize?: number;
}

/** GET /stock-transfers/{id}/history item. */
export interface StockTransferHistoryEntry {
  at: string;
  action: string;
  fromStatus: StockTransferStatus | null;
  toStatus: StockTransferStatus | null;
  actorName: string | null;
  reason: string | null;
}

/** Optional body of POST approve: every line with its approved quantity in the requested unit; reason required when any line is reduced. */
export interface ApproveStockTransferRequest {
  lines: { lineId: string; quantity: number }[];
  reason: string | null;
}
