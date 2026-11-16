import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import { ContextService } from '../../../core/context/context.service';
import type { ReservationListQuery, StockReservation } from '../shipments/shipments.models';

const BASE_URL = `${environment.apiBaseUrl}/stock-reservations`;

/** Server default status is Active (oldest first). `scope=company` only while the Company view is active for Shipments.View. */
export function reservationListParams(query: ReservationListQuery, companyView: boolean): Record<string, string> {
  const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
  if (query.locationId) params['locationId'] = query.locationId;
  if (query.productId) params['productId'] = query.productId;
  if (query.status) params['status'] = query.status;
  if (companyView) params['scope'] = 'company';
  return params;
}

@Injectable({ providedIn: 'root' })
export class ReservationsService {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ContextService);

  list(query: ReservationListQuery = {}): Observable<PagedResult<StockReservation>> {
    return this.http.get<PagedResult<StockReservation>>(BASE_URL, { params: reservationListParams(query, this.context.companyViewActive('Shipments.View')) });
  }

  /** Company-only action (Reservations.Release); also cancels the consignment line. */
  release(id: string, reason: string): Observable<StockReservation> {
    return this.http.post<StockReservation>(`${BASE_URL}/${id}/release`, { reason });
  }
}
