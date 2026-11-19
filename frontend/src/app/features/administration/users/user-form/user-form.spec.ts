import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { ContextService } from '../../../../core/context/context.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { RolesService } from '../../roles/roles.service';
import { UsersService } from '../users.service';
import { UserForm } from './user-form';

const role = (id: string, over: object = {}) => ({ id, name: `Role ${id}`, description: null, isSystem: false, isActive: true, userCount: 0, scopeType: 'Branch', branchId: 'b1', branchName: 'North', holdsBranchAdministration: false, ...over });

function mount(perms: string[], id: string | null = null, roles = [role('r1'), role('r2', { isActive: false })]) {
  const has = (c: string) => perms.includes(c);
  const list = vi.fn(() => of(roles));
  const create = vi.fn(() => of({}));
  const update = vi.fn(() => of({}));
  const get = vi.fn(() => of({ firstName: 'A', lastName: 'B', username: 'ab', email: 'a@b.co', phoneNumber: null, roleId: null, roles: [] }));
  TestBed.configureTestingModule({
    imports: [UserForm],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap(id ? { id } : {}) } } },
      { provide: AuthService, useValue: { hasPermission: has } },
      { provide: ContextService, useValue: { hasPermission: has, workingBranchId: () => 'b1', workingBranch: () => ({ id: 'b1', name: 'North' }) } },
      { provide: RolesService, useValue: { list } },
      { provide: UsersService, useValue: { create, update, get } },
      { provide: ToastService, useValue: { show: vi.fn() } },
    ],
  });
  vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(UserForm);
  fixture.detectChanges();
  return { component: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, list, create, update, fixture };
}

const fill = (c: UserForm) =>
  c.form.patchValue({ firstName: 'A', lastName: 'B', username: 'ab', email: 'a@b.co', roleId: 'r1', password: 'secret123', confirmPassword: 'secret123' });

describe('UserForm branch mode (Branch.Users.Create only)', () => {
  it('requests only branch roles of the working branch, active ones, and shows the banner', () => {
    const { component, el, list } = mount(['Branch.Users.Create']);
    expect(component.branchMode).toBe(true);
    expect(list).toHaveBeenCalledWith({ scope: 'Branch', branchId: 'b1' });
    expect(component.roles().map((r) => r.id)).toEqual(['r1']);
    expect(el.textContent).toContain('This user will be added to North.');
  });

  it('payload carries no scope or branch fields', () => {
    const { component, create } = mount(['Branch.Users.Create']);
    fill(component);
    component.submit();
    expect(create).toHaveBeenCalledTimes(1);
    const body = (create.mock.calls[0] as unknown[])[0] as Record<string, unknown>;
    expect(body['roleId']).toBe('r1');
    for (const k of ['accessScope', 'branchIds', 'primaryBranchId']) expect(k in body).toBe(false);
  });
});

describe('UserForm branch edit (Branch.Users.Edit only)', () => {
  it('has no role control or role select and the PUT omits roleId', () => {
    const { component, el, update, list } = mount(['Branch.Users.Edit'], 'u1');
    expect(component.form.get('roleId')).toBeNull();
    expect(el.querySelector('select')).toBeNull();
    expect(list).not.toHaveBeenCalled();
    component.form.patchValue({ firstName: 'Z' });
    component.submit();
    expect(update).toHaveBeenCalledTimes(1);
    const body = (update.mock.calls[0] as unknown[])[1] as Record<string, unknown>;
    expect('roleId' in body).toBe(false);
    expect(body['firstName']).toBe('Z');
  });

  it('company edit keeps the required role control and sends roleId', () => {
    const { component, update } = mount(['Users.Edit'], 'u1');
    expect(component.form.get('roleId')).not.toBeNull();
    component.form.patchValue({ roleId: 'r1' });
    component.submit();
    expect((update.mock.calls[0] as unknown[])[1]).toEqual(expect.objectContaining({ roleId: 'r1' }));
  });
});

describe('UserForm branch create role picker', () => {
  it('excludes roles holding branch administration (FI-D45)', () => {
    const { component } = mount(['Branch.Users.Create'], null, [role('r1'), role('r3', { holdsBranchAdministration: true })]);
    expect(component.roles().map((r) => r.id)).toEqual(['r1']);
  });
});

describe('UserForm company mode (Users.Create)', () => {
  it('loads company roles with no filter and shows no banner', () => {
    const { component, el, list } = mount(['Users.Create']);
    expect(component.branchMode).toBe(false);
    expect(list).toHaveBeenCalledWith();
    expect(el.textContent).not.toContain('will be added to');
  });
});
