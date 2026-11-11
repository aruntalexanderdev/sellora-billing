import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import type { Observable } from 'rxjs';
import type { PagedResult } from '../../../core/api.models';
import { ContextService } from '../../../core/context/context.service';
import { applyProblem, toProblem } from '../../../core/http/api-errors';
import { UiButton } from '../../../shared/ui/button/button';
import { UiDataTable } from '../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { normalizeReason } from '../access-ui/reason-prompt';
import type { RoleListItem } from '../roles/roles.models';
import { RolesService } from '../roles/roles.service';
import type { AuditEvent } from '../users/user-access/user-access.models';
import type { UserListItem } from '../users/users.models';
import { UsersService } from '../users/users.service';
import { governanceCapabilities, grantableCodes, memberActions, titleChoices, type MemberAction } from './governance.logic';
import type { GovernanceMember, GovernancePermission, GovernanceTitle } from './governance.models';
import { GovernanceService } from './governance.service';

type Tab = 'members' | 'administrators' | 'events';

type Panel =
  | { kind: 'add' }
  | { kind: 'title'; m: GovernanceMember }
  | { kind: 'permissions'; m: GovernanceMember }
  | { kind: 'end'; m: GovernanceMember }
  | { kind: 'grant' }
  | { kind: 'remove' };

const COLS: DataTableColumn[] = [
  { label: 'Member', align: 'left' },
  { label: 'Title', align: 'left' },
  { label: 'Permissions', align: 'left' },
  { label: 'Since', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

const TITLE_LABELS: Record<GovernanceTitle, string> = {
  Owner: 'Owner', BoardMember: 'Board member', Director: 'Director', DelegatedAdministrator: 'Delegated administrator',
};
const shortCode = (code: string) => code.replace('Company.Governance.', '');

/** Company governance: members and their grants, administrator delegation and the governance audit trail (all actions gated by governance permissions). */
@Component({
  selector: 'app-governance-page',
  standalone: true,
  imports: [ReactiveFormsModule, DatePipe, UiButton, UiDataTable],
  templateUrl: './governance-page.html',
  styleUrl: '../access-ui/access-ui.scss',
})
export class GovernancePage implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(GovernanceService);
  private readonly usersService = inject(UsersService);
  private readonly rolesService = inject(RolesService);
  private readonly context = inject(ContextService);
  private readonly toast = inject(ToastService);

  readonly cols = COLS;
  readonly titleLabels = TITLE_LABELS;
  readonly tab = signal<Tab>('members');
  readonly state = signal<DataTableState>('loading');
  readonly members = signal<GovernanceMember[]>([]);
  readonly catalog = signal<GovernancePermission[]>([]);
  readonly includeEnded = signal(false);
  readonly events = signal<PagedResult<AuditEvent> | null>(null);
  readonly eventsPage = signal(1);
  readonly userOptions = signal<UserListItem[]>([]);
  readonly roles = signal<RoleListItem[]>([]);

  readonly panel = signal<Panel | null>(null);
  readonly busy = signal(false);
  readonly banner = signal<string | null>(null);
  readonly selectedCodes = signal<ReadonlySet<string>>(new Set());

  readonly form = this.fb.nonNullable.group({
    reason: [''],
    userSearch: [''],
    userId: [''],
    title: ['BoardMember' as GovernanceTitle],
    replacementRoleId: [''],
  });

  /** Everything below is a UI hint derived from the caller's governance permissions; the server re-checks each write. */
  private readonly has = (code?: string | null) => this.context.hasGovernance(code);
  readonly capabilities = computed(() => governanceCapabilities(this.has));
  readonly addableTitles = computed(() => titleChoices(this.has));
  readonly grantable = computed(() => grantableCodes(this.catalog(), this.has));

  readonly rows = computed<DataTableRow[]>(() =>
    this.members().map((m) => ({
      cells: [
        { kind: 'text', text: m.displayName, weight: '600' },
        { kind: 'badge', text: TITLE_LABELS[m.title], tone: m.title === 'Owner' ? 'info' : 'mute' },
        { kind: 'text', text: m.permissions.map(shortCode).join(', ') || '—', color: 'var(--color-text-muted)', wrap: 'normal' },
        { kind: 'text', text: new Date(m.startAt).toLocaleDateString(), color: 'var(--color-text-muted)' },
        m.status === 'Active'
          ? { kind: 'badge', text: 'Active', tone: 'good' }
          : { kind: 'badge', text: `Ended${m.endReason ? ': ' + m.endReason : ''}`, tone: 'mute' },
        { kind: 'actions', actions: this.actionsFor(m) },
      ],
    })),
  );

  ngOnInit(): void {
    this.loadMembers();
    this.service.permissions().subscribe({ next: (c) => this.catalog.set(c), error: () => this.catalog.set([]) });
  }

  setTab(tab: Tab): void {
    this.tab.set(tab);
    this.closePanel();
    if (tab === 'events' && !this.events()) this.loadEvents(1);
  }

  loadMembers(): void {
    this.state.set('loading');
    this.service.members(this.includeEnded()).subscribe({
      next: (m) => {
        this.members.set(m);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  toggleEnded(): void {
    this.includeEnded.update((v) => !v);
    this.loadMembers();
  }

  loadEvents(page: number): void {
    this.eventsPage.set(page);
    this.service.events(page).subscribe({ next: (e) => this.events.set(e), error: () => this.events.set(null) });
  }

  eventJson(value: unknown): string {
    return value === null || value === undefined ? '' : JSON.stringify(value, null, 2);
  }

  open(panel: Panel): void {
    this.banner.set(null);
    this.form.reset({ title: panel.kind === 'title' ? panel.m.title : (this.addableTitles()[0] ?? 'BoardMember') });
    this.selectedCodes.set(new Set(panel.kind === 'permissions' ? panel.m.permissions : []));
    this.userOptions.set([]);
    this.panel.set(panel);
    if (panel.kind === 'remove' && this.roles().length === 0) {
      this.rolesService.list().subscribe({ next: (r) => this.roles.set(r.filter((x) => x.isActive)), error: () => this.roles.set([]) });
    }
  }

  closePanel(): void {
    this.panel.set(null);
    this.banner.set(null);
  }

  panelTitle(): string {
    const p = this.panel();
    switch (p?.kind) {
      case 'add': return 'Add governance member';
      case 'title': return `Change title of ${p.m.displayName}`;
      case 'permissions': return `Governance permissions of ${p.m.displayName}`;
      case 'end': return `End governance membership of ${p.m.displayName}`;
      case 'grant': return 'Grant the Administrator role';
      case 'remove': return 'Remove the Administrator role';
      default: return '';
    }
  }

  needsUser(): boolean {
    const k = this.panel()?.kind;
    return k === 'add' || k === 'grant' || k === 'remove';
  }

  needsTitle(): boolean {
    const k = this.panel()?.kind;
    return k === 'add' || k === 'title';
  }

  needsCodes(): boolean {
    const k = this.panel()?.kind;
    return k === 'add' || k === 'permissions';
  }

  searchUsers(): void {
    const search = this.form.controls.userSearch.value.trim();
    if (search.length < 2) return;
    this.usersService.list({ search, pageSize: 10 }).subscribe({ next: (r) => this.userOptions.set(r.items), error: () => this.userOptions.set([]) });
  }

  toggleCode(code: string): void {
    const next = new Set(this.selectedCodes());
    if (next.has(code)) next.delete(code);
    else next.add(code);
    this.selectedCodes.set(next);
  }

  submit(): void {
    const p = this.panel();
    if (!p) return;
    const v = this.form.getRawValue();
    const reason = normalizeReason(v.reason);
    const codes = [...this.selectedCodes()];
    const need = (name: 'userId' | 'replacementRoleId') => {
      this.form.controls[name].setErrors({ required: true });
      this.form.controls[name].markAsTouched();
    };

    let call: Observable<unknown>;
    let done: string;
    switch (p.kind) {
      case 'add':
        if (!v.userId) return need('userId');
        call = this.service.addMember({ userId: v.userId, title: v.title, permissionCodes: codes, reason });
        done = 'Governance member added.';
        break;
      case 'title':
        call = this.service.changeTitle(p.m.id, { title: v.title, reason });
        done = 'Title changed.';
        break;
      case 'permissions':
        call = this.service.setPermissions(p.m.id, { permissionCodes: codes, reason });
        done = 'Governance permissions updated.';
        break;
      case 'end':
        call = this.service.endMember(p.m.id, { reason });
        done = 'Governance membership ended.';
        break;
      case 'grant':
        if (!v.userId) return need('userId');
        call = this.service.grantAdministrator({ userId: v.userId, reason });
        done = 'Administrator role granted.';
        break;
      case 'remove':
        if (!v.userId) return need('userId');
        if (!v.replacementRoleId) return need('replacementRoleId');
        call = this.service.removeAdministrator(v.userId, { replacementRoleId: v.replacementRoleId, reason });
        done = 'Administrator role removed.';
        break;
    }

    this.busy.set(true);
    this.banner.set(null);
    call.subscribe({
      next: () => {
        this.busy.set(false);
        this.toast.show(done);
        this.panel.set(null);
        this.loadMembers();
        if (this.events()) this.loadEvents(1);
      },
      error: (err: HttpErrorResponse) => {
        this.busy.set(false);
        this.banner.set(applyProblem(this.form, toProblem(err), { permissionCodes: 'userId' }));
      },
    });
  }

  private actionsFor(m: GovernanceMember) {
    const labels: Record<MemberAction, string> = { changeTitle: 'Change title', setPermissions: 'Permissions', end: 'End' };
    return memberActions(m, this.has).map((a) => ({
      label: labels[a],
      onClick: () => this.open(a === 'changeTitle' ? { kind: 'title', m } : a === 'setPermissions' ? { kind: 'permissions', m } : { kind: 'end', m }),
    }));
  }
}
