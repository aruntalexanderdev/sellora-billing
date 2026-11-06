import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { idempotencyHeaders } from '../../core/idempotency';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import type { PagedResult } from '../../core/api.models';
import type {
  AddSalePaymentRequest,
  AddSaleRefundRequest,
  CreateSaleReturnRequest,
  SalesReturnDetail,
  SalesReturnListItem,
  CreateSaleRequest,
  SaleBarcodeMatch,
  SaleCatalogProduct,
  SalesInvoiceDetail,
  SalesInvoiceListItem,
  SalesStatus,
} from './sales.models';

const BASE_URL = `${environment.apiBaseUrl}/sales`;
const RETURNS_URL = `${environment.apiBaseUrl}/sales-returns`;

export interface SalesListQuery {
  search?: string;
  status?: SalesStatus;
  page?: number;
  pageSize?: number;
}

@Injectable({ providedIn: 'root' })
export class SalesService {
  private readonly http = inject(HttpClient);

  catalog(search?: string): Observable<SaleCatalogProduct[]> {
    const params: Record<string, string> = {};
    if (search) params['search'] = search;
    return this.http.get<SaleCatalogProduct[]>(`${BASE_URL}/catalog`, { params });
  }

  /** Exact barcode lookup; 404 when no sellable item carries the code. */
  lookupBarcode(code: string): Observable<SaleBarcodeMatch> {
    return this.http.get<SaleBarcodeMatch>(`${BASE_URL}/catalog/by-barcode/${encodeURIComponent(code.trim())}`);
  }

  list(query: SalesListQuery): Observable<PagedResult<SalesInvoiceListItem>> {
    const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
    if (query.search) params['search'] = query.search;
    if (query.status) params['status'] = query.status;
    return this.http.get<PagedResult<SalesInvoiceListItem>>(BASE_URL, { params });
  }

  get(id: string): Observable<SalesInvoiceDetail> {
    return this.http.get<SalesInvoiceDetail>(`${BASE_URL}/${id}`);
  }

  create(request: CreateSaleRequest, idempotencyKey?: string): Observable<SalesInvoiceDetail> {
    return this.http.post<SalesInvoiceDetail>(BASE_URL, request, { headers: idempotencyHeaders(idempotencyKey) });
  }

  addPayment(id: string, request: AddSalePaymentRequest): Observable<SalesInvoiceDetail> {
    return this.http.post<SalesInvoiceDetail>(`${BASE_URL}/${id}/payments`, request);
  }

  addRefund(id: string, request: AddSaleRefundRequest): Observable<SalesInvoiceDetail> {
    return this.http.post<SalesInvoiceDetail>(`${BASE_URL}/${id}/refunds`, request);
  }

  reversePayment(id: string, paymentId: string, reason: string | null): Observable<SalesInvoiceDetail> {
    return this.http.post<SalesInvoiceDetail>(`${BASE_URL}/${id}/payments/${paymentId}/reverse`, { reason });
  }

  createReturn(request: CreateSaleReturnRequest): Observable<SalesReturnDetail> {
    return this.http.post<SalesReturnDetail>(RETURNS_URL, request);
  }

  returns(query: { search?: string; page?: number; pageSize?: number }): Observable<PagedResult<SalesReturnListItem>> {
    const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
    if (query.search) params['search'] = query.search;
    return this.http.get<PagedResult<SalesReturnListItem>>(RETURNS_URL, { params });
  }

  getReturn(id: string): Observable<SalesReturnDetail> {
    return this.http.get<SalesReturnDetail>(`${RETURNS_URL}/${id}`);
  }

  cancel(id: string): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/${id}/cancel`, {});
  }
}
