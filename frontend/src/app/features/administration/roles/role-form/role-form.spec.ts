import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { PermissionsService } from '../../permissions/permissions.service';
import { UserAccessService } from '../../users/user-access/user-access.service';
import { RolesService } from '../roles.service';
import type { RoleDetail } from '../roles.models';
import { RoleForm } from './role-form';

const catalog = [
  { code: 'Sales.View', module: 'Sales', action: 'View', description: '' },
  { code: 'Inventory.Edit', module: 'Inventory', action: 'Edit', description: '' },
];

function setup(id: string | null, role?: Partial<RoleDetail>) {
  const update = vi.fn(() => of({}));
  const create = vi.fn(() => of({}));
  TestBed.configureTestingModule({
    imports: [RoleForm],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap(id ? { id } : {}) } } },
      { provide: PermissionsService, useValue: { list: () => of(catalog) } },
      { provide: UserAccessService, useValue: { branches: () => of([{ id: 'b1', code: 'N', name: 'North', isDefault: false }]) } },
      {
        provide: RolesService,
        useValue: {
          create, update,
          get: () => of({ id: 'r1', name: 'Cashier', description: null, isSystem: false, isActive: true, permissionCodes: ['Sales.View'],
            scopeType: 'Branch', branchId: 'b1', branchName: 'North', ...role }),
        },
      },
    ],
  });
  vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(RoleForm);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, update, create };
}

describe('RoleForm scope', () => {
  it('edit: scope and branch are read-only and never sent', () => {
    const { component, update, el } = setup('r1');
    expect(component.form.controls.scopeType.disabled).toBe(true);
    expect(component.form.controls.branchId.disabled).toBe(true);
    expect(component.form.getRawValue().scopeType).toBe('Branch');
    expect(el.textContent).toContain('cannot be changed');

    component.submit();
    const sent = (update.mock.calls.at(-1) as unknown as [string, Record<string, unknown>])[1];
    expect(sent).not.toHaveProperty('scopeType');
    expect(sent).not.toHaveProperty('branchId');
  });

  it('edit of a branch role lists only branch-eligible permissions', () => {
    const { component } = setup('r1');
    expect(component.visibleCatalog().map((e) => e.code)).toEqual(['Sales.View']);
  });

  it('create: choosing Branch requires a branch, restricts the catalogue and sends scope + branch', () => {
    const { component, create } = setup(null);
    component.form.patchValue({ name: 'Cashier' });
    component.form.controls.scopeType.setValue('Branch');
    expect(component.visibleCatalog().map((e) => e.code)).toEqual(['Sales.View']);

    component.submit();
    expect(create).not.toHaveBeenCalled(); // branch missing

    component.form.controls.branchId.setValue('b1');
    component.toggle('Sales.View');
    component.submit();
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ scopeType: 'Branch', branchId: 'b1', permissionCodes: ['Sales.View'] }));
  });
});
