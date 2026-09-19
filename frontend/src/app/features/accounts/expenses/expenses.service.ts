import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import type {
  CreateExpenseRequest,
  ExpenseCategory,
  ExpenseDetail,
  ExpenseListItem,
  ExpenseStatus,
  UpdateExpenseRequest,
} from './expenses.models';

const BASE_URL = `${environment.apiBaseUrl}/expenses`;
const CATEGORIES_URL = `${environment.apiBaseUrl}/expense-categories`;

export interface ExpenseListQuery {
  search?: string;
  categoryId?: string;
  status?: ExpenseStatus;
  page?: number;
  pageSize?: number;
}

@Injectable({ providedIn: 'root' })
export class ExpensesService {
  private readonly http = inject(HttpClient);

  list(query: ExpenseListQuery): Observable<PagedResult<ExpenseListItem>> {
    const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) };
    if (query.search) params['search'] = query.search;
    if (query.categoryId) params['categoryId'] = query.categoryId;
    if (query.status) params['status'] = query.status;
    return this.http.get<PagedResult<ExpenseListItem>>(BASE_URL, { params });
  }

  get(id: string): Observable<ExpenseDetail> {
    return this.http.get<ExpenseDetail>(`${BASE_URL}/${id}`);
  }

  create(request: CreateExpenseRequest): Observable<ExpenseDetail> {
    return this.http.post<ExpenseDetail>(BASE_URL, request);
  }

  update(id: string, request: UpdateExpenseRequest): Observable<ExpenseDetail> {
    return this.http.put<ExpenseDetail>(`${BASE_URL}/${id}`, request);
  }

  cancel(id: string, reason: string | null): Observable<ExpenseDetail> {
    return this.http.post<ExpenseDetail>(`${BASE_URL}/${id}/cancel`, { reason });
  }

  categories(): Observable<ExpenseCategory[]> {
    return this.http.get<ExpenseCategory[]>(CATEGORIES_URL);
  }

  createCategory(name: string): Observable<ExpenseCategory> {
    return this.http.post<ExpenseCategory>(CATEGORIES_URL, { name });
  }

  renameCategory(id: string, name: string): Observable<ExpenseCategory> {
    return this.http.put<ExpenseCategory>(`${CATEGORIES_URL}/${id}`, { name });
  }

  setCategoryActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${CATEGORIES_URL}/${id}/status`, { isActive });
  }
}
