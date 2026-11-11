import {
  addAssignmentRequest, allocationActions, assignRoleRequest, assignableRoles, branchesToAdd, endAssignmentRequest, endRoleRequest,
  membershipActions, reassignRequest, scopeRequest, setPrimaryRequest, statusRequest,
} from './user-access.logic';
import type { RoleListItem } from '../../roles/roles.models';

describe('membership actions', () => {
  it('maps each status to the offered transitions', () => {
    expect(membershipActions('Active')).toEqual(['suspend', 'revoke']);
    expect(membershipActions('Suspended')).toEqual(['reactivate', 'revoke']);
    expect(membershipActions('Revoked')).toEqual(['reinstate']);
  });

  it('maps actions to the status request', () => {
    expect(statusRequest('suspend', 'on leave')).toEqual({ status: 'Suspended', reason: 'on leave', accessScope: null, branchIds: null, primaryBranchId: null });
    expect(statusRequest('revoke', 'left').status).toBe('Revoked');
    expect(statusRequest('reactivate', 'back').status).toBe('Active');
    expect(statusRequest('reactivate', 'back').accessScope).toBeNull();
  });

  it('reinstating a revoked membership decides the scope again (defaults the primary to the first branch)', () => {
    expect(statusRequest('reinstate', 'rehired', { accessScope: 'AssignedBranches', branchIds: ['b1', 'b2'], primaryBranchId: null })).toEqual({
      status: 'Active', reason: 'rehired', accessScope: 'AssignedBranches', branchIds: ['b1', 'b2'], primaryBranchId: 'b1',
    });
    expect(statusRequest('reinstate', 'rehired', { accessScope: 'CompanyWide', branchIds: ['b1'], primaryBranchId: 'b1' })).toMatchObject({
      status: 'Active', accessScope: 'CompanyWide', branchIds: null, primaryBranchId: null,
    });
  });
});

describe('allocation actions', () => {
  it('offers make-primary only on non-primary allocations and nothing without manage rights', () => {
    expect(allocationActions({ isPrimary: false, status: 'Active' }, true)).toEqual(['makePrimary', 'reassign', 'end']);
    expect(allocationActions({ isPrimary: true, status: 'Active' }, true)).toEqual(['reassign', 'end']);
    expect(allocationActions({ isPrimary: false, status: 'Active' }, false)).toEqual([]);
    expect(allocationActions({ isPrimary: false, status: 'Ended' }, true)).toEqual([]);
  });

  it('only offers branches without an active allocation', () => {
    const branches = [{ id: 'b1' }, { id: 'b2' }, { id: 'b3' }];
    expect(branchesToAdd(branches, [{ branchId: 'b2' }])).toEqual([{ id: 'b1' }, { id: 'b3' }]);
  });
});

describe('request builders', () => {
  it('builds the allocation requests', () => {
    expect(addAssignmentRequest('b1', true, 'cover')).toEqual({ branchId: 'b1', isPrimary: true, reason: 'cover' });
    expect(endAssignmentRequest('closed')).toEqual({ reason: 'closed' });
    expect(setPrimaryRequest('b2', 'moved')).toEqual({ branchId: 'b2', reason: 'moved' });
    expect(reassignRequest('a1', 'b3', true, 'transfer')).toEqual({ fromAssignmentIds: ['a1'], toBranchId: 'b3', makePrimary: true, reason: 'transfer' });
  });

  it('scope change: CompanyWide carries no branches; AssignedBranches keeps a valid primary', () => {
    expect(scopeRequest('CompanyWide', ['b1'], 'b1', 'promoted')).toEqual({ accessScope: 'CompanyWide', branchIds: null, primaryBranchId: null, reason: 'promoted' });
    expect(scopeRequest('AssignedBranches', ['b1', 'b2'], 'b2', 'x')).toMatchObject({ branchIds: ['b1', 'b2'], primaryBranchId: 'b2' });
    expect(scopeRequest('AssignedBranches', ['b1', 'b2'], 'zzz', 'x').primaryBranchId).toBe('b1');
  });

  it('builds the role requests', () => {
    expect(assignRoleRequest('r1', null)).toEqual({ roleId: 'r1', reason: null });
    expect(endRoleRequest('left')).toEqual({ reason: 'left' });
  });
});

describe('assignableRoles', () => {
  const role = (over: Partial<RoleListItem>): RoleListItem => ({
    id: 'r', name: 'R', description: null, isSystem: false, isActive: true, userCount: 0, scopeType: 'Company', branchId: null, branchName: null, ...over,
  });
  const roles = [
    role({ id: 'c1' }), role({ id: 'c2', isActive: false }),
    role({ id: 'b1', scopeType: 'Branch', branchId: 'x' }), role({ id: 'b2', scopeType: 'Branch', branchId: 'y' }),
  ];

  it('company scope: active company roles only', () => {
    expect(assignableRoles(roles, 'Company', null).map((r) => r.id)).toEqual(['c1']);
  });
  it('branch scope: active roles of the chosen branch only', () => {
    expect(assignableRoles(roles, 'Branch', 'x').map((r) => r.id)).toEqual(['b1']);
  });
});
