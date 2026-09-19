import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type {
  FinancialTransactionList,
  FinancialTransactionStatus,
  FinancialTransactionType,
  MoneyDirection,
} from './financial-transactions.models';

const BASE_URL = `${environment.apiBaseUrl}/financial-transactions`;

export interface FinancialTransactionQuery {
  search?: string;
  type?: FinancialTransactionType;
  direction?: MoneyDirection;
  status?: FinancialTransactionStatus;
  accountId?: string;
  dateFrom?: string;
  dateTo?: string;
  page?: number;
  pageSize?: number;
}

@Injectable({ providedIn: 'root' })
export class FinancialTransactionsService {
  private readonly http = inject(HttpClient);

  list(query: FinancialTransactionQuery): Observable<FinancialTransactionList> {
    const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
    for (const key of ['search', 'type', 'direction', 'status', 'accountId', 'dateFrom', 'dateTo'] as const) {
      if (query[key]) params[key] = query[key]!;
    }
    return this.http.get<FinancialTransactionList>(BASE_URL, { params });
  }
}
