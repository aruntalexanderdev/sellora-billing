import type { PermissionCatalogEntry } from '../permissions/permissions.models';
import type { CreateRoleRequest, RoleScope, UpdateRoleRequest } from './roles.models';

/**
 * Mirror of backend PermissionCatalog.BranchEligibleCodes (Phase 7 D5). Fallback only: used when a GET /permissions entry carries no
 * `scope` (the server `scope` is preferred); the server stays authoritative (it rejects any other code on a branch role).
 */
export const BRANCH_ELIGIBLE_CODES: ReadonlySet<string> = new Set([
  'Sales.View', 'Sales.Create', 'Sales.PriceOverride', 'Sales.Edit', 'Sales.Receive', 'Sales.Return', 'Sales.Refund', 'Sales.Cancel',
  'Purchase.View', 'Purchase.Create', 'Purchase.Edit', 'Purchase.Pay', 'Purchase.Return', 'Purchase.Refund', 'Purchase.Cancel',
  'Inventory.Adjust', 'StockLocations.View', 'Inventory.View',
  'StockTransfers.View', 'StockTransfers.Create', 'StockTransfers.Approve', 'StockTransfers.Dispatch', 'StockTransfers.Receive', 'StockTransfers.Cancel',
  'Expenses.View', 'Expenses.Create', 'Expenses.Edit', 'Expenses.Cancel',
  'Customers.View',
  'Reports.Sales', 'Reports.Purchase', 'Reports.Stock', 'Reports.Movement',
  'Branch.Roles.View', 'Branch.Roles.Manage', 'Branch.Users.AssignRoles',
]);

export function isBranchEligible(entry: Pick<PermissionCatalogEntry, 'code' | 'scope'>): boolean {
  return entry.scope ? entry.scope === 'CompanyOrBranch' : BRANCH_ELIGIBLE_CODES.has(entry.code);
}

/** The catalogue a role of `scope` may pick from: everything for Company, only branch-eligible codes for Branch. */
export function eligibleCatalog(catalog: PermissionCatalogEntry[], scope: RoleScope): PermissionCatalogEntry[] {
  return scope === 'Branch' ? catalog.filter(isBranchEligible) : catalog;
}

/** Drops selected codes that the scope does not allow (when switching a new role to Branch). */
export function pruneSelection(selected: ReadonlySet<string>, catalog: PermissionCatalogEntry[], scope: RoleScope): Set<string> {
  const allowed = new Set(eligibleCatalog(catalog, scope).map((e) => e.code));
  return new Set([...selected].filter((c) => allowed.has(c)));
}

/** Create sends scope + branch; update never does (scope is immutable, the server rejects a change). */
export function buildCreateRoleRequest(
  v: { name: string; description: string; scopeType: RoleScope; branchId: string }, codes: Iterable<string>,
): CreateRoleRequest {
  return {
    name: v.name.trim(),
    description: v.description.trim() || null,
    permissionCodes: [...codes],
    scopeType: v.scopeType,
    branchId: v.scopeType === 'Branch' ? v.branchId : null,
  };
}

export function buildUpdateRoleRequest(v: { name: string; description: string }, codes: Iterable<string>): UpdateRoleRequest {
  return { name: v.name.trim(), description: v.description.trim() || null, permissionCodes: [...codes] };
}

/** Scope label for lists: "Company" or the branch name. */
export function scopeLabel(r: { scopeType: RoleScope; branchName: string | null }): string {
  return r.scopeType === 'Branch' ? `Branch${r.branchName ? ` · ${r.branchName}` : ''}` : 'Company';
}

/**
 * UI mirror of RoleRules.CanManageAsync (server stays authoritative): company roles need the company-level `Roles.*` code,
 * branch roles need that code or Branch.Roles.Manage.
 */
export function canManageRole(has: (code: string) => boolean, scope: RoleScope, companyCode: string): boolean {
  return has(companyCode) || (scope === 'Branch' && has('Branch.Roles.Manage'));
}
