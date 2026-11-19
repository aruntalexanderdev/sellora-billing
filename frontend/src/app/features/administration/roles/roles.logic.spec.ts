import type { PermissionCatalogEntry } from '../permissions/permissions.models';
import { canManageRole, buildCreateRoleRequest, buildUpdateRoleRequest, eligibleCatalog, isBranchEligible, pruneSelection, scopeLabel, BRANCH_ADMIN_CODES, isBranchAdminCode, withoutBranchAdminCodes } from './roles.logic';

const entry = (code: string, scope?: PermissionCatalogEntry['scope']): PermissionCatalogEntry => ({ code, module: 'M', action: 'A', description: '', scope });
const catalog = [entry('Sales.View'), entry('Sales.Create'), entry('Inventory.Edit'), entry('Masters.Edit'), entry('Branch.Roles.Manage')];

describe('branch-eligible permissions', () => {
  it('Branch roles only see branch-eligible codes; Company roles see everything', () => {
    expect(eligibleCatalog(catalog, 'Branch').map((e) => e.code)).toEqual(['Sales.View', 'Sales.Create', 'Branch.Roles.Manage']);
    expect(eligibleCatalog(catalog, 'Company')).toHaveLength(5);
  });

  it('prefers the server scope metadata when present', () => {
    expect(isBranchEligible(entry('Masters.Edit', 'CompanyOrBranch'))).toBe(true);
    expect(isBranchEligible(entry('Sales.View', 'CompanyOnly'))).toBe(false);
  });

  it('drops ineligible codes when a new role is switched to Branch', () => {
    const kept = pruneSelection(new Set(['Sales.View', 'Inventory.Edit']), catalog, 'Branch');
    expect([...kept]).toEqual(['Sales.View']);
    expect(pruneSelection(new Set(['Inventory.Edit']), catalog, 'Company').has('Inventory.Edit')).toBe(true);
  });
});

describe('role requests: scope is immutable', () => {
  it('create carries scope and branch', () => {
    expect(buildCreateRoleRequest({ name: ' Cashier ', description: '', scopeType: 'Branch', branchId: 'b1' }, ['Sales.View'])).toEqual({
      name: 'Cashier', description: null, permissionCodes: ['Sales.View'], scopeType: 'Branch', branchId: 'b1',
    });
    expect(buildCreateRoleRequest({ name: 'Mgr', description: 'd', scopeType: 'Company', branchId: 'ignored' }, []).branchId).toBeNull();
  });

  it('update never carries scopeType or branchId', () => {
    const req = buildUpdateRoleRequest({ name: 'Cashier', description: 'x' }, new Set(['Sales.View']));
    expect(Object.keys(req).sort()).toEqual(['description', 'name', 'permissionCodes']);
  });

  it('labels the scope for lists', () => {
    expect(scopeLabel({ scopeType: 'Company', branchName: null })).toBe('Company');
    expect(scopeLabel({ scopeType: 'Branch', branchName: 'North' })).toBe('Branch · North');
  });
});

describe('server scope preferred over the fallback list', () => {
  it('a code in the fallback list is excluded when the server says CompanyOnly, and vice versa', () => {
    const cat = [entry('Sales.View', 'CompanyOnly'), entry('Masters.Edit', 'CompanyOrBranch'), entry('Inventory.Edit')];
    expect(eligibleCatalog(cat, 'Branch').map((e) => e.code)).toEqual(['Masters.Edit']);
  });
});

describe('canManageRole', () => {
  const has = (...codes: string[]) => (c: string) => codes.includes(c);
  it('company roles need the Roles.* code; branch roles also accept Branch.Roles.Manage', () => {
    expect(canManageRole(has('Branch.Roles.Manage'), 'Company', 'Roles.Edit')).toBe(false);
    expect(canManageRole(has('Branch.Roles.Manage'), 'Branch', 'Roles.Edit')).toBe(true);
    expect(canManageRole(has('Roles.Edit'), 'Company', 'Roles.Edit')).toBe(true);
    expect(canManageRole(has('Branch.Roles.View'), 'Branch', 'Roles.Edit')).toBe(false);
  });
});

describe('branch administration codes (FI-D44/FI-D45)', () => {
  const users = ['Branch.Users.View', 'Branch.Users.Create', 'Branch.Users.Edit', 'Branch.Users.Disable', 'Branch.Users.ResetPassword'];

  it('the five Branch.Users.* codes are branch-eligible (fallback list)', () => {
    for (const code of users) expect(isBranchEligible(entry(code))).toBe(true);
  });

  it('admin codes cover Branch.Users.*, Branch.Roles.View/Manage and Branch.Users.AssignRoles', () => {
    for (const code of [...users, 'Branch.Roles.View', 'Branch.Roles.Manage', 'Branch.Users.AssignRoles']) {
      expect(isBranchAdminCode(code)).toBe(true);
      expect(BRANCH_ADMIN_CODES.has(code)).toBe(true);
    }
    expect(isBranchAdminCode('Sales.View')).toBe(false);
    expect(isBranchAdminCode('Users.Create')).toBe(false);
  });

  it('withoutBranchAdminCodes strips only admin codes', () => {
    const list = [entry('Sales.View'), entry('Branch.Users.Create'), entry('Branch.Roles.Manage'), entry('Users.Create')];
    expect(withoutBranchAdminCodes(list).map((e) => e.code)).toEqual(['Sales.View', 'Users.Create']);
  });
});
