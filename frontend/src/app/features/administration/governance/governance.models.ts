export type GovernanceTitle = 'Owner' | 'BoardMember' | 'Director' | 'DelegatedAdministrator';
export type GovernanceMemberStatus = 'Active' | 'Ended';

export const GOVERNANCE_TITLES: readonly GovernanceTitle[] = ['Owner', 'BoardMember', 'Director', 'DelegatedAdministrator'];

/** Governance permission codes (backend GovernancePermissionCatalog). */
export const GOV = {
  view: 'Company.Governance.View',
  manageMembers: 'Company.Governance.ManageMembers',
  manageOwners: 'Company.Governance.ManageOwners',
  assignPermissions: 'Company.Governance.AssignPermissions',
  manageAdministrators: 'Company.Governance.ManageAdministrators',
} as const;

/** Mirrors Api/Contracts/GovernanceContracts.cs GovernancePermissionDto. */
export interface GovernancePermission {
  code: string;
  description: string;
}

/** Mirrors GovernanceMemberDto. */
export interface GovernanceMember {
  id: string;
  userId: string;
  displayName: string;
  title: GovernanceTitle;
  status: GovernanceMemberStatus;
  startAt: string;
  endAt: string | null;
  endReason: string | null;
  permissions: string[];
}

/** Mirrors AddGovernanceMemberRequest. */
export interface AddGovernanceMemberRequest {
  userId: string;
  title: GovernanceTitle;
  permissionCodes: string[] | null;
  reason: string | null;
}
/** Mirrors ChangeGovernanceTitleRequest. */
export interface ChangeGovernanceTitleRequest {
  title: GovernanceTitle;
  reason: string | null;
}
/** Mirrors SetGovernancePermissionsRequest. */
export interface SetGovernancePermissionsRequest {
  permissionCodes: string[];
  reason: string | null;
}
/** Mirrors EndGovernanceMemberRequest. */
export interface EndGovernanceMemberRequest {
  reason: string | null;
}
/** Mirrors GrantAdministratorRequest. */
export interface GrantAdministratorRequest {
  userId: string;
  reason: string | null;
}
/** Mirrors RemoveAdministratorRequest. */
export interface RemoveAdministratorRequest {
  replacementRoleId: string;
  reason: string | null;
}
