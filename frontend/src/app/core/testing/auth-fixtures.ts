import type { AllowedBranch, AuthResponse, AuthenticatedUser, MeResponse } from '../auth/auth.models';

// Plain fixtures shared by core specs (no test framework imports, so the app build can type-check it).

export const MAIN: AllowedBranch = { id: 'b-main', code: 'MAIN', name: 'Main Branch', isPrimary: true, isDefault: true };
export const NORTH: AllowedBranch = { id: 'b-north', code: 'NTH', name: 'North Branch', isPrimary: false, isDefault: false };

export function user(over: Partial<AuthenticatedUser> = {}): AuthenticatedUser {
  return {
    id: 'u1', firstName: 'Asha', lastName: 'Rao', username: 'asha', email: 'asha@example.test', roleName: 'Manager',
    permissions: [], companyId: 'c1', companyName: 'Sellora Mart', accessScope: 'AssignedBranches',
    primaryBranchId: MAIN.id, allowedBranches: [MAIN, NORTH], roles: [], ...over,
  };
}

export function me(over: Partial<MeResponse> = {}): MeResponse {
  return {
    ...user(), permissions: ['Sales.View', 'Inventory.View'],
    companies: [{ id: 'c1', name: 'Sellora Mart' }], governance: null, ...over,
  };
}

export function authResponse(over: Partial<AuthResponse> = {}): AuthResponse {
  return {
    accessToken: 'access-2', accessTokenExpiresAt: '2030-01-01T00:00:00Z', refreshToken: 'refresh-2',
    refreshTokenExpiresAt: '2030-01-02T00:00:00Z', user: user(), ...over,
  };
}

export function storeSession(accessToken = 'access-1', refreshToken = 'refresh-1', u: AuthenticatedUser = user()): void {
  localStorage.setItem('sellora.auth', JSON.stringify({ accessToken, refreshToken, user: u }));
}

export const branchKey = (userId = 'u1', companyId = 'c1') => `sellora.branch.${userId}.${companyId}`;

/** Lets pending promise continuations (after an awaited HTTP flush) run. */
export const tick = () => new Promise<void>((resolve) => setTimeout(resolve, 0));
