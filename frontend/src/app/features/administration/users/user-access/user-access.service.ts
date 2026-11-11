import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../../environments/environment';
import type { PagedResult } from '../../../../core/api.models';
import type { AccessibleBranch } from '../../../../shared/branches/branch-select';
import type {
  AddAssignmentRequest, AssignRoleRequest, AuditEvent, EndAssignmentRequest, EndRoleAssignmentRequest, ReassignRequest,
  SetAccessScopeRequest, SetMembershipStatusRequest, SetPrimaryBranchRequest, UserAccess, UserRoleAssignments,
} from './user-access.models';

const USERS = `${environment.apiBaseUrl}/users`;

/** Company membership + branch allocations (UserAccessController) and role assignments (RoleAssignmentsController). Every write returns the refreshed view. */
@Injectable({ providedIn: 'root' })
export class UserAccessService {
  private readonly http = inject(HttpClient);

  get(userId: string): Observable<UserAccess> {
    return this.http.get<UserAccess>(`${USERS}/${userId}/access`);
  }

  events(userId: string, page: number, pageSize = 20): Observable<PagedResult<AuditEvent>> {
    return this.http.get<PagedResult<AuditEvent>>(`${USERS}/${userId}/access/events`, { params: { page, pageSize } });
  }

  setScope(userId: string, request: SetAccessScopeRequest): Observable<UserAccess> {
    return this.http.put<UserAccess>(`${USERS}/${userId}/access/scope`, request);
  }

  addAssignment(userId: string, request: AddAssignmentRequest): Observable<UserAccess> {
    return this.http.post<UserAccess>(`${USERS}/${userId}/access/assignments`, request);
  }

  endAssignment(userId: string, assignmentId: string, request: EndAssignmentRequest): Observable<UserAccess> {
    return this.http.post<UserAccess>(`${USERS}/${userId}/access/assignments/${assignmentId}/end`, request);
  }

  setPrimary(userId: string, request: SetPrimaryBranchRequest): Observable<UserAccess> {
    return this.http.post<UserAccess>(`${USERS}/${userId}/access/primary`, request);
  }

  reassign(userId: string, request: ReassignRequest): Observable<UserAccess> {
    return this.http.post<UserAccess>(`${USERS}/${userId}/access/reassign`, request);
  }

  setStatus(userId: string, request: SetMembershipStatusRequest): Observable<UserAccess> {
    return this.http.post<UserAccess>(`${USERS}/${userId}/access/status`, request);
  }

  roles(userId: string): Observable<UserRoleAssignments> {
    return this.http.get<UserRoleAssignments>(`${USERS}/${userId}/roles`);
  }

  assignRole(userId: string, request: AssignRoleRequest): Observable<UserRoleAssignments> {
    return this.http.post<UserRoleAssignments>(`${USERS}/${userId}/roles`, request);
  }

  endRole(userId: string, assignmentId: string, request: EndRoleAssignmentRequest): Observable<UserRoleAssignments> {
    return this.http.post<UserRoleAssignments>(`${USERS}/${userId}/roles/${assignmentId}/end`, request);
  }

  /** Branches the caller can reach (needs no permission). */
  branches(): Observable<AccessibleBranch[]> {
    return this.http.get<AccessibleBranch[]>(`${environment.apiBaseUrl}/branches/accessible`);
  }
}
