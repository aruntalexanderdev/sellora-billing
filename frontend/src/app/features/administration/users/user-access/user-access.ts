import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import type { Observable } from 'rxjs';
import type { CompanyAccessScope, RoleScope } from '../../../../core/auth/auth.models';
import { applyProblem, toProblem } from '../../../../core/http/api-errors';
import { ContextService } from '../../../../core/context/context.service';
import type { AccessibleBranch } from '../../../../shared/branches/branch-select';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { UiButton } from '../../../../shared/ui/button/button';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableRowAction, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { normalizeReason } from '../../access-ui/reason-prompt';
import type { PagedResult } from '../../../../core/api.models';
import { RolesService } from '../../roles/roles.service';
import type { RoleListItem } from '../../roles/roles.models';
import { UsersService } from '../users.service';
import type { UserDetail } from '../users.models';
import {
  addAssignmentRequest, allocationActions, assignRoleRequest, assignableRoles, branchesToAdd, endAssignmentRequest, endRoleRequest,
  membershipActions, reassignRequest, scopeRequest, setPrimaryRequest, statusRequest, type MembershipAction,
} from './user-access.logic';
import { UserAccessService } from './user-access.service';
import type { AuditEvent, BranchAssignment, RoleAssignment, UserAccess, UserRoleAssignments } from './user-access.models';

type Tab = 'access' | 'roles' | 'audit';

type Panel =
  | { kind: 'add' }
  | { kind: 'primary'; a: BranchAssignment }
  | { kind: 'reassign'; a: BranchAssignment }
  | { kind: 'end'; a: BranchAssignment }
  | { kind: 'scope' }
  | { kind: 'status'; action: MembershipAction }
  | { kind: 'assignRole' }
  | { kind: 'endRole'; r: RoleAssignment };

const ALLOC_COLS: DataTableColumn[] = [
  { label: 'Branch', align: 'left' },
  { label: 'Primary', align: 'left' },
  { label: 'Since', align: 'left' },
  { label: 'Reason', align: 'left' },
  { label: '', align: 'right' },
];
const HISTORY_COLS: DataTableColumn[] = [
  { label: 'Branch', align: 'left' },
  { label: 'From', align: 'left' },
  { label: 'Ended', align: 'left' },
  { label: 'Ended by / reason', align: 'left' },
];
const ROLE_COLS: DataTableColumn[] = [
  { label: 'Role', align: 'left' },
  { label: 'Scope', align: 'left' },
  { label: 'Since', align: 'left' },
  { label: 'Reason', align: 'left' },
  { label: '', align: 'right' },
];
const ROLE_HISTORY_COLS: DataTableColumn[] = [
  { label: 'Role', align: 'left' },
  { label: 'Scope', align: 'left' },
  { label: 'From', align: 'left' },
  { label: 'Ended', align: 'left' },
  { label: 'Ended by / reason', align: 'left' },
];

const ACTION_LABELS: Record<MembershipAction, string> = {
  suspend: 'Suspend membership', revoke: 'Revoke membership', reactivate: 'Reactivate membership', reinstate: 'Reinstate membership',
};
const date = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : '—');

/** User detail: company access (branch allocations, scope, membership), role assignments and the access audit trail. */
@Component({
  selector: 'app-user-access',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, DatePipe, UiButton, UiBadge, UiDataTable],
  templateUrl: './user-access.html',
  styleUrl: '../../access-ui/access-ui.scss',
})
export class UserAccessPage implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(UserAccessService);
  private readonly usersService = inject(UsersService);
  private readonly rolesService = inject(RolesService);
  private readonly context = inject(ContextService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);

  readonly userId = this.route.snapshot.paramMap.get('id') ?? '';
  readonly allocCols = ALLOC_COLS;
  readonly historyCols = HISTORY_COLS;
  readonly roleCols = ROLE_COLS;
  readonly roleHistoryCols = ROLE_HISTORY_COLS;

  readonly tab = signal<Tab>(this.route.snapshot.queryParamMap.get('tab') === 'roles' ? 'roles' : 'access');
  readonly state = signal<DataTableState>('loading');
  readonly user = signal<UserDetail | null>(null);
  readonly access = signal<UserAccess | null>(null);
  readonly roleAssignments = signal<UserRoleAssignments | null>(null);
  readonly rolesState = signal<DataTableState>('loading');
  readonly branches = signal<AccessibleBranch[]>([]);
  readonly assignable = signal<RoleListItem[]>([]);
  readonly events = signal<PagedResult<AuditEvent> | null>(null);
  readonly eventsPage = signal(1);

  readonly panel = signal<Panel | null>(null);
  readonly busy = signal(false);
  readonly banner = signal<string | null>(null);
  readonly selectedBranchIds = signal<ReadonlySet<string>>(new Set());

  /** Hints only; the server re-checks every write (company-wide actor, per-scope role authorisation). */
  readonly canManage = computed(() => this.context.hasPermission('Company.Users.ManageBranchAccess'));
  readonly canAssignRoles = computed(
    () => this.context.hasPermission('Company.Users.AssignRoles') || this.context.hasPermission('Branch.Users.AssignRoles'),
  );

  readonly form = this.fb.nonNullable.group({
    reason: [''],
    branchId: [''],
    isPrimary: [false],
    makePrimary: [false],
    accessScope: ['AssignedBranches' as CompanyAccessScope],
    primaryBranchId: [''],
    roleScope: ['Company' as RoleScope],
    roleId: [''],
  });

  readonly membershipActionList = computed(() => {
    const a = this.access();
    return a && this.canManage() ? membershipActions(a.status) : [];
  });
  readonly addableBranches = computed(() => branchesToAdd(this.branches(), this.access()?.current ?? []));
  readonly pickedBranches = computed(() => this.branches().filter((b) => this.selectedBranchIds().has(b.id)));

  readonly allocRows = computed<DataTableRow[]>(() =>
    (this.access()?.current ?? []).map((a) => ({
      cells: [
        { kind: 'text', text: a.branchName, sub: a.branchCode, weight: '600' },
        { kind: 'badge', text: a.isPrimary ? 'Primary' : 'Secondary', tone: a.isPrimary ? 'info' : 'mute' },
        { kind: 'text', text: date(a.startAt), color: 'var(--color-text-muted)' },
        { kind: 'text', text: a.assignmentReason ?? '—', color: 'var(--color-text-muted)', wrap: 'normal' },
        { kind: 'actions', actions: this.allocationRowActions(a) },
      ],
    })),
  );
  readonly historyRows = computed<DataTableRow[]>(() =>
    (this.access()?.history ?? []).map((a) => ({
      cells: [
        { kind: 'text', text: a.branchName, sub: a.branchCode, weight: '600' },
        { kind: 'text', text: date(a.startAt), color: 'var(--color-text-muted)' },
        { kind: 'text', text: date(a.endAt), color: 'var(--color-text-muted)' },
        { kind: 'text', text: [a.endedBy, a.endReason].filter(Boolean).join(' — ') || '—', color: 'var(--color-text-muted)', wrap: 'normal' },
      ],
    })),
  );
  readonly roleRows = computed<DataTableRow[]>(() =>
    (this.roleAssignments()?.current ?? []).map((r) => ({
      cells: [
        { kind: 'text', text: r.roleName, weight: '600' },
        { kind: 'text', text: this.roleScopeText(r), color: 'var(--color-text-muted)' },
        { kind: 'text', text: date(r.startAt), color: 'var(--color-text-muted)' },
        { kind: 'text', text: r.assignmentReason ?? '—', color: 'var(--color-text-muted)', wrap: 'normal' },
        { kind: 'actions', actions: this.canAssignRoles() ? [{ label: 'End', onClick: () => this.open({ kind: 'endRole', r }) }] : [] },
      ],
    })),
  );
  readonly roleHistoryRows = computed<DataTableRow[]>(() =>
    (this.roleAssignments()?.history ?? []).map((r) => ({
      cells: [
        { kind: 'text', text: r.roleName, weight: '600' },
        { kind: 'text', text: this.roleScopeText(r), color: 'var(--color-text-muted)' },
        { kind: 'text', text: date(r.startAt), color: 'var(--color-text-muted)' },
        { kind: 'text', text: date(r.endAt), color: 'var(--color-text-muted)' },
        { kind: 'text', text: [r.endedBy, r.endReason].filter(Boolean).join(' — ') || '—', color: 'var(--color-text-muted)', wrap: 'normal' },
      ],
    })),
  );

  readonly actionLabels = ACTION_LABELS;

  ngOnInit(): void {
    this.usersService.get(this.userId).subscribe({ next: (u) => this.user.set(u) });
    this.service.branches().subscribe({ next: (b) => this.branches.set(b), error: () => this.branches.set([]) });
    this.loadAccess();
    this.loadRoles();
    if (this.tab() === 'audit') this.loadEvents(1);
  }

  setTab(tab: Tab): void {
    this.tab.set(tab);
    this.closePanel();
    if (tab === 'audit' && !this.events()) this.loadEvents(1);
  }

  loadAccess(): void {
    this.state.set('loading');
    this.service.get(this.userId).subscribe({
      next: (a) => {
        this.access.set(a);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  loadRoles(): void {
    this.rolesState.set('loading');
    this.service.roles(this.userId).subscribe({
      next: (r) => {
        this.roleAssignments.set(r);
        this.rolesState.set('ready');
      },
      error: () => this.rolesState.set('error'),
    });
  }

  loadEvents(page: number): void {
    this.eventsPage.set(page);
    this.service.events(this.userId, page).subscribe({ next: (e) => this.events.set(e), error: () => this.events.set(null) });
  }

  eventJson(value: unknown): string {
    return value === null || value === undefined ? '' : JSON.stringify(value, null, 2);
  }

  // ---- panel handling ----

  open(panel: Panel): void {
    this.banner.set(null);
    this.form.reset({ accessScope: panel.kind === 'scope' ? this.otherScope() : 'AssignedBranches' });
    this.selectedBranchIds.set(new Set());
    this.panel.set(panel);
    if (panel.kind === 'assignRole') this.loadAssignable();
  }

  closePanel(): void {
    this.panel.set(null);
    this.banner.set(null);
  }

  panelTitle(): string {
    const p = this.panel();
    switch (p?.kind) {
      case 'add': return 'Add branch allocation';
      case 'primary': return `Make ${p.a.branchName} the primary branch`;
      case 'reassign': return `Reassign from ${p.a.branchName}`;
      case 'end': return `End allocation at ${p.a.branchName}`;
      case 'scope': return 'Change access scope';
      case 'status': return ACTION_LABELS[p.action];
      case 'assignRole': return 'Assign a role';
      case 'endRole': return `End role ${p.r.roleName}`;
      default: return '';
    }
  }

  /** Whether the open panel needs a reason (every audited access change; role assignment reason is optional). */
  reasonRequired(): boolean {
    return this.panel()?.kind !== 'assignRole';
  }

  needsBranchList(): boolean {
    const p = this.panel();
    return p?.kind === 'scope' ? this.form.controls.accessScope.value === 'AssignedBranches' : p?.kind === 'status' && p.action === 'reinstate';
  }

  toggleBranch(id: string): void {
    const next = new Set(this.selectedBranchIds());
    if (next.has(id)) next.delete(id);
    else next.add(id);
    this.selectedBranchIds.set(next);
  }

  /** Role assignment: reload the assignable roles when the scope or branch changes. */
  loadAssignable(): void {
    const { roleScope, branchId } = this.form.getRawValue();
    this.form.controls.roleId.setValue('');
    if (roleScope === 'Branch' && !branchId) {
      this.assignable.set([]);
      return;
    }
    this.rolesService.list({ scope: roleScope, branchId: roleScope === 'Branch' ? branchId : null }).subscribe({
      next: (roles) => this.assignable.set(assignableRoles(roles, roleScope, branchId || null)),
      error: (err: HttpErrorResponse) => {
        this.assignable.set([]);
        this.banner.set(applyProblem(this.form, toProblem(err)));
      },
    });
  }

  submit(): void {
    const p = this.panel();
    if (!p) return;
    const v = this.form.getRawValue();
    const reason = normalizeReason(v.reason);
    if (this.reasonRequired() && reason === null) {
      this.form.controls.reason.setErrors({ required: true });
      this.form.controls.reason.markAsTouched();
      return;
    }
    const missing = (name: 'branchId' | 'roleId') => {
      this.form.controls[name].setErrors({ required: true });
      this.form.controls[name].markAsTouched();
    };

    let call: Observable<UserAccess | UserRoleAssignments>;
    let done: string;
    switch (p.kind) {
      case 'add':
        if (!v.branchId) return missing('branchId');
        call = this.service.addAssignment(this.userId, addAssignmentRequest(v.branchId, v.isPrimary, reason));
        done = 'Allocation added.';
        break;
      case 'primary':
        call = this.service.setPrimary(this.userId, setPrimaryRequest(p.a.branchId, reason));
        done = 'Primary branch changed.';
        break;
      case 'reassign':
        if (!v.branchId) return missing('branchId');
        call = this.service.reassign(this.userId, reassignRequest(p.a.id, v.branchId, v.makePrimary || p.a.isPrimary, reason));
        done = 'Allocation reassigned.';
        break;
      case 'end':
        call = this.service.endAssignment(this.userId, p.a.id, endAssignmentRequest(reason));
        done = 'Allocation ended.';
        break;
      case 'scope':
        call = this.service.setScope(this.userId, scopeRequest(v.accessScope, [...this.selectedBranchIds()], v.primaryBranchId || null, reason));
        done = 'Access scope changed.';
        break;
      case 'status':
        call = this.service.setStatus(
          this.userId,
          statusRequest(p.action, reason, p.action === 'reinstate' ? { accessScope: v.accessScope, branchIds: [...this.selectedBranchIds()], primaryBranchId: v.primaryBranchId || null } : undefined),
        );
        done = 'Membership updated.';
        break;
      case 'assignRole':
        if (!v.roleId) return missing('roleId');
        call = this.service.assignRole(this.userId, assignRoleRequest(v.roleId, reason));
        done = 'Role assigned.';
        break;
      case 'endRole':
        call = this.service.endRole(this.userId, p.r.id, endRoleRequest(reason ?? ''));
        done = 'Role assignment ended.';
        break;
    }

    this.busy.set(true);
    this.banner.set(null);
    call.subscribe({
      next: (view) => {
        this.busy.set(false);
        if (p.kind === 'assignRole' || p.kind === 'endRole') {
          this.roleAssignments.set(view as UserRoleAssignments);
        } else {
          this.access.set(view as UserAccess);
        }
        this.toast.show(done);
        this.panel.set(null);
        if (this.events()) this.loadEvents(1); // keep the audit tab fresh
      },
      error: (err: HttpErrorResponse) => {
        this.busy.set(false);
        this.banner.set(applyProblem(this.form, toProblem(err), { accessScope: 'accessScope', branchIds: 'accessScope', primaryBranchId: 'primaryBranchId' }));
      },
    });
  }

  roleScopeText(r: Pick<RoleAssignment, 'scopeType' | 'branchName'>): string {
    return r.scopeType === 'Branch' ? `Branch${r.branchName ? ` · ${r.branchName}` : ''}` : 'Company';
  }

  private otherScope(): CompanyAccessScope {
    return this.access()?.accessScope === 'CompanyWide' ? 'AssignedBranches' : 'CompanyWide';
  }

  private allocationRowActions(a: BranchAssignment): DataTableRowAction[] {
    const labels = { makePrimary: 'Make primary', reassign: 'Reassign', end: 'End' } as const;
    return allocationActions(a, this.canManage() && this.access()?.status !== 'Revoked').map((kind) => ({
      label: labels[kind],
      onClick: () => this.open(kind === 'makePrimary' ? { kind: 'primary', a } : { kind, a }),
    }));
  }
}
