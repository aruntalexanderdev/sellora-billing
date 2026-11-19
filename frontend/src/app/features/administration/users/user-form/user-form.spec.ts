import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { ContextService } from '../../../../core/context/context.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { RolesService } from '../../roles/roles.service';
import { UsersService } from '../users.service';
import { UserForm } from './user-form';

const role = (id: string, over: object = {}) => ({ id, name: `Role ${id}`, description: null, isSystem: false, isActive: true, userCount: 0, scopeType: 'Branch', branchId: 'b1', branchName: 'North', ...over });

function mount(perms: string[]) {
  const has = (c: string) => perms.includes(c);
  const list = vi.fn(() => of([role('r1'), role('r2', { isActive: false })]));
  const create = vi.fn(() => of({}));
  TestBed.configureTestingModule({
    imports: [UserForm],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({}) } } },
      { provide: AuthService, useValue: { hasPermission: has } },
      { provide: ContextService, useValue: { hasPermission: has, workingBranchId: () => 'b1', workingBranch: () => ({ id: 'b1', name: 'North' }) } },
      { provide: RolesService, useValue: { list } },
      { provide: UsersService, useValue: { create, update: vi.fn(), get: vi.fn() } },
      { provide: ToastService, useValue: { show: vi.fn() } },
    ],
  });
  vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(UserForm);
  fixture.detectChanges();
  return { component: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, list, create };
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

describe('UserForm company mode (Users.Create)', () => {
  it('loads company roles with no filter and shows no banner', () => {
    const { component, el, list } = mount(['Users.Create']);
    expect(component.branchMode).toBe(false);
    expect(list).toHaveBeenCalledWith();
    expect(el.textContent).not.toContain('will be added to');
  });
});
