export interface PermissionCatalogEntry {
  code: string;
  module: string;
  action: string;
  description: string;
  /** Scope metadata. Returned by GET /permissions; `roles.logic.ts` prefers it and only falls back to the mirrored branch-eligible list when absent. */
  scope?: 'CompanyOnly' | 'CompanyOrBranch';
}
