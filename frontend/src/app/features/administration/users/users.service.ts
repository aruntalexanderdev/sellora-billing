import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import type { CreateUserRequest, UpdateUserRequest, UserAccountStatus, UserDetail, UserListItem } from './users.models';

const BASE_URL = `${environment.apiBaseUrl}/users`;

export interface UserListQuery {
  search?: string;
  roleId?: string;
  status?: UserAccountStatus;
  page?: number;
  pageSize?: number;
}

@Injectable({ providedIn: 'root' })
export class UsersService {
  private readonly http = inject(HttpClient);

  list(query: UserListQuery): Observable<PagedResult<UserListItem>> {
    const params: Record<string, string> = {};
    if (query.search) params['search'] = query.search;
    if (query.roleId) params['roleId'] = query.roleId;
    if (query.status) params['status'] = query.status;
    params['page'] = String(query.page ?? 1);
    params['pageSize'] = String(query.pageSize ?? 20);

    return this.http.get<PagedResult<UserListItem>>(BASE_URL, { params });
  }

  get(id: string): Observable<UserDetail> {
    return this.http.get<UserDetail>(`${BASE_URL}/${id}`);
  }

  create(request: CreateUserRequest): Observable<UserDetail> {
    return this.http.post<UserDetail>(BASE_URL, request);
  }

  update(id: string, request: UpdateUserRequest): Observable<UserDetail> {
    return this.http.put<UserDetail>(`${BASE_URL}/${id}`, request);
  }

  setStatus(id: string, status: UserAccountStatus): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/${id}/status`, { status });
  }

  resetPassword(id: string): Observable<{ temporaryPassword: string }> {
    return this.http.post<{ temporaryPassword: string }>(`${BASE_URL}/${id}/reset-password`, {});
  }
}
