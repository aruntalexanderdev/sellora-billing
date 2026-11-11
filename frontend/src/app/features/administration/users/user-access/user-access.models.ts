import type { CompanyAccessScope, RoleScope } from '../../../../core/auth/auth.models';

export type AssignmentStatus = 'Active' | 'Ended';
export type MembershipStatus = 'Active' | 'Suspended' | 'Revoked';

/** Mirrors Api/Contracts/UserAccessContracts.cs BranchAssignmentDto. */
export interface BranchAssignment {
  id: string;
  branchId: string;
  branchCode: string;
  branchName: string;
  isPrimary: boolean;
  status: AssignmentStatus;
  startAt: string;
  endAt: string | null;
  assignmentReason: string | null;
  endReason: string | null;
  createdBy: string | null;
  endedBy: string | null;
}

/** Mirrors UserAccessDto. */
export interface UserAccess {
  membershipId: string;
  userId: string;
  status: MembershipStatus;
  accessScope: CompanyAccessScope;
  accessVersion: number;
  current: BranchAssignment[];
  history: BranchAssignment[];
}

/** Mirrors AuditEventDto. */
export interface AuditEvent {
  id: string;
  occurredAt: string;
  actorUserId: string | null;
  actorName: string | null;
  action: string;
  targetType: string;
  targetId: string;
  reason: string | null;
  before: unknown;
  after: unknown;
  correlationId: string | null;
}

/** Mirrors SetAccessScopeRequest. */
export interface SetAccessScopeRequest {
  accessScope: CompanyAccessScope;
  branchIds: string[] | null;
  primaryBranchId: string | null;
  reason: string | null;
}
/** Mirrors AddAssignmentRequest. */
export interface AddAssignmentRequest {
  branchId: string;
  isPrimary: boolean;
  reason: string | null;
}
/** Mirrors EndAssignmentRequest. */
export interface EndAssignmentRequest {
  reason: string | null;
}
/** Mirrors SetPrimaryBranchRequest. */
export interface SetPrimaryBranchRequest {
  branchId: string;
  reason: string | null;
}
/** Mirrors ReassignRequest. */
export interface ReassignRequest {
  fromAssignmentIds: string[];
  toBranchId: string;
  makePrimary: boolean;
  reason: string | null;
}
/** Mirrors SetMembershipStatusRequest (accessScope/branchIds/primaryBranchId only when reactivating a revoked membership). */
export interface SetMembershipStatusRequest {
  status: MembershipStatus;
  reason: string | null;
  accessScope: CompanyAccessScope | null;
  branchIds: string[] | null;
  primaryBranchId: string | null;
}

/** Mirrors RoleContracts.cs RoleAssignmentDto. */
export interface RoleAssignment {
  id: string;
  roleId: string;
  roleName: string;
  scopeType: RoleScope;
  branchId: string | null;
  branchName: string | null;
  isSystem: boolean;
  status: AssignmentStatus;
  startAt: string;
  endAt: string | null;
  assignmentReason: string | null;
  endReason: string | null;
  createdBy: string | null;
  endedBy: string | null;
}

/** Mirrors UserRoleAssignmentsDto. */
export interface UserRoleAssignments {
  membershipId: string;
  userId: string;
  current: RoleAssignment[];
  history: RoleAssignment[];
}

/** Mirrors AssignRoleRequest. */
export interface AssignRoleRequest {
  roleId: string;
  reason: string | null;
}
/** Mirrors EndRoleAssignmentRequest (reason required). */
export interface EndRoleAssignmentRequest {
  reason: string;
}
