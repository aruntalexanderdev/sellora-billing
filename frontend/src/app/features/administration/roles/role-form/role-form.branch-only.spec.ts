import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { ContextService } from '../../../../core/context/context.service';
import type { PermissionCatalogEntry } from '../../permissions/permissions.models';
import { PermissionsService } from '../../permissions/permissions.service';
import { UserAccessService } from '../../users/user-access/user-access.service';
import { RolesService } from '../roles.service';
import { RoleForm } from './role-form';

const catalog: PermissionCatalogEntry[] = [
  { code: 'Sales.View', module: 'Sales', action: 'View', description: 'View sales', scope: 'CompanyOrBranch' },
  { code: 'Branch.Users.Create', module: 'Administration', action: 'Create', description: 'Create branch users', scope: 'CompanyOrBranch' },
  { code: 'Branch.Roles.Manage', module: 'Administration', action: 'Manage', description: 'Manage branch roles', scope: 'CompanyOrBranch' },
  { code: 'Roles.Create', module: 'Administration', action: 'Create', description: 'Create roles', scope: 'CompanyOnly' },
];

function mount(perms: string[], working: string | null = null, id: string | null = null, codes: string[] = ['Sales.View']) {
  const create = vi.fn(() => of({}));
  const update = vi.fn(() => of({}));
  const has = (c: string) => perms.includes(c);
  TestBed.configureTestingModule({
    imports: [RoleForm],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap(id ? { id } : {}) } } },
      { provide: AuthService, useValue: { hasPermission: has } },
      { provide: ContextService, useValue: { hasPermission: has, workingBranchId: () => working, workingBranch: () => (working ? { id: working, name: 'North' } : null) } },
      { provide: PermissionsService, useValue: { list: () => of(catalog) } },
      { provide: UserAccessService, useValue: { branches: () => of([{ id: 'b1', code: 'N', name: 'North', isDefault: false }]) } },
      { provide: RolesService, useValue: { create, update, get: () => of({ id: 'r1', name: 'Cashier', description: null, isSystem: false, isActive: true, permissionCodes: codes, scopeType: 'Branch', branchId: 'b1', branchName: 'North', holdsBranchAdministration: codes.some((c) => c.startsWith('Branch.')) }) } },
    ],
  });
  vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(RoleForm);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, create };
}

const scopeOptions = (el: HTMLElement) => Array.from(el.querySelectorAll<HTMLOptionElement>('#role-scope option')).map((o) => o.value);

describe('RoleForm branch-only mode (Branch.Roles.Manage without Roles.Create)', () => {
  it('locks the scope to Branch: only the Branch option, control disabled, branch picker shown and required', () => {
    const { component, el, fixture } = mount(['Branch.Roles.Manage']);
    expect(component.branchOnly).toBe(true);
    expect(scopeOptions(el)).toEqual(['Branch']);
    expect(component.form.controls.scopeType.disabled).toBe(true);
    expect(component.form.getRawValue().scopeType).toBe('Branch');
    expect(el.querySelector('#role-scope')!.hasAttribute('disabled')).toBe(true);
    expect(el.querySelector('#role-branch')).not.toBeNull();

    component.form.patchValue({ name: 'Cashier' });
    component.submit();
    fixture.detectChanges();
    expect(component.form.controls.branchId.errors?.['required']).toBeTruthy();
    expect(el.textContent).toContain('Choose the branch this role belongs to.');
  });

  it('presets and locks the branch to the working branch and sends it', () => {
    const { component, el, create } = mount(['Branch.Roles.Manage'], 'b1');
    expect(component.form.controls.branchId.value).toBe('b1');
    expect(component.form.controls.branchId.disabled).toBe(true);
    expect(el.querySelector('#role-branch')!.hasAttribute('disabled')).toBe(true);
    component.form.patchValue({ name: 'Cashier' });
    component.submit();
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ scopeType: 'Branch', branchId: 'b1' }));
  });

  it('hides branch administration codes from a branch-only actor but not from a company admin', () => {
    expect(mount(['Branch.Roles.Manage'], 'b1').component.visibleCatalog().map((e) => e.code)).toEqual(['Sales.View']);
    TestBed.resetTestingModule();
    const company = mount(['Roles.Create']).component;
    company.form.controls.scopeType.setValue('Branch');
    expect(company.visibleCatalog().map((e) => e.code)).toEqual(['Sales.View', 'Branch.Users.Create', 'Branch.Roles.Manage']);
  });

  it('only offers branch-eligible permissions', () => {
    const { component, el } = mount(['Branch.Roles.Manage']);
    expect(component.visibleCatalog().map((e) => e.code)).toEqual(['Sales.View']);
    expect(el.textContent).not.toContain('Create roles');
  });

  it('creates a Branch-scope role for the chosen branch (the disabled scope is still sent)', () => {
    const { component, create } = mount(['Branch.Roles.Manage']);
    component.form.patchValue({ name: 'Cashier', branchId: 'b1' });
    component.toggle('Sales.View');
    component.submit();
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ name: 'Cashier', scopeType: 'Branch', branchId: 'b1', permissionCodes: ['Sales.View'] }));
  });

  it('with Roles.Create both Company and Branch scopes are offered and the scope is editable', () => {
    const { component, el } = mount(['Roles.Create']);
    expect(component.branchOnly).toBe(false);
    expect(scopeOptions(el)).toEqual(['Company', 'Branch']);
    expect(component.form.controls.scopeType.enabled).toBe(true);
    expect(component.visibleCatalog().map((e) => e.code)).toEqual(['Sales.View', 'Branch.Users.Create', 'Branch.Roles.Manage', 'Roles.Create']);
  });

  it('renders a server scopeType error on a branch-only form under the scope control', () => {
    const { component, create, el, fixture } = mount(['Branch.Roles.Manage']);
    create.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { scopeType: ['Not allowed.'] } } })) as never);
    component.form.patchValue({ name: 'Cashier', branchId: 'b1' });
    component.submit();
    fixture.detectChanges();
    expect(el.textContent).toContain('Not allowed.');
  });
});

describe('RoleForm branchOnly is per action (FI-D45)', () => {
  it('edit with Roles.Edit but not Roles.Create is not branch-only; create without Roles.Create is', () => {
    expect(mount(['Roles.Edit'], null, 'r1').component.branchOnly).toBe(false);
    TestBed.resetTestingModule();
    expect(mount(['Branch.Roles.Manage'], null, 'r1').component.branchOnly).toBe(true);
    TestBed.resetTestingModule();
    expect(mount(['Roles.Edit']).component.branchOnly).toBe(true);
  });

  it('branch-only edit of a role holding admin codes shows a read-only notice and never saves', () => {
    const { component, el, fixture, update } = mount(['Branch.Roles.Manage'], null, 'r1', ['Sales.View', 'Branch.Users.Create']);
    fixture.detectChanges();
    expect(component.readOnlyAdminRole()).toBe(true);
    expect(el.querySelector('[data-testid="admin-role-notice"]')).not.toBeNull();
    component.submit();
    expect(update).not.toHaveBeenCalled();
  });

  it('branch-only edit of an ordinary role stays editable; a company editor is never read-only', () => {
    const plain = mount(['Branch.Roles.Manage'], null, 'r1');
    expect(plain.component.readOnlyAdminRole()).toBe(false);
    TestBed.resetTestingModule();
    const company = mount(['Roles.Edit'], null, 'r1', ['Sales.View', 'Branch.Users.Create']);
    expect(company.component.readOnlyAdminRole()).toBe(false);
  });
});
