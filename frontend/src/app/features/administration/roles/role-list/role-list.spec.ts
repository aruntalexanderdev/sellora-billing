import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { UserAccessService } from '../../users/user-access/user-access.service';
import { RolesService } from '../roles.service';
import type { RoleListItem } from '../roles.models';
import { RoleList } from './role-list';

const item = (over: Partial<RoleListItem>): RoleListItem =>
  ({ id: 'r', name: 'R', description: null, isSystem: false, isActive: true, userCount: 0, scopeType: 'Branch', branchId: 'b1', branchName: 'North', holdsBranchAdministration: false, ...over }) as RoleListItem;

function setup(perms: string[]) {
  const list = vi.fn(() => of([item({ id: 'c', scopeType: 'Company', branchId: null }), item({ id: 'b' })]));
  TestBed.configureTestingModule({
    imports: [RoleList],
    providers: [
      provideRouter([]),
      { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c) } },
      { provide: RolesService, useValue: { list } },
      { provide: UserAccessService, useValue: { branches: () => of([]) } },
    ],
  });
  const fixture = TestBed.createComponent(RoleList);
  fixture.detectChanges();
  return { component: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, list };
}

function setup2(perms: string[]) {
  TestBed.resetTestingModule();
  return setup(perms).component;
}

describe('RoleList for a branch administrator', () => {
  it('defaults to Branch scope, hides the Company option, and loads branch roles', () => {
    const { component, el, list } = setup(['Branch.Roles.View', 'Branch.Roles.Manage']);
    expect(component.scope()).toBe('Branch');
    expect(list).toHaveBeenCalledWith({ scope: 'Branch', branchId: null });
    expect(el.querySelector('option[value="Company"]')).toBeNull();
  });

  it('gates actions per row: Branch.Roles.Manage covers branch roles only', () => {
    const { component } = setup(['Branch.Roles.View', 'Branch.Roles.Manage']);
    expect(component.canCreate()).toBe(true);
    expect(component.can({ scopeType: 'Branch' }, 'Roles.Edit')).toBe(true);
    expect(component.can({ scopeType: 'Branch' }, 'Roles.Disable')).toBe(true);
    expect(component.can({ scopeType: 'Company' }, 'Roles.Edit')).toBe(false);
    expect(component.can({ scopeType: 'Company' }, 'Roles.Duplicate')).toBe(false);
  });

  it('hides Edit/Duplicate/Disable on roles holding branch administration unless the company code is held (FI-D45)', () => {
    const { component } = setup(['Branch.Roles.View', 'Branch.Roles.Manage']);
    const admin = { scopeType: 'Branch' as const, holdsBranchAdministration: true };
    expect(component.can(admin, 'Roles.Edit')).toBe(false);
    expect(component.can(admin, 'Roles.Duplicate')).toBe(false);
    expect(component.can(admin, 'Roles.Disable')).toBe(false);
    const company = setup2(['Roles.Edit']);
    expect(company.can(admin, 'Roles.Edit')).toBe(true);
  });

  it('view-only branch users get no actions', () => {
    const { component } = setup(['Branch.Roles.View']);
    expect(component.canCreate()).toBe(false);
    expect(component.can({ scopeType: 'Branch' }, 'Roles.Edit')).toBe(false);
  });

  it('company viewers default to Company scope and keep the option', () => {
    const { component, el } = setup(['Roles.View']);
    expect(component.scope()).toBe('Company');
    expect(el.querySelector('option[value="Company"]')).not.toBeNull();
  });
});
