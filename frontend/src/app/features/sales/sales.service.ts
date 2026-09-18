import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import type { PagedResult } from '../../core/api.models';
import type {
  CreateSaleRequest,
  SaleCatalogProduct,
  SalesInvoiceDetail,
  SalesInvoiceListItem,
  SalesStatus,
} from './sales.models';

const BASE_URL = `${environment.apiBaseUrl}/sales`;

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

  list(query: SalesListQuery): Observable<PagedResult<SalesInvoiceListItem>> {
    const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
    if (query.search) params['search'] = query.search;
    if (query.status) params['status'] = query.status;
    return this.http.get<PagedResult<SalesInvoiceListItem>>(BASE_URL, { params });
  }

  get(id: string): Observable<SalesInvoiceDetail> {
    return this.http.get<SalesInvoiceDetail>(`${BASE_URL}/${id}`);
  }

  create(request: CreateSaleRequest): Observable<SalesInvoiceDetail> {
    return this.http.post<SalesInvoiceDetail>(BASE_URL, request);
  }

  cancel(id: string): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/${id}/cancel`, {});
  }
}
