import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ContextService } from '../../../../core/context/context.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { RolesService } from '../../roles/roles.service';
import { UsersService } from '../users.service';
import type { BranchAssignment, UserAccess, UserRoleAssignments } from './user-access.models';
import { UserAccessPage } from './user-access';
import { UserAccessService } from './user-access.service';

const NOW = '2026-01-01T00:00:00Z';
const alloc = (o: Partial<BranchAssignment>): BranchAssignment => ({
  id: 'a1', branchId: 'b1', branchCode: 'MAIN', branchName: 'Main', isPrimary: true, status: 'Active', startAt: NOW, endAt: null,
  assignmentReason: null, endReason: null, createdBy: null, endedBy: null, ...o,
});
const ACCESS: UserAccess = {
  membershipId: 'm1', userId: 'u9', status: 'Active', accessScope: 'AssignedBranches', accessVersion: 3,
  current: [alloc({}), alloc({ id: 'a2', branchId: 'b2', branchCode: 'NTH', branchName: 'North', isPrimary: false })], history: [],
};
const ROLES: UserRoleAssignments = {
  membershipId: 'm1', userId: 'u9',
  current: [{ id: 'ra1', roleId: 'r1', roleName: 'Cashier', scopeType: 'Branch', branchId: 'b1', branchName: 'Main', isSystem: false, status: 'Active', startAt: NOW, endAt: null,
    assignmentReason: null, endReason: null, createdBy: null, endedBy: null }],
  history: [],
};
const BRANCHES = [
  { id: 'b1', code: 'MAIN', name: 'Main', isDefault: true },
  { id: 'b2', code: 'NTH', name: 'North', isDefault: false },
  { id: 'b3', code: 'STH', name: 'South', isDefault: false },
];

function mount(perms: string[], query: Record<string, string> = {}) {
  const service = {
    get: vi.fn(() => of(ACCESS)), roles: vi.fn(() => of(ROLES)), branches: vi.fn(() => of(BRANCHES)),
    events: vi.fn(() => of({ items: [{ id: 'e1', occurredAt: NOW, actorUserId: null, actorName: 'Asha', action: 'AllocationAdded', targetType: 'x', targetId: 'y', reason: 'because', before: null, after: null, correlationId: null }], total: 1, page: 1, pageSize: 20 })),
    addAssignment: vi.fn(() => of(ACCESS)), setPrimary: vi.fn(() => of(ACCESS)), reassign: vi.fn(() => of(ACCESS)), endAssignment: vi.fn(() => of(ACCESS)),
    setScope: vi.fn(() => of(ACCESS)), setStatus: vi.fn(() => of(ACCESS)), assignRole: vi.fn(() => of(ROLES)), endRole: vi.fn(() => of(ROLES)),
  };
  const toast = { show: vi.fn() };
  TestBed.configureTestingModule({
    imports: [UserAccessPage],
    providers: [
      provideRouter([]),
      { provide: UserAccessService, useValue: service },
      { provide: UsersService, useValue: { get: () => of({ firstName: 'Ravi', lastName: 'Kumar' }) } },
      { provide: RolesService, useValue: { list: vi.fn(() => of([{ id: 'r1', name: 'Cashier', description: null, isSystem: false, isActive: true, userCount: 1, scopeType: 'Company', branchId: null, branchName: null, holdsBranchAdministration: false }])) } },
      { provide: ContextService, useValue: { hasPermission: (c: string) => perms.includes(c) } },
      { provide: ToastService, useValue: toast },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'u9' }), queryParamMap: convertToParamMap(query) } } },
    ],
  });
  const fixture = TestBed.createComponent(UserAccessPage);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const click = (text: string, scope: ParentNode = el) => {
    const b = Array.from(scope.querySelectorAll('button')).find((x) => x.textContent?.trim() === text);
    expect(b, `button "${text}"`).toBeTruthy();
    b!.click();
    fixture.detectChanges();
  };
  const setValue = (selector: string, value: string) => {
    const field = el.querySelector<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>(selector)!;
    field.value = value;
    field.dispatchEvent(new Event(field instanceof HTMLSelectElement ? 'change' : 'input'));
    fixture.detectChanges();
  };
  const submit = () => {
    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  };
  return { fixture, el, service, toast, click, setValue, submit, component: fixture.componentInstance };
}

const MANAGE = ['Company.Users.ManageBranchAccess', 'Company.Users.AssignRoles'];
const tabs = (el: HTMLElement) => Array.from(el.querySelectorAll('[role="tab"]')).map((t) => t.textContent?.trim());

describe('UserAccessPage tabs', () => {
  it('shows Access, Roles and (only with ManageBranchAccess) Audit events tabs', () => {
    expect(tabs(mount(MANAGE).el)).toEqual(['Access', 'Roles', 'Audit events']);
    TestBed.resetTestingModule();
    expect(tabs(mount([]).el)).toEqual(['Access', 'Roles']);
  });

  it('Access tab lists allocations; Roles tab lists role assignments; Audit tab loads events lazily', () => {
    const { el, service, click, fixture } = mount(MANAGE);
    expect(el.textContent).toContain('Current allocations');
    expect(el.textContent).toContain('North');
    expect(service.events).not.toHaveBeenCalled();
    click('Roles');
    expect(el.textContent).toContain('Cashier');
    expect(el.textContent).toContain('Branch · Main');
    click('Audit events');
    expect(service.events).toHaveBeenCalledWith('u9', 1);
    fixture.detectChanges();
    expect(el.textContent).toContain('AllocationAdded');
    expect(el.textContent).toContain('because');
  });

  it('opens on the Roles tab with ?tab=roles', () => {
    const { el } = mount(MANAGE, { tab: 'roles' });
    expect(el.textContent).toContain('Current roles');
  });

  it('hides every access action without ManageBranchAccess / AssignRoles', () => {
    const { el, click } = mount([]);
    const labels = () => Array.from(el.querySelectorAll('button')).map((b) => b.textContent?.trim());
    expect(labels()).not.toContain('+ Add allocation');
    expect(labels()).not.toContain('Change access scope');
    expect(labels()).not.toContain('Suspend membership');
    expect(labels()).not.toContain('Make primary');
    click('Roles');
    expect(labels()).not.toContain('+ Assign role');
    expect(labels()).not.toContain('End');
  });
});

describe('UserAccessPage audited actions send the reason', () => {
  it('add allocation: needs a branch and a reason, then posts both', () => {
    const { el, service, click, setValue, submit } = mount(MANAGE);
    click('+ Add allocation');
    submit();
    expect(service.addAssignment).not.toHaveBeenCalled();
    expect(el.textContent).toContain('A reason is required.');

    setValue('#alloc-branch', 'b3');
    setValue('#panel-reason', '   ');
    submit();
    expect(service.addAssignment).not.toHaveBeenCalled();

    setValue('#panel-reason', '  Covers the south counter  ');
    submit();
    expect(service.addAssignment).toHaveBeenCalledWith('u9', { branchId: 'b3', isPrimary: false, reason: 'Covers the south counter' });
  });

  it('offers only branches that are not already allocated', () => {
    const { el, click } = mount(MANAGE);
    click('+ Add allocation');
    const values = Array.from(el.querySelectorAll<HTMLOptionElement>('#alloc-branch option')).map((o) => o.value);
    expect(values).toEqual(['', 'b3']);
  });

  it('make primary / end allocation require a reason and post it', () => {
    const { service, click, setValue, submit, el, fixture } = mount(MANAGE);
    click('Make primary');
    submit();
    expect(service.setPrimary).not.toHaveBeenCalled();
    setValue('#panel-reason', 'Manager moved');
    submit();
    expect(service.setPrimary).toHaveBeenCalledWith('u9', { branchId: 'b2', reason: 'Manager moved' });

    const endButtons = Array.from(el.querySelectorAll('.dt-cell-action')).filter((b) => b.textContent?.trim() === 'End');
    (endButtons[1] as HTMLButtonElement).click();
    fixture.detectChanges();
    submit();
    expect(service.endAssignment).not.toHaveBeenCalled();
    setValue('#panel-reason', 'Left the branch');
    submit();
    expect(service.endAssignment).toHaveBeenCalledWith('u9', 'a2', { reason: 'Left the branch' });
  });

  it('membership suspend requires a reason and posts the status with it', () => {
    const { service, click, setValue, submit } = mount(MANAGE);
    click('Suspend membership');
    submit();
    expect(service.setStatus).not.toHaveBeenCalled();
    setValue('#panel-reason', 'Investigation');
    submit();
    expect(service.setStatus).toHaveBeenCalledWith('u9', expect.objectContaining({ status: 'Suspended', reason: 'Investigation' }));
  });

  it('change access scope to CompanyWide sends no branches and the reason', () => {
    const { service, click, setValue, submit } = mount(MANAGE);
    click('Change access scope');
    setValue('#scope-select', 'CompanyWide');
    submit();
    expect(service.setScope).not.toHaveBeenCalled();
    setValue('#panel-reason', 'Promoted');
    submit();
    expect(service.setScope).toHaveBeenCalledWith('u9', { accessScope: 'CompanyWide', branchIds: null, primaryBranchId: null, reason: 'Promoted' });
  });

  it('assign role: reason is optional (null when blank), role is required', () => {
    const { el, service, click, setValue, submit } = mount(MANAGE);
    click('Roles');
    click('+ Assign role');
    expect(el.textContent).toContain('Reason (optional)');
    submit();
    expect(service.assignRole).not.toHaveBeenCalled();
    expect(el.textContent).toContain('Choose a role.');
    setValue('#role-pick', 'r1');
    submit();
    expect(service.assignRole).toHaveBeenCalledWith('u9', { roleId: 'r1', reason: null });
  });

  it('end role assignment requires a reason', () => {
    const { service, click, setValue, submit, el } = mount(MANAGE);
    click('Roles');
    click('End');
    submit();
    expect(service.endRole).not.toHaveBeenCalled();
    expect(el.textContent).toContain('A reason is required.');
    setValue('#panel-reason', 'No longer needed');
    submit();
    expect(service.endRole).toHaveBeenCalledWith('u9', 'ra1', { reason: 'No longer needed' });
  });

  it('a successful action toasts, closes the panel and refreshes a loaded audit tab', () => {
    const { el, toast, service, click, setValue, submit } = mount(MANAGE);
    click('Audit events');
    click('Access');
    click('Make primary');
    setValue('#panel-reason', 'Why not');
    submit();
    expect(toast.show).toHaveBeenCalledWith('Primary branch changed.');
    expect(el.querySelector('form.panel')).toBeNull();
    expect(service.events).toHaveBeenCalledTimes(2);
  });

  it('renders a server field error under its control and keeps the panel open', () => {
    const { el, service, click, setValue, submit } = mount(MANAGE);
    service.addAssignment.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { branchId: ['Branch is inactive.'] } } })) as never);
    click('+ Add allocation');
    setValue('#alloc-branch', 'b3');
    setValue('#panel-reason', 'r');
    submit();
    expect(el.querySelector('form.panel')).not.toBeNull();
    expect(el.querySelector('#alloc-branch')!.parentElement!.textContent).toContain('Branch is inactive.');
  });
});

describe('UserAccessPage for a branch-only administrator (Branch.Users.View + Branch.Users.AssignRoles)', () => {
  const BRANCH_ADMIN = ['Branch.Users.View', 'Branch.Users.AssignRoles'];

  it('requests only access + roles of the user: no events, allocations or scope controls', () => {
    const { el, service, component } = mount(BRANCH_ADMIN);
    expect(service.get).toHaveBeenCalledWith('u9');
    expect(service.roles).toHaveBeenCalledWith('u9');
    expect(service.events).not.toHaveBeenCalled();
    expect(tabs(el)).toEqual(['Access', 'Roles']);
    expect(el.textContent).not.toContain('Current allocations');
    expect(el.textContent).not.toContain('Change access scope');
    expect(component.canViewAllocations()).toBe(false);
  });

  it('assigns branch roles only and excludes roles holding branch administration', () => {
    const { component, click, el, fixture } = mount(BRANCH_ADMIN, { tab: 'roles' });
    const rolesService = TestBed.inject(RolesService) as unknown as { list: ReturnType<typeof vi.fn> };
    const r = (id: string, admin: boolean) => ({ id, name: id, description: null, isSystem: false, isActive: true, userCount: 0, scopeType: 'Branch', branchId: 'b1', branchName: 'Main', holdsBranchAdministration: admin });
    rolesService.list.mockReturnValue(of([r('plain', false), r('admin', true)]));
    click('+ Assign role');
    expect(Array.from(el.querySelectorAll('#role-scope option')).map((o) => (o as HTMLOptionElement).value)).toEqual(['Branch']);
    component.form.patchValue({ branchId: 'b1' });
    component.loadAssignable();
    fixture.detectChanges();
    expect(component.assignable().map((x) => x.id)).toEqual(['plain']);
  });

  it('a company assigner still sees roles holding branch administration', () => {
    const { component, click } = mount(MANAGE, { tab: 'roles' });
    const rolesService = TestBed.inject(RolesService) as unknown as { list: ReturnType<typeof vi.fn> };
    rolesService.list.mockReturnValue(of([{ id: 'adm', name: 'adm', description: null, isSystem: false, isActive: true, userCount: 0, scopeType: 'Company', branchId: null, branchName: null, holdsBranchAdministration: true }]));
    click('+ Assign role');
    expect(component.assignable().map((x) => x.id)).toEqual(['adm']);
  });
});
