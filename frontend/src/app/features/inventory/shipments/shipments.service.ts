import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import { ContextService } from '../../../core/context/context.service';
import { idempotencyHeaders } from '../../../core/idempotency';
import { primaryLoad } from '../../../core/http/request-context';
import type {
  AddShipmentVehicleRequest, AllocateShipmentRequest, CreateShipmentRequest, LoadConsignmentRequest, Shipment, ShipmentListItem,
  ShipmentListQuery, UpdateShipmentRequest,
} from './shipments.models';

const BASE_URL = `${environment.apiBaseUrl}/shipments`;
export const PERMISSION_VIEW = 'Shipments.View';

/** List params; empty filters omitted; `scope=company` only while the Company view is active for Shipments.View (FI-D11). */
export function shipmentListParams(query: ShipmentListQuery, companyView: boolean): Record<string, string> {
  const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
  if (query.status) params['status'] = query.status;
  if (query.mode) params['mode'] = query.mode;
  if (query.sourceLocationId) params['sourceLocationId'] = query.sourceLocationId;
  if (query.search) params['search'] = query.search;
  if (companyView) params['scope'] = 'company';
  return params;
}

/**
 * Phase 04 shipment API. Create, cancel, allocate, change allocation and load require an Idempotency-Key
 * (callers hold one IdempotentSubmission per action); the other writes are state-idempotent.
 */
@Injectable({ providedIn: 'root' })
export class ShipmentsService {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ContextService);

  list(query: ShipmentListQuery = {}): Observable<PagedResult<ShipmentListItem>> {
    return this.http.get<PagedResult<ShipmentListItem>>(BASE_URL, { params: shipmentListParams(query, this.context.companyViewActive(PERMISSION_VIEW)) });
  }

  get(id: string): Observable<Shipment> {
    return this.http.get<Shipment>(`${BASE_URL}/${id}`, { context: primaryLoad() });
  }

  create(request: CreateShipmentRequest, idempotencyKey: string): Observable<Shipment> {
    return this.http.post<Shipment>(BASE_URL, request, { headers: idempotencyHeaders(idempotencyKey) });
  }

  update(id: string, request: UpdateShipmentRequest): Observable<Shipment> {
    return this.http.put<Shipment>(`${BASE_URL}/${id}`, request);
  }

  cancel(id: string, reason: string, idempotencyKey: string): Observable<Shipment> {
    return this.http.post<Shipment>(`${BASE_URL}/${id}/cancel`, { reason }, { headers: idempotencyHeaders(idempotencyKey) });
  }

  addVehicle(id: string, request: AddShipmentVehicleRequest): Observable<Shipment> {
    return this.http.post<Shipment>(`${BASE_URL}/${id}/vehicles`, request);
  }

  removeVehicle(id: string, shipmentVehicleId: string): Observable<Shipment> {
    return this.http.delete<Shipment>(`${BASE_URL}/${id}/vehicles/${shipmentVehicleId}`);
  }

  allocate(id: string, request: AllocateShipmentRequest, idempotencyKey: string): Observable<Shipment> {
    return this.http.post<Shipment>(`${BASE_URL}/${id}/allocations`, request, { headers: idempotencyHeaders(idempotencyKey) });
  }

  /** New TOTAL quantity in the allocation's unit; 0 cancels the line. */
  changeAllocation(id: string, consignmentLineId: string, quantity: number, idempotencyKey: string): Observable<Shipment> {
    return this.http.put<Shipment>(`${BASE_URL}/${id}/allocations/${consignmentLineId}`, { quantity }, { headers: idempotencyHeaders(idempotencyKey) });
  }

  removeAllocation(id: string, consignmentLineId: string): Observable<Shipment> {
    return this.http.delete<Shipment>(`${BASE_URL}/${id}/allocations/${consignmentLineId}`);
  }

  assignVehicle(id: string, consignmentId: string, shipmentVehicleId: string | null): Observable<Shipment> {
    return this.http.put<Shipment>(`${BASE_URL}/${id}/consignments/${consignmentId}/vehicle`, { shipmentVehicleId });
  }

  cancelConsignment(id: string, consignmentId: string, reason: string): Observable<Shipment> {
    return this.http.post<Shipment>(`${BASE_URL}/${id}/consignments/${consignmentId}/cancel`, { reason });
  }

  load(id: string, consignmentId: string, request: LoadConsignmentRequest, idempotencyKey: string): Observable<Shipment> {
    return this.http.post<Shipment>(`${BASE_URL}/${id}/consignments/${consignmentId}/load`, request, { headers: idempotencyHeaders(idempotencyKey) });
  }
}
