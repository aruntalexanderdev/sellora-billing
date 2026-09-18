import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import type { PagedResult } from '../../core/api.models';
import type {
  AddPurchasePaymentRequest,
  CreatePurchaseRequest,
  PurchaseDetail,
  PurchaseListItem,
  PurchaseStatus,
  UpdatePurchaseHeaderRequest,
} from './purchases.models';

const BASE_URL = `${environment.apiBaseUrl}/purchases`;

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

  create(request: CreatePurchaseRequest): Observable<PurchaseDetail> {
    return this.http.post<PurchaseDetail>(BASE_URL, request);
  }

  updateHeader(id: string, request: UpdatePurchaseHeaderRequest): Observable<PurchaseDetail> {
    return this.http.put<PurchaseDetail>(`${BASE_URL}/${id}`, request);
  }

  addPayment(id: string, request: AddPurchasePaymentRequest): Observable<PurchaseDetail> {
    return this.http.post<PurchaseDetail>(`${BASE_URL}/${id}/payments`, request);
  }

  cancel(id: string): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/${id}/cancel`, {});
  }
}
