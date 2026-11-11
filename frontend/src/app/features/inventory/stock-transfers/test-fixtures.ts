import type { StockTransfer, StockTransferLine, StockTransferStatus } from './stock-transfers.models';

export function line(overrides: Partial<StockTransferLine> = {}): StockTransferLine {
  return {
    id: 'l1', lineNo: 1, productId: 'pen', productName: 'Pen', sku: 'PEN-1', baseUnitCode: 'pc', baseDecimalPlaces: 0,
    productUnitId: 'box', unitCode: 'box', conversionFactorToBase: 10, requestedQty: 5, requestedBaseQty: 50,
    dispatchProductUnitId: null, dispatchUnitCode: null, dispatchFactor: null, dispatchedQty: null, dispatchedBaseQty: 0,
    receivedBaseQty: 0, damagedBaseQty: 0, writtenOffBaseQty: 0, returnedBaseQty: 0, outstandingBaseQty: 0, batches: [],
    ...overrides,
  };
}

export function dispatchedLine(overrides: Partial<StockTransferLine> = {}): StockTransferLine {
  return line({
    dispatchProductUnitId: 'box', dispatchUnitCode: 'box', dispatchFactor: 10, dispatchedQty: 5, dispatchedBaseQty: 50, outstandingBaseQty: 50,
    ...overrides,
  });
}

const loc = (id: string, name: string, type: string) => ({ id, code: id.toUpperCase(), name, type });
const branch = { id: 'b1', code: 'B1', name: 'Branch 1' };
const actor = { userId: 'u1', userName: 'admin', at: '2026-11-06T10:00:00Z' };

export function transfer(status: StockTransferStatus, lines: StockTransferLine[] = [line()], overrides: Partial<StockTransfer> = {}): StockTransfer {
  return {
    id: 't1', transferNo: 'TRF-0001', status, notes: null, branch,
    from: loc('store', 'Main Store', 'BranchStore'), to: loc('van', 'Van 1', 'Vehicle'), transit: loc('transit', 'Transit', 'Transit'),
    sourceBranch: branch, destinationBranch: branch, created: actor,
    submitted: null, approved: null, rejected: null, rejectionReason: null, dispatched: null, completed: null, cancelled: null, cancellationReason: null,
    lines, receipts: [], ...overrides,
  };
}
