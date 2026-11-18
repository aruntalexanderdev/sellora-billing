import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import { ContextService } from '../../../core/context/context.service';
import type { TransitItem, TransitQuery } from '../shipments/shipments.models';

const BASE_URL = `${environment.apiBaseUrl}/transit`;

/** `view` is always sent; `scope=company` only while the Company view is active for Shipments.View. */
export function transitParams(query: TransitQuery, companyView: boolean): Record<string, string> {
  const params: Record<string, string> = { view: query.view, page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
  if (query.search) params['search'] = query.search;
  if (companyView) params['scope'] = 'company';
  return params;
}

@Injectable({ providedIn: 'root' })
export class TransitService {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ContextService);

  list(query: TransitQuery): Observable<PagedResult<TransitItem>> {
    return this.http.get<PagedResult<TransitItem>>(BASE_URL, { params: transitParams(query, this.context.companyViewActive('Shipments.View')) });
  }
}
