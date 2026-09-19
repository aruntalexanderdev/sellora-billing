import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
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

  payments(side: 'purchase' | 'sales', query: PagedQuery & { paymentMethod?: PaymentMethod; kind?: PaymentEntryKind }): Observable<PaymentReport> {
    return this.http.get<PaymentReport>(`${BASE_URL}/${side === 'purchase' ? 'purchase-payments' : 'sales-payments'}`, { params: toParams(query) });
  }

  outstanding(side: 'supplier' | 'customer', query: PagedQuery & { onlyOutstanding?: boolean }): Observable<OutstandingReport> {
    return this.http.get<OutstandingReport>(`${BASE_URL}/${side}-outstanding`, { params: toParams(query) });
  }

  expenses(query: PagedQuery & { categoryId?: string; paymentMethod?: PaymentMethod; includeCancelled?: boolean }): Observable<ExpenseReport> {
    return this.http.get<ExpenseReport>(`${BASE_URL}/expenses`, { params: toParams(query) });
  }

  cashFlow(query: DateRange): Observable<CashFlowReport> {
    return this.http.get<CashFlowReport>(`${BASE_URL}/cash-flow`, { params: toParams(query) });
  }

  cashBank(query: DateRange): Observable<CashBankReport> {
    return this.http.get<CashBankReport>(`${BASE_URL}/cash-bank`, { params: toParams(query) });
  }

  paymentMethods(query: DateRange): Observable<PaymentMethodSummary> {
    return this.http.get<PaymentMethodSummary>(`${BASE_URL}/payment-methods`, { params: toParams(query) });
  }
}
