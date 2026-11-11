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
  { code: 'Roles.Create', module: 'Administration', action: 'Create', description: 'Create roles', scope: 'CompanyOnly' },
];

function mount(perms: string[]) {
  const create = vi.fn(() => of({}));
  const has = (c: string) => perms.includes(c);
  TestBed.configureTestingModule({
    imports: [RoleForm],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({}) } } },
      { provide: AuthService, useValue: { hasPermission: has } },
      { provide: ContextService, useValue: { hasPermission: has } },
      { provide: PermissionsService, useValue: { list: () => of(catalog) } },
      { provide: UserAccessService, useValue: { branches: () => of([{ id: 'b1', code: 'N', name: 'North', isDefault: false }]) } },
      { provide: RolesService, useValue: { create, update: vi.fn(), get: vi.fn() } },
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
    expect(component.visibleCatalog().map((e) => e.code)).toEqual(['Sales.View', 'Roles.Create']);
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
