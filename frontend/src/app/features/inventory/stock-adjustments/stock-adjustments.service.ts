import { HttpClient } from '@angular/common/http';
import { primaryLoad } from '../../../core/http/request-context';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { idempotencyHeaders } from '../../../core/idempotency';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import type {
  CreateStockAdjustmentRequest,
  StockAdjustmentDetail,
  StockAdjustmentListItem,
  StockAdjustmentListQuery,
} from './stock-adjustments.models';

const BASE_URL = `${environment.apiBaseUrl}/stock-adjustments`;

/** Builds the HTTP query params for the adjustment list; empty filters are omitted. */
export function stockAdjustmentListParams(query: StockAdjustmentListQuery): Record<string, string> {
  const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
  if (query.search) params['search'] = query.search;
  if (query.productId) params['productId'] = query.productId;
  if (query.type) params['type'] = query.type;
  if (query.direction) params['direction'] = query.direction;
  if (query.dateFrom) params['dateFrom'] = query.dateFrom;
  if (query.dateTo) params['dateTo'] = query.dateTo;
  if (query.branchId) params['branchId'] = query.branchId;
  if (query.stockLocationId) params['stockLocationId'] = query.stockLocationId;
  return params;
}

@Injectable({ providedIn: 'root' })
export class StockAdjustmentsService {
  private readonly http = inject(HttpClient);

  list(query: StockAdjustmentListQuery = {}): Observable<PagedResult<StockAdjustmentListItem>> {
    return this.http.get<PagedResult<StockAdjustmentListItem>>(BASE_URL, { params: stockAdjustmentListParams(query) });
  }

  get(id: string): Observable<StockAdjustmentDetail> {
    return this.http.get<StockAdjustmentDetail>(`${BASE_URL}/${id}`, { context: primaryLoad() });
  }

  create(request: CreateStockAdjustmentRequest, idempotencyKey?: string): Observable<StockAdjustmentDetail> {
    return this.http.post<StockAdjustmentDetail>(BASE_URL, request, { headers: idempotencyHeaders(idempotencyKey) });
  }
}
