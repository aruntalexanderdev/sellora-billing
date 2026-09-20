import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import type { BatchConsumption, StockDetail, StockOverviewItem, StockStatus } from './stock.models';

const BASE_URL = `${environment.apiBaseUrl}/stock`;

export interface StockOverviewQuery {
  search?: string;
  category?: string;
  status?: StockStatus;
  page?: number;
  pageSize?: number;
}

@Injectable({ providedIn: 'root' })
export class StockService {
  private readonly http = inject(HttpClient);

  overview(query: StockOverviewQuery): Observable<PagedResult<StockOverviewItem>> {
    const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 50) };
    if (query.search) params['search'] = query.search;
    if (query.category) params['category'] = query.category;
    if (query.status) params['status'] = query.status;
    return this.http.get<PagedResult<StockOverviewItem>>(`${BASE_URL}/overview`, { params });
  }

  batchConsumptions(batchId: string): Observable<BatchConsumption[]> {
    return this.http.get<BatchConsumption[]>(`${BASE_URL}/batches/${batchId}/consumptions`);
  }

  detail(productId: string): Observable<StockDetail> {
    return this.http.get<StockDetail>(`${BASE_URL}/${productId}`);
  }
}
