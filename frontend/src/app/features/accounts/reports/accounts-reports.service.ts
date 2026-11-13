import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { ContextService } from '../../../core/context/context.service';
import type {
  CashBankReport,
  CashFlowReport,
  ExpenseReport,
  OutstandingReport,
  PaymentEntryKind,
  PaymentMethod,
  PaymentMethodSummary,
  PaymentReport,
} from './accounts-reports.models';

const BASE_URL = `${environment.apiBaseUrl}/reports/accounts`;

export interface DateRange {
  dateFrom?: string;
  dateTo?: string;
}

export interface PagedQuery extends DateRange {
  search?: string;
  page?: number;
  pageSize?: number;
}

/** Drops empty values so the API only sees the filters actually in use. */
function toParams(query: object): Record<string, string> {
  const params: Record<string, string> = {};
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null && value !== '' && value !== false) params[key] = String(value);
  }
  return params;
}

@Injectable({ providedIn: 'root' })
export class AccountsReportsService {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ContextService);

  /** FI-D11: query params plus `scope=company` when the Company view is on and `Reports.Accounts` is held company-level. */
  private params(query: object): Record<string, string> {
    const params = toParams(query);
    if (this.context.companyViewActive('Reports.Accounts')) params['scope'] = 'company';
    return params;
  }

  payments(side: 'purchase' | 'sales', query: PagedQuery & { paymentMethod?: PaymentMethod; kind?: PaymentEntryKind }): Observable<PaymentReport> {
    return this.http.get<PaymentReport>(`${BASE_URL}/${side === 'purchase' ? 'purchase-payments' : 'sales-payments'}`, { params: this.params(query) });
  }

  outstanding(side: 'supplier' | 'customer', query: PagedQuery & { onlyOutstanding?: boolean }): Observable<OutstandingReport> {
    return this.http.get<OutstandingReport>(`${BASE_URL}/${side}-outstanding`, { params: this.params(query) });
  }

  expenses(query: PagedQuery & { categoryId?: string; paymentMethod?: PaymentMethod; includeCancelled?: boolean }): Observable<ExpenseReport> {
    return this.http.get<ExpenseReport>(`${BASE_URL}/expenses`, { params: this.params(query) });
  }

  cashFlow(query: DateRange): Observable<CashFlowReport> {
    return this.http.get<CashFlowReport>(`${BASE_URL}/cash-flow`, { params: this.params(query) });
  }

  cashBank(query: DateRange): Observable<CashBankReport> {
    return this.http.get<CashBankReport>(`${BASE_URL}/cash-bank`, { params: this.params(query) });
  }

  paymentMethods(query: DateRange): Observable<PaymentMethodSummary> {
    return this.http.get<PaymentMethodSummary>(`${BASE_URL}/payment-methods`, { params: this.params(query) });
  }
}
