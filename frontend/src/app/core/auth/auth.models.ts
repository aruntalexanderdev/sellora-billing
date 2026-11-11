export type CompanyAccessScope = 'CompanyWide' | 'AssignedBranches';
export type RoleScope = 'Company' | 'Branch';

/** Mirrors Api/Contracts/AuthContracts.cs AllowedBranchDto. */
export interface AllowedBranch {
  id: string;
  code: string;
  name: string;
  isPrimary: boolean;
  isDefault: boolean;
}

/** Mirrors AuthRoleDto. */
export interface AuthRole {
  id: string;
  name: string;
  scopeType: RoleScope;
  branchId: string | null;
}

/** Mirrors CompanyOptionDto. */
export interface CompanyOption {
  id: string;
  name: string;
}

/** Mirrors GovernanceHintDto (a UI hint only; the server re-checks). */
export interface GovernanceHint {
  memberId: string;
  title: string;
  permissions: string[];
}

/** Mirrors UserSummaryDto (login / refresh / switch-company). `permissions` here are primary-branch permissions; the ContextService (GET /auth/me) is authoritative. */
export interface AuthenticatedUser {
  id: string;
  firstName: string;
  lastName: string;
  username: string;
  email: string;
  roleName: string;
  permissions: string[];
  companyId: string;
  companyName: string;
  accessScope: CompanyAccessScope;
  primaryBranchId: string | null;
  allowedBranches: AllowedBranch[];
  roles?: AuthRole[] | null;
}

/** Mirrors MeResponse: UserSummaryDto + companies + governance; permissions are those at the working branch. */
export interface MeResponse extends AuthenticatedUser {
  /** D13: company-level codes plus codes held at any allowed branch. Used for navigation in All-branches mode; absent on older servers. */
  permissionsAnyBranch?: string[];
  companies: CompanyOption[];
  governance: GovernanceHint | null;
}

export interface AuthResponse {
  accessToken: string;
  accessTokenExpiresAt: string;
  refreshToken: string;
  refreshTokenExpiresAt: string;
  user: AuthenticatedUser;
}

export interface LoginRequest {
  usernameOrEmail: string;
  password: string;
}

export interface SwitchCompanyRequest {
  companyId: string;
  refreshToken: string;
}

export interface ApiProblem {
  title?: string;
  detail?: string;
  status?: number;
  code?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
}
