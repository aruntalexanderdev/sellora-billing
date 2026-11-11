import type { RoleScope } from '../../../core/auth/auth.models';

export type { RoleScope };

/** Mirrors Api/Contracts/RoleContracts.cs RoleListItemDto. */
export interface RoleListItem {
  id: string;
  name: string;
  description: string | null;
  isSystem: boolean;
  isActive: boolean;
  userCount: number;
  scopeType: RoleScope;
  branchId: string | null;
  branchName: string | null;
}

/** Mirrors RoleDetailDto. */
export interface RoleDetail {
  id: string;
  name: string;
  description: string | null;
  isSystem: boolean;
  isActive: boolean;
  permissionCodes: string[];
  scopeType: RoleScope;
  branchId: string | null;
  branchName: string | null;
}

/** Mirrors CreateRoleRequest: a Branch role needs `branchId` and only branch-eligible codes. */
export interface CreateRoleRequest {
  name: string;
  description?: string | null;
  permissionCodes: string[];
  scopeType?: RoleScope;
  branchId?: string | null;
}

/** Mirrors UpdateRoleRequest. Scope fields are immutable: the client never sends them on update. */
export interface UpdateRoleRequest {
  name: string;
  description?: string | null;
  permissionCodes: string[];
}

/** Mirrors RoleContracts.cs BranchMemberDto. */
export interface BranchMember {
  userId: string;
  membershipId: string;
  firstName: string;
  lastName: string;
  username: string;
  membershipStatus: 'Active' | 'Suspended' | 'Revoked';
  isPrimary: boolean;
}

export interface RoleListFilter {
  scope?: RoleScope;
  branchId?: string | null;
}
