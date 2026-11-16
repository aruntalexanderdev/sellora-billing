import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import { ContextService } from '../../../core/context/context.service';
import { primaryLoad } from '../../../core/http/request-context';
import type { ConsignmentDetail, ConsignmentListItem, ConsignmentListQuery } from '../shipments/shipments.models';

const BASE_URL = `${environment.apiBaseUrl}/consignments`;

/** `direction` is mandatory server-side; `scope=company` only while the Company view is active for Shipments.View. */
export function consignmentListParams(query: ConsignmentListQuery, companyView: boolean): Record<string, string> {
  const params: Record<string, string> = { direction: query.direction, page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
  if (query.status) params['status'] = query.status;
  if (query.shipmentId) params['shipmentId'] = query.shipmentId;
  if (query.sourceLocationId) params['sourceLocationId'] = query.sourceLocationId;
  if (query.destinationLocationId) params['destinationLocationId'] = query.destinationLocationId;
  if (query.search) params['search'] = query.search;
  if (companyView) params['scope'] = 'company';
  return params;
}

@Injectable({ providedIn: 'root' })
export class ConsignmentsService {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ContextService);

  list(query: ConsignmentListQuery): Observable<PagedResult<ConsignmentListItem>> {
    return this.http.get<PagedResult<ConsignmentListItem>>(BASE_URL, { params: consignmentListParams(query, this.context.companyViewActive('Shipments.View')) });
  }

  get(id: string): Observable<ConsignmentDetail> {
    return this.http.get<ConsignmentDetail>(`${BASE_URL}/${id}`, { context: primaryLoad() });
  }
}
