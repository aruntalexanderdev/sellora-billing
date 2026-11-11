export type UserAccountStatus = 'Active' | 'Invited' | 'Disabled';

/** Mirrors UserRoleSummaryDto (an Active role assignment). */
export interface UserRoleSummary {
  id: string;
  name: string;
  scopeType: 'Company' | 'Branch';
  branchId: string | null;
}

export interface UserListItem {
  id: string;
  firstName: string;
  lastName: string;
  username: string;
  email: string;
  /** First active company role (null when the user holds none). */
  roleId: string | null;
  roleName: string;
  status: UserAccountStatus;
  lastLoginAt: string | null;
  createdAt: string;
  roles?: UserRoleSummary[] | null;
}

export interface UserDetail extends UserListItem {
  phoneNumber: string | null;
}

export interface CreateUserRequest {
  firstName: string;
  lastName: string;
  username: string;
  email: string;
  phoneNumber?: string | null;
  password: string;
  confirmPassword: string;
  roleId: string;
}

export interface UpdateUserRequest {
  firstName: string;
  lastName: string;
  username: string;
  email: string;
  phoneNumber?: string | null;
  roleId: string;
}
