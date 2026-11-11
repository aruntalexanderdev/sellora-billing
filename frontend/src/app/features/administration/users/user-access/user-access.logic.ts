import type { CompanyAccessScope, RoleScope } from '../../../../core/auth/auth.models';
import type { RoleListItem } from '../../roles/roles.models';
import type {
  AddAssignmentRequest, AssignRoleRequest, BranchAssignment, EndAssignmentRequest, EndRoleAssignmentRequest, MembershipStatus,
  ReassignRequest, SetAccessScopeRequest, SetMembershipStatusRequest, SetPrimaryBranchRequest,
} from './user-access.models';

/** Membership-level actions offered for a status (the server re-validates every transition). */
export type MembershipAction = 'suspend' | 'revoke' | 'reactivate' | 'reinstate';

export function membershipActions(status: MembershipStatus): MembershipAction[] {
  switch (status) {
    case 'Active':
      return ['suspend', 'revoke'];
    case 'Suspended':
      return ['reactivate', 'revoke'];
    case 'Revoked':
      return ['reinstate']; // Revoked -> Active decides the access scope again
  }
}

/** Per-allocation actions on an Active allocation. */
export type AllocationAction = 'makePrimary' | 'reassign' | 'end';

export function allocationActions(a: Pick<BranchAssignment, 'isPrimary' | 'status'>, canManage: boolean): AllocationAction[] {
  if (!canManage || a.status !== 'Active') return [];
  return a.isPrimary ? ['reassign', 'end'] : ['makePrimary', 'reassign', 'end'];
}

/** Branches that can still be added (no Active allocation yet). */
export function branchesToAdd<B extends { id: string }>(branches: B[], current: Pick<BranchAssignment, 'branchId'>[]): B[] {
  const taken = new Set(current.map((c) => c.branchId));
  return branches.filter((b) => !taken.has(b.id));
}

export function addAssignmentRequest(branchId: string, isPrimary: boolean, reason: string | null): AddAssignmentRequest {
  return { branchId, isPrimary, reason };
}

export function endAssignmentRequest(reason: string | null): EndAssignmentRequest {
  return { reason };
}

export function setPrimaryRequest(branchId: string, reason: string | null): SetPrimaryBranchRequest {
  return { branchId, reason };
}

export function reassignRequest(fromAssignmentId: string, toBranchId: string, makePrimary: boolean, reason: string | null): ReassignRequest {
  return { fromAssignmentIds: [fromAssignmentId], toBranchId, makePrimary, reason };
}

/** CompanyWide carries no branches; AssignedBranches needs the chosen branches and a primary (defaults to the first). */
export function scopeRequest(
  accessScope: CompanyAccessScope, branchIds: string[], primaryBranchId: string | null, reason: string | null,
): SetAccessScopeRequest {
  if (accessScope === 'CompanyWide') return { accessScope, branchIds: null, primaryBranchId: null, reason };
  const primary = primaryBranchId && branchIds.includes(primaryBranchId) ? primaryBranchId : (branchIds[0] ?? null);
  return { accessScope, branchIds, primaryBranchId: primary, reason };
}

/** Suspend / revoke / reactivate send status + reason only; reinstating a revoked membership also decides the access scope again. */
export function statusRequest(
  action: MembershipAction, reason: string | null,
  reinstate?: { accessScope: CompanyAccessScope; branchIds: string[]; primaryBranchId: string | null },
): SetMembershipStatusRequest {
  const status: MembershipStatus = action === 'suspend' ? 'Suspended' : action === 'revoke' ? 'Revoked' : 'Active';
  if (action === 'reinstate' && reinstate) {
    const s = scopeRequest(reinstate.accessScope, reinstate.branchIds, reinstate.primaryBranchId, reason);
    return { status, reason, accessScope: s.accessScope, branchIds: s.branchIds, primaryBranchId: s.primaryBranchId };
  }
  return { status, reason, accessScope: null, branchIds: null, primaryBranchId: null };
}

export function assignRoleRequest(roleId: string, reason: string | null): AssignRoleRequest {
  return { roleId, reason };
}

export function endRoleRequest(reason: string): EndRoleAssignmentRequest {
  return { reason };
}

/** Roles that may be assigned in the chosen scope: active, matching scope (and branch for branch roles). */
export function assignableRoles(roles: RoleListItem[], scope: RoleScope, branchId: string | null): RoleListItem[] {
  return roles.filter((r) => r.isActive && (r.scopeType ?? 'Company') === scope && (scope === 'Company' || r.branchId === branchId));
}
