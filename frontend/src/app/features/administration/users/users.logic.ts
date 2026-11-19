export type UserAdminMode = 'company' | 'branch' | null;

/** Company mode when the user holds the company code; otherwise branch mode when they hold the matching Branch.Users.* code. */
export function userAdminMode(has: (code: string) => boolean, companyCode: string, branchCode: string): UserAdminMode {
  if (has(companyCode)) return 'company';
  return has(branchCode) ? 'branch' : null;
}
