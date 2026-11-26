import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { ContextService } from '../../core/context/context.service';
import type { PagedResult } from '../../core/api.models';
import type {
  CurrentStockReportRow,
  GstSummary,
  HsnDirection,
  HsnSummary,
  VatSummary,
  PaymentMethod,
  ProfitSummary,
  PurchaseReportRow,
  SalesReportRow,
  StockMovementReportRow,
  StockMovementType,
  StockStatus,
} from './reports.models';

const BASE_URL = `${environment.apiBaseUrl}/reports`;

export interface DateRangeQuery {
  dateFrom?: string;
  dateTo?: string;
  page?: number;
  pageSize?: number;
}

/** Branch scope (purchase/sales/profit reports) and location scope (current stock / movement reports); empty = all. */
export interface BranchQuery {
  branchId?: string | null;
}
export interface LocationQuery {
  stockLocationId?: string | null;
}

@Injectable({ providedIn: 'root' })
export class ReportsService {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ContextService);

  /** FI-D11: `scope=company` when the Company view is on and `permission` is held company-level. */
  private withScope(params: Record<string, string>, permission: string): Record<string, string> {
    if (this.context.companyViewActive(permission)) params['scope'] = 'company';
    return params;
  }

  currentStock(query: LocationQuery & { search?: string; categoryId?: string | null; subcategoryId?: string | null; status?: StockStatus; page?: number; pageSize?: number }): Observable<PagedResult<CurrentStockReportRow>> {
    const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
    if (query.search) params['search'] = query.search;
    if (query.categoryId) params['categoryId'] = query.categoryId;
    if (query.subcategoryId) params['subcategoryId'] = query.subcategoryId;
    if (query.status) params['status'] = query.status;
    if (query.stockLocationId) params['stockLocationId'] = query.stockLocationId;
    return this.http.get<PagedResult<CurrentStockReportRow>>(`${BASE_URL}/current-stock`, { params: this.withScope(params, 'Reports.Stock') });
  }


  stockMovement(query: DateRangeQuery & LocationQuery & { search?: string; type?: StockMovementType }): Observable<PagedResult<StockMovementReportRow>> {
    const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
    if (query.search) params['search'] = query.search;
    if (query.type) params['type'] = query.type;
    if (query.dateFrom) params['dateFrom'] = query.dateFrom;
    if (query.dateTo) params['dateTo'] = query.dateTo;
    if (query.stockLocationId) params['stockLocationId'] = query.stockLocationId;
    return this.http.get<PagedResult<StockMovementReportRow>>(`${BASE_URL}/stock-movement`, { params: this.withScope(params, 'Reports.Movement') });
  }

  purchases(query: DateRangeQuery & BranchQuery & { search?: string }): Observable<PagedResult<PurchaseReportRow>> {
    const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
    if (query.search) params['search'] = query.search;
    if (query.dateFrom) params['dateFrom'] = query.dateFrom;
    if (query.dateTo) params['dateTo'] = query.dateTo;
    if (query.branchId) params['branchId'] = query.branchId;
    return this.http.get<PagedResult<PurchaseReportRow>>(`${BASE_URL}/purchases`, { params: this.withScope(params, 'Reports.Purchase') });
  }

  sales(query: DateRangeQuery & BranchQuery & { search?: string; paymentMethod?: PaymentMethod }): Observable<PagedResult<SalesReportRow>> {
    const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
    if (query.search) params['search'] = query.search;
    if (query.paymentMethod) params['paymentMethod'] = query.paymentMethod;
    if (query.dateFrom) params['dateFrom'] = query.dateFrom;
    if (query.dateTo) params['dateTo'] = query.dateTo;
    if (query.branchId) params['branchId'] = query.branchId;
    return this.http.get<PagedResult<SalesReportRow>>(`${BASE_URL}/sales`, { params: this.withScope(params, 'Reports.Sales') });
  }

  profitSummary(query: BranchQuery & { dateFrom?: string; dateTo?: string }): Observable<ProfitSummary> {
    const params: Record<string, string> = {};
    if (query.dateFrom) params['dateFrom'] = query.dateFrom;
    if (query.dateTo) params['dateTo'] = query.dateTo;
    if (query.branchId) params['branchId'] = query.branchId;
    return this.http.get<ProfitSummary>(`${BASE_URL}/profit-summary`, { params: this.withScope(params, 'Reports.Profit') });
  }

  /** TP-5a tax reports: `dateFrom` and `dateTo` are required (at most 366 days apart). */
  gstSummary(query: BranchQuery & { dateFrom: string; dateTo: string }): Observable<GstSummary> {
    return this.http.get<GstSummary>(`${BASE_URL}/tax/gst-summary`, { params: this.taxParams(query) });
  }

  hsnSummary(query: BranchQuery & { dateFrom: string; dateTo: string; direction: HsnDirection }): Observable<HsnSummary> {
    return this.http.get<HsnSummary>(`${BASE_URL}/tax/hsn-summary`, { params: { ...this.taxParams(query), direction: query.direction } });
  }

  vatSummary(query: BranchQuery & { dateFrom: string; dateTo: string }): Observable<VatSummary> {
    return this.http.get<VatSummary>(`${BASE_URL}/tax/vat-summary`, { params: this.taxParams(query) });
  }

  private taxParams(query: BranchQuery & { dateFrom: string; dateTo: string }): Record<string, string> {
    const params: Record<string, string> = { dateFrom: query.dateFrom, dateTo: query.dateTo };
    if (query.branchId) params['branchId'] = query.branchId;
    return this.withScope(params, 'Reports.Tax');
  }
}
