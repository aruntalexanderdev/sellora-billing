import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import type { AccessibleBranch } from '../../../../shared/branches/branch-select';
import { UserAccessService } from '../../users/user-access/user-access.service';
import { canManageRole, scopeLabel } from '../roles.logic';
import type { RoleScope } from '../roles.models';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../../shared/ui/button/button';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { RolesService } from '../roles.service';
import type { RoleListItem } from '../roles.models';

const COLS: DataTableColumn[] = [
  { label: 'Role', align: 'left' },
  { label: 'Scope', align: 'left' },
  { label: 'Users', align: 'right' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

@Component({
  selector: 'app-role-list',
  standalone: true,
  imports: [UiDataTable, UiButton],
  templateUrl: './role-list.html',
  styleUrl: '../../users/user-list/user-list.scss',
})
export class RoleList implements OnInit {
  private readonly rolesService = inject(RolesService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly accessService = inject(UserAccessService);

  /** Company-level Roles.View is needed to list company roles; branch administrators (Branch.Roles.View) only see branch roles. */
  readonly canViewCompany = computed(() => this.auth.hasPermission('Roles.View'));
  readonly scope = signal<RoleScope>(this.auth.hasPermission('Roles.View') ? 'Company' : 'Branch');
  readonly branchId = signal<string>('');
  readonly branches = signal<AccessibleBranch[]>([]);

  readonly cols = COLS;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<RoleListItem[]>([]);

  readonly canCreate = computed(() => this.auth.hasPermission('Roles.Create') || this.auth.hasPermission('Branch.Roles.Manage'));

  /** Per-row gating: company roles need Roles.*, branch roles Roles.* or Branch.Roles.Manage. */
  can(r: Pick<RoleListItem, 'scopeType'> & Partial<Pick<RoleListItem, 'holdsBranchAdministration'>>, code: 'Roles.Edit' | 'Roles.Duplicate' | 'Roles.Disable'): boolean {
    return canManageRole((c) => this.auth.hasPermission(c), r.scopeType, code, r.holdsBranchAdministration);
  }

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((r) => ({
      cells: [
        { kind: 'text', text: r.name, sub: r.description ?? undefined, weight: '600', wrap: 'normal' },
        { kind: 'text', text: scopeLabel(r), color: 'var(--color-text-muted)' },
        { kind: 'text', text: String(r.userCount), align: 'right', color: 'var(--color-text-muted)' },
        {
          kind: 'badge',
          text: r.isSystem ? 'System' : r.isActive ? 'Active' : 'Inactive',
          tone: r.isSystem ? 'info' : r.isActive ? 'good' : 'mute',
        },
        {
          kind: 'actions',
          actions: [
            ...(this.can(r, 'Roles.Edit') && !r.isSystem
              ? [{ label: 'Edit', onClick: () => this.router.navigate(['/administration/roles', r.id, 'edit']) }]
              : []),
            ...(r.scopeType === 'Branch' && r.branchId
              ? [{ label: 'Members', onClick: () => this.router.navigate(['/administration/roles/branches', r.branchId, 'members']) }]
              : []),
            ...(this.can(r, 'Roles.Duplicate') ? [{ label: 'Duplicate', onClick: () => this.duplicate(r) }] : []),
            ...(this.can(r, 'Roles.Disable') && !r.isSystem
              ? [{ label: r.isActive ? 'Deactivate' : 'Activate', onClick: () => this.toggleActive(r) }]
              : []),
          ],
        },
      ],
    })),
  );

  ngOnInit(): void {
    this.accessService.branches().subscribe({ next: (b) => this.branches.set(b), error: () => this.branches.set([]) });
    this.load();
  }

  setScope(scope: RoleScope): void {
    this.scope.set(scope);
    if (scope === 'Company') this.branchId.set('');
    this.load();
  }

  setBranch(id: string): void {
    this.branchId.set(id);
    this.load();
  }

  retry(): void {
    this.load();
  }

  goToCreate(): void {
    this.router.navigate(['/administration/roles/new']);
  }

  private load(): void {
    this.state.set('loading');
    this.rolesService.list({ scope: this.scope(), branchId: this.branchId() || null }).subscribe({
      next: (roles) => {
        this.items.set(roles);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  private duplicate(r: RoleListItem): void {
    this.rolesService.duplicate(r.id).subscribe({
      next: (copy) => {
        this.toast.show(`Created "${copy.name}" as a copy of ${r.name}.`);
        this.load();
      },
      error: (err) => this.toast.show(err.error?.title ?? 'Could not duplicate this role.'),
    });
  }

  private toggleActive(r: RoleListItem): void {
    const disabling = r.isActive;
    this.confirm
      .ask({
        title: disabling ? `Deactivate ${r.name}?` : `Activate ${r.name}?`,
        body: disabling
          ? `Users holding ${r.name} will lose its permissions immediately.`
          : `Users holding ${r.name} will regain its permissions.`,
        confirmLabel: disabling ? 'Deactivate' : 'Activate',
        destructive: disabling,
      })
      .then((confirmed) => {
        if (!confirmed) return;
        this.rolesService.setActive(r.id, !disabling).subscribe({
          next: () => {
            this.toast.show(`${r.name} ${disabling ? 'deactivated' : 'activated'}.`);
            this.load();
          },
          error: (err) => this.toast.show(err.error?.title ?? 'Could not update this role.'),
        });
      });
  }
}
