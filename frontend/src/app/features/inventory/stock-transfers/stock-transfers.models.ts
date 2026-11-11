/** Mirrors Domain/Entities/StockTransferStatus.cs (string enum). */
export type StockTransferStatus =
  | 'Draft'
  | 'Requested'
  | 'Approved'
  | 'Rejected'
  | 'Dispatched'
  | 'PartiallyReceived'
  | 'Completed'
  | 'Cancelled';

export const STOCK_TRANSFER_STATUSES: StockTransferStatus[] = [
  'Draft', 'Requested', 'Approved', 'Rejected', 'Dispatched', 'PartiallyReceived', 'Completed', 'Cancelled',
];

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

export interface DispatchStockTransferLineRequest {
  lineId: string;
  productUnitId: string;
  quantity: number;
}

export interface DispatchStockTransferRequest {
  lines: DispatchStockTransferLineRequest[];
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
  unitCost: number;
  dispatchedBaseQty: number;
  receivedBaseQty: number;
  damagedBaseQty: number;
  writtenOffBaseQty: number;
  returnedBaseQty: number;
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
  outstandingBaseQty: number;
  batches: StockTransferLineBatch[];
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
  lines: StockTransferLine[];
  receipts: StockTransferReceipt[];
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
