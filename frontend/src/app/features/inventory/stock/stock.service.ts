import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { ContextService } from '../../../core/context/context.service';
import { primaryLoad } from '../../../core/http/request-context';
import type { PagedResult } from '../../../core/api.models';
import type { BatchConsumption, StockDetail, StockOverviewItem, StockStatus } from './stock.models';

const BASE_URL = `${environment.apiBaseUrl}/stock`;

export interface StockOverviewQuery {
  search?: string;
  category?: string;
  status?: StockStatus;
  stockLocationId?: string | null;
  page?: number;
  pageSize?: number;
}

@Injectable({ providedIn: 'root' })
export class StockService {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ContextService);

  overview(query: StockOverviewQuery): Observable<PagedResult<StockOverviewItem>> {
    const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 50) };
    if (query.search) params['search'] = query.search;
    if (query.category) params['category'] = query.category;
    if (query.status) params['status'] = query.status;
    if (query.stockLocationId) params['stockLocationId'] = query.stockLocationId;
    if (this.context.companyViewActive('Inventory.View')) params['scope'] = 'company';
    return this.http.get<PagedResult<StockOverviewItem>>(`${BASE_URL}/overview`, { params });
  }

  batchConsumptions(batchId: string): Observable<BatchConsumption[]> {
    return this.http.get<BatchConsumption[]>(`${BASE_URL}/batches/${batchId}/consumptions`);
  }

  /** `primary` marks the stock page's own load: a bare 403 then goes to /permission-denied. Lookups (e.g. the adjust form) leave it off. */
  detail(productId: string, stockLocationId?: string | null, primary = false): Observable<StockDetail> {
    const params: Record<string, string> = stockLocationId ? { stockLocationId } : {};
    return this.http.get<StockDetail>(`${BASE_URL}/${productId}`, primary ? { params, context: primaryLoad() } : { params });
  }
}
