import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import { idempotencyHeaders } from '../../../core/idempotency';
import { ContextService } from '../../../core/context/context.service';
import { primaryLoad } from '../../../core/http/request-context';
import type {
  CreateStockTransferRequest,
  ReadyForShipmentItem,
  ReadyForShipmentQuery,
  StockTransferHistoryEntry,
  ReceiveStockTransferRequest,
  ResolveStockTransferRequest,
  StockTransfer,
  StockTransferListItem,
  StockTransferListQuery,
  StockTransferReceiptResult,
  UpdateStockTransferRequest, ApproveStockTransferRequest } from './stock-transfers.models';

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

/** Ready-for-shipment query params; `scope=company` only while the Company view is active for StockTransfers.Dispatch (FI-D11/D28). */
export function readyForShipmentParams(query: ReadyForShipmentQuery, companyView: boolean): Record<string, string> {
  const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
  if (query.sourceLocationId) params['sourceLocationId'] = query.sourceLocationId;
  if (query.search) params['search'] = query.search;
  if (companyView) params['scope'] = 'company';
  return params;
}

/** Phase 9 API. Create / receipts / resolve require an Idempotency-Key; the state-idempotent actions accept one harmlessly. */
@Injectable({ providedIn: 'root' })
export class StockTransfersService {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ContextService);

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

  approve(id: string, idempotencyKey?: string, body: ApproveStockTransferRequest | Record<string, never> = {}): Observable<StockTransfer> {
    return this.http.post<StockTransfer>(`${BASE_URL}/${id}/approve`, body, { headers: idempotencyHeaders(idempotencyKey) });
  }

  reject(id: string, reason: string, idempotencyKey?: string): Observable<StockTransfer> {
    return this.http.post<StockTransfer>(`${BASE_URL}/${id}/reject`, { reason }, { headers: idempotencyHeaders(idempotencyKey) });
  }

  cancel(id: string, reason: string, idempotencyKey?: string): Observable<StockTransfer> {
    return this.http.post<StockTransfer>(`${BASE_URL}/${id}/cancel`, { reason }, { headers: idempotencyHeaders(idempotencyKey) });
  }

  receive(id: string, request: ReceiveStockTransferRequest, idempotencyKey: string): Observable<StockTransferReceiptResult> {
    return this.http.post<StockTransferReceiptResult>(`${BASE_URL}/${id}/receipts`, request, { headers: idempotencyHeaders(idempotencyKey) });
  }

  resolve(id: string, request: ResolveStockTransferRequest, idempotencyKey: string): Observable<StockTransferReceiptResult> {
    return this.http.post<StockTransferReceiptResult>(`${BASE_URL}/${id}/resolve`, request, { headers: idempotencyHeaders(idempotencyKey) });
  }

  /** Close the remaining quantity of a partially fulfilled request (StockTransfers.Approve). */
  close(id: string, reason: string, idempotencyKey?: string): Observable<StockTransfer> {
    return this.http.post<StockTransfer>(`${BASE_URL}/${id}/close`, { reason }, { headers: idempotencyHeaders(idempotencyKey) });
  }

  readyForShipment(query: ReadyForShipmentQuery = {}): Observable<PagedResult<ReadyForShipmentItem>> {
    const params = readyForShipmentParams(query, this.context.companyViewActive('StockTransfers.Dispatch'));
    return this.http.get<PagedResult<ReadyForShipmentItem>>(`${BASE_URL}/ready-for-shipment`, { params });
  }

  history(id: string): Observable<StockTransferHistoryEntry[]> {
    return this.http.get<StockTransferHistoryEntry[]>(`${BASE_URL}/${id}/history`);
  }
}
