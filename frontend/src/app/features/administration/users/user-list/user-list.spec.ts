import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { ContextService } from '../../../../core/context/context.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { RolesService } from '../../roles/roles.service';
import { UsersService } from '../users.service';
import { UserList } from './user-list';

function mount(perms: string[]) {
  const has = (c: string) => perms.includes(c);
  const rolesList = vi.fn(() => of([]));
  TestBed.configureTestingModule({
    imports: [UserList],
    providers: [
      provideRouter([]),
      { provide: AuthService, useValue: { hasPermission: has, hasAnyPermission: (cs: string[]) => cs.some(has) } },
      { provide: ContextService, useValue: { workingBranchId: () => 'b1', workingBranch: () => ({ id: 'b1', name: 'North' }) } },
      { provide: UsersService, useValue: { list: () => of({ items: [], total: 0, page: 1, pageSize: 20 }) } },
      { provide: RolesService, useValue: { list: rolesList } },
      { provide: ToastService, useValue: { show: vi.fn() } },
      { provide: ConfirmDialogService, useValue: { ask: vi.fn() } },
    ],
  });
  const fixture = TestBed.createComponent(UserList);
  fixture.detectChanges();
  return { component: fixture.componentInstance, rolesList };
}

describe('UserList gating', () => {
  it('branch codes enable the matching actions and load branch roles', () => {
    const { component, rolesList } = mount(['Branch.Users.View', 'Branch.Users.Create', 'Branch.Users.Disable']);
    expect(component.canCreate()).toBe(true);
    expect(component.canDisable()).toBe(true);
    expect(component.canEdit()).toBe(false);
    expect(component.canResetPassword()).toBe(false);
    expect(rolesList).toHaveBeenCalledWith({ scope: 'Branch', branchId: 'b1' });
  });

  it('company codes keep company behaviour and load company roles', () => {
    const { component, rolesList } = mount(['Users.View', 'Users.Edit', 'Users.ResetPassword']);
    expect(component.canEdit()).toBe(true);
    expect(component.canResetPassword()).toBe(true);
    expect(component.canCreate()).toBe(false);
    expect(rolesList).toHaveBeenCalledWith();
  });
});
