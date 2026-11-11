import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import { idempotencyHeaders } from '../../../core/idempotency';
import { primaryLoad } from '../../../core/http/request-context';
import type {
  CreateStockTransferRequest,
  DispatchStockTransferRequest,
  ReceiveStockTransferRequest,
  ResolveStockTransferRequest,
  StockTransfer,
  StockTransferListItem,
  StockTransferListQuery,
  StockTransferReceiptResult,
  UpdateStockTransferRequest,
} from './stock-transfers.models';

const BASE_URL = `${environment.apiBaseUrl}/stock-transfers`;

/** Query params for the list; empty filters are omitted. Direction is relative to the working branch (server side). */
export function stockTransferListParams(query: StockTransferListQuery): Record<string, string> {
  const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
  if (query.status) params['status'] = query.status;
  if (query.sourceLocationId) params['sourceLocationId'] = query.sourceLocationId;
  if (query.destinationLocationId) params['destinationLocationId'] = query.destinationLocationId;
  if (query.direction) params['direction'] = query.direction;
  if (query.dateFrom) params['dateFrom'] = query.dateFrom;
  if (query.dateTo) params['dateTo'] = query.dateTo;
  return params;
}

/** Phase 9 API. Create / dispatch / receipts / resolve require an Idempotency-Key; the state-idempotent actions accept one harmlessly. */
@Injectable({ providedIn: 'root' })
export class StockTransfersService {
  private readonly http = inject(HttpClient);

  list(query: StockTransferListQuery = {}): Observable<PagedResult<StockTransferListItem>> {
    return this.http.get<PagedResult<StockTransferListItem>>(BASE_URL, { params: stockTransferListParams(query) });
  }

  get(id: string): Observable<StockTransfer> {
    return this.http.get<StockTransfer>(`${BASE_URL}/${id}`, { context: primaryLoad() });
  }

  create(request: CreateStockTransferRequest, idempotencyKey: string): Observable<StockTransfer> {
    return this.http.post<StockTransfer>(BASE_URL, request, { headers: idempotencyHeaders(idempotencyKey) });
  }

  update(id: string, request: UpdateStockTransferRequest): Observable<StockTransfer> {
    return this.http.put<StockTransfer>(`${BASE_URL}/${id}`, request);
  }

  submit(id: string, idempotencyKey?: string): Observable<StockTransfer> {
    return this.http.post<StockTransfer>(`${BASE_URL}/${id}/submit`, {}, { headers: idempotencyHeaders(idempotencyKey) });
  }

  approve(id: string, idempotencyKey?: string): Observable<StockTransfer> {
    return this.http.post<StockTransfer>(`${BASE_URL}/${id}/approve`, {}, { headers: idempotencyHeaders(idempotencyKey) });
  }

  reject(id: string, reason: string, idempotencyKey?: string): Observable<StockTransfer> {
    return this.http.post<StockTransfer>(`${BASE_URL}/${id}/reject`, { reason }, { headers: idempotencyHeaders(idempotencyKey) });
  }

  cancel(id: string, reason: string, idempotencyKey?: string): Observable<StockTransfer> {
    return this.http.post<StockTransfer>(`${BASE_URL}/${id}/cancel`, { reason }, { headers: idempotencyHeaders(idempotencyKey) });
  }

  dispatch(id: string, request: DispatchStockTransferRequest, idempotencyKey: string): Observable<StockTransfer> {
    return this.http.post<StockTransfer>(`${BASE_URL}/${id}/dispatch`, request, { headers: idempotencyHeaders(idempotencyKey) });
  }

  receive(id: string, request: ReceiveStockTransferRequest, idempotencyKey: string): Observable<StockTransferReceiptResult> {
    return this.http.post<StockTransferReceiptResult>(`${BASE_URL}/${id}/receipts`, request, { headers: idempotencyHeaders(idempotencyKey) });
  }

  resolve(id: string, request: ResolveStockTransferRequest, idempotencyKey: string): Observable<StockTransferReceiptResult> {
    return this.http.post<StockTransferReceiptResult>(`${BASE_URL}/${id}/resolve`, request, { headers: idempotencyHeaders(idempotencyKey) });
  }
}
