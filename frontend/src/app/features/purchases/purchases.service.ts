import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { idempotencyHeaders } from '../../core/idempotency';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import type { PagedResult } from '../../core/api.models';
import type {
  AddPurchasePaymentRequest,
  AddPurchaseRefundRequest,
  CreatePurchaseReturnRequest,
  PurchaseReturnDetail,
  PurchaseReturnListItem,
  CreatePurchaseRequest,
  PurchaseDetail,
  PurchaseListItem,
  PurchaseStatus,
  UpdatePurchaseHeaderRequest,
} from './purchases.models';

const BASE_URL = `${environment.apiBaseUrl}/purchases`;
const RETURNS_URL = `${environment.apiBaseUrl}/purchase-returns`;

export interface PurchaseListQuery {
  search?: string;
  supplierId?: string;
  status?: PurchaseStatus;
  page?: number;
  pageSize?: number;
}

@Injectable({ providedIn: 'root' })
export class PurchasesService {
  private readonly http = inject(HttpClient);

  list(query: PurchaseListQuery): Observable<PagedResult<PurchaseListItem>> {
    const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
    if (query.search) params['search'] = query.search;
    if (query.supplierId) params['supplierId'] = query.supplierId;
    if (query.status) params['status'] = query.status;
    return this.http.get<PagedResult<PurchaseListItem>>(BASE_URL, { params });
  }

  get(id: string): Observable<PurchaseDetail> {
    return this.http.get<PurchaseDetail>(`${BASE_URL}/${id}`);
  }

  create(request: CreatePurchaseRequest, idempotencyKey?: string): Observable<PurchaseDetail> {
    return this.http.post<PurchaseDetail>(BASE_URL, request, { headers: idempotencyHeaders(idempotencyKey) });
  }

  updateHeader(id: string, request: UpdatePurchaseHeaderRequest): Observable<PurchaseDetail> {
    return this.http.put<PurchaseDetail>(`${BASE_URL}/${id}`, request);
  }

  addPayment(id: string, request: AddPurchasePaymentRequest): Observable<PurchaseDetail> {
    return this.http.post<PurchaseDetail>(`${BASE_URL}/${id}/payments`, request);
  }

  addRefund(id: string, request: AddPurchaseRefundRequest): Observable<PurchaseDetail> {
    return this.http.post<PurchaseDetail>(`${BASE_URL}/${id}/refunds`, request);
  }

  reversePayment(id: string, paymentId: string, reason: string | null): Observable<PurchaseDetail> {
    return this.http.post<PurchaseDetail>(`${BASE_URL}/${id}/payments/${paymentId}/reverse`, { reason });
  }

  createReturn(request: CreatePurchaseReturnRequest): Observable<PurchaseReturnDetail> {
    return this.http.post<PurchaseReturnDetail>(RETURNS_URL, request);
  }

  returns(query: { search?: string; page?: number; pageSize?: number }): Observable<PagedResult<PurchaseReturnListItem>> {
    const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
    if (query.search) params['search'] = query.search;
    return this.http.get<PagedResult<PurchaseReturnListItem>>(RETURNS_URL, { params });
  }

  getReturn(id: string): Observable<PurchaseReturnDetail> {
    return this.http.get<PurchaseReturnDetail>(`${RETURNS_URL}/${id}`);
  }

  cancel(id: string): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/${id}/cancel`, {});
  }
}
