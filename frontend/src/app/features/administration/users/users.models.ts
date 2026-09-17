export type UserAccountStatus = 'Active' | 'Invited' | 'Disabled';

export interface UserListItem {
  id: string;
  firstName: string;
  lastName: string;
  username: string;
  email: string;
  roleId: string;
  roleName: string;
  status: UserAccountStatus;
  lastLoginAt: string | null;
  createdAt: string;
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
