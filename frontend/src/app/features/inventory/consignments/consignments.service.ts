import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import { ContextService } from '../../../core/context/context.service';
import { idempotencyHeaders } from '../../../core/idempotency';
import { primaryLoad } from '../../../core/http/request-context';
import type { ConsignmentDetail, ConsignmentListItem, ConsignmentListQuery, ConsignmentDisposition, ConsignmentReceipt, RecordDispositionRequest, RecordReceiptRequest } from '../shipments/shipments.models';

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

  receipts(id: string): Observable<ConsignmentReceipt[]> {
    return this.http.get<ConsignmentReceipt[]>(`${BASE_URL}/${id}/receipts`);
  }

  receipt(id: string, receiptId: string): Observable<ConsignmentReceipt> {
    return this.http.get<ConsignmentReceipt>(`${BASE_URL}/${id}/receipts/${receiptId}`);
  }

  /** Verifies (records) a receipt; moves no stock. Quantities are in each line's own unit. */
  recordReceipt(id: string, request: RecordReceiptRequest, idempotencyKey: string): Observable<ConsignmentReceipt> {
    return this.http.post<ConsignmentReceipt>(`${BASE_URL}/${id}/receipts`, request, { headers: idempotencyHeaders(idempotencyKey) });
  }

  /** Posts a Verified receipt (accepted quantity Transit to destination); idempotent. */
  postReceipt(id: string, receiptId: string, idempotencyKey: string): Observable<ConsignmentReceipt> {
    return this.http.post<ConsignmentReceipt>(`${BASE_URL}/${id}/receipts/${receiptId}/post`, null, { headers: idempotencyHeaders(idempotencyKey) });
  }

  /** Supervisor policy only (StockTransfers.ApproveReceipt): approves a PendingApproval receipt, which posts it. No body. */
  approveReceipt(id: string, receiptId: string, idempotencyKey: string): Observable<ConsignmentReceipt> {
    return this.http.post<ConsignmentReceipt>(`${BASE_URL}/${id}/receipts/${receiptId}/approve`, null, { headers: idempotencyHeaders(idempotencyKey) });
  }

  /** Rejects an unposted receipt for correction; its quantities become recordable again. */
  rejectReceipt(id: string, receiptId: string, reason: string, idempotencyKey: string): Observable<ConsignmentReceipt> {
    return this.http.post<ConsignmentReceipt>(`${BASE_URL}/${id}/receipts/${receiptId}/reject`, { reason }, { headers: idempotencyHeaders(idempotencyKey) });
  }

  dispositions(id: string): Observable<ConsignmentDisposition[]> {
    return this.http.get<ConsignmentDisposition[]>(`${BASE_URL}/${id}/dispositions`);
  }

  /** Resolves damaged / rejected / missing quantities of posted receipts (company-level StockTransfers.Resolve). */
  recordDisposition(id: string, request: RecordDispositionRequest, idempotencyKey: string): Observable<ConsignmentDisposition> {
    return this.http.post<ConsignmentDisposition>(`${BASE_URL}/${id}/dispositions`, request, { headers: idempotencyHeaders(idempotencyKey) });
  }
}
