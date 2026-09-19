import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PaymentMethod } from '../../purchases/purchases.models';
import type {
  AccountOption,
  CreateAccountRequest,
  FinancialAccount,
  FinancialAccountList,
  TransferRequest,
  UpdateAccountRequest,
} from './cash-bank.models';

const BASE_URL = `${environment.apiBaseUrl}/financial-accounts`;

/** The account a payment made by this method most plausibly went through: cash → a cash account, anything
 *  else → a bank account. Falls back to the first option; '' when there are no accounts. */
export function defaultAccountId(options: AccountOption[], method: PaymentMethod): string {
  const wanted = method === 'Cash' ? 'Cash' : 'Bank';
  return (options.find((o) => o.kind === wanted) ?? options[0])?.id ?? '';
}

@Injectable({ providedIn: 'root' })
export class CashBankService {
  private readonly http = inject(HttpClient);

  list(): Observable<FinancialAccountList> {
    return this.http.get<FinancialAccountList>(BASE_URL);
  }

  get(id: string): Observable<FinancialAccount> {
    return this.http.get<FinancialAccount>(`${BASE_URL}/${id}`);
  }

  /** Active accounts a payment can be recorded against; open to any signed-in user. */
  options(): Observable<AccountOption[]> {
    return this.http.get<AccountOption[]>(`${BASE_URL}/options`);
  }

  create(request: CreateAccountRequest): Observable<FinancialAccount> {
    return this.http.post<FinancialAccount>(BASE_URL, request);
  }

  update(id: string, request: UpdateAccountRequest): Observable<FinancialAccount> {
    return this.http.put<FinancialAccount>(`${BASE_URL}/${id}`, request);
  }

  setActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/${id}/status`, { isActive });
  }

  transfer(request: TransferRequest): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/transfers`, request);
  }

  reconcile(id: string, transactionIds: string[]): Observable<{ changed: number }> {
    return this.http.post<{ changed: number }>(`${BASE_URL}/${id}/reconcile`, { transactionIds });
  }

  unreconcile(id: string, transactionIds: string[]): Observable<{ changed: number }> {
    return this.http.post<{ changed: number }>(`${BASE_URL}/${id}/unreconcile`, { transactionIds });
  }
}
