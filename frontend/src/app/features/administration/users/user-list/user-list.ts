import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../../shared/ui/button/button';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { UsersService } from '../users.service';
import { RolesService } from '../../roles/roles.service';
import type { RoleListItem } from '../../roles/roles.models';
import type { UserListItem } from '../users.models';

const COLS: DataTableColumn[] = [
  { label: 'User', align: 'left' },
  { label: 'Role', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: 'Last sign-in', align: 'left' },
  { label: '', align: 'right' },
];

@Component({
  selector: 'app-user-list',
  standalone: true,
  imports: [UiDataTable, UiButton],
  templateUrl: './user-list.html',
  styleUrl: './user-list.scss',
})
export class UserList implements OnInit {
  private readonly usersService = inject(UsersService);
  private readonly rolesService = inject(RolesService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly search = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<UserListItem[]>([]);
  readonly total = signal(0);
  readonly roles = signal<RoleListItem[]>([]);

  readonly canCreate = computed(() => this.auth.hasPermission('Users.Create'));
  readonly hasMore = computed(() => this.page() * 20 < this.total());
  readonly canEdit = computed(() => this.auth.hasPermission('Users.Edit'));
  readonly canDisable = computed(() => this.auth.hasPermission('Users.Disable'));
  readonly canResetPassword = computed(() => this.auth.hasPermission('Users.ResetPassword'));

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((u) => ({
      cells: [
        { kind: 'text', text: `${u.firstName} ${u.lastName}`, sub: u.email, weight: '600', wrap: 'normal' },
        { kind: 'text', text: u.roleName, color: 'var(--color-text-muted)' },
        { kind: 'badge', text: u.status, tone: u.status === 'Active' ? 'good' : u.status === 'Invited' ? 'info' : 'mute' },
        { kind: 'text', text: u.lastLoginAt ? new Date(u.lastLoginAt).toLocaleString() : '—', color: 'var(--color-text-muted)' },
        {
          kind: 'actions',
          actions: [
            ...(this.canEdit() ? [{ label: 'Edit', onClick: () => this.router.navigate(['/administration/users', u.id, 'edit']) }] : []),
            ...(this.canResetPassword() ? [{ label: 'Reset password', onClick: () => this.resetPassword(u) }] : []),
            ...(this.canDisable()
              ? [{ label: u.status === 'Disabled' ? 'Activate' : 'Deactivate', onClick: () => this.toggleStatus(u) }]
              : []),
          ],
        },
      ],
    })),
  );

  ngOnInit(): void {
    this.rolesService.list().subscribe({ next: (roles) => this.roles.set(roles) });
    this.load();
  }

  onSearch(value: string): void {
    this.search.set(value);
    this.page.set(1);
    this.load();
  }

  onPageChange(page: number): void {
    this.page.set(page);
    this.load();
  }

  retry(): void {
    this.load();
  }

  goToCreate(): void {
    this.router.navigate(['/administration/users/new']);
  }

  private load(): void {
    this.state.set('loading');
    this.usersService.list({ search: this.search(), page: this.page() }).subscribe({
      next: (res) => {
        this.items.set(res.items);
        this.total.set(res.total);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  private toggleStatus(u: UserListItem): void {
    const disabling = u.status !== 'Disabled';
    this.confirm
      .ask({
        title: disabling ? `Deactivate ${u.firstName} ${u.lastName}?` : `Activate ${u.firstName} ${u.lastName}?`,
        body: disabling
          ? 'They will no longer be able to sign in. This can be reversed later.'
          : 'They will be able to sign in again.',
        confirmLabel: disabling ? 'Deactivate' : 'Activate',
        destructive: disabling,
      })
      .then((confirmed) => {
        if (!confirmed) return;
        this.usersService.setStatus(u.id, disabling ? 'Disabled' : 'Active').subscribe({
          next: () => {
            this.toast.show(`${u.firstName} ${u.lastName} ${disabling ? 'deactivated' : 'activated'}.`);
            this.load();
          },
          error: (err) => this.toast.show(err.error?.title ?? 'Could not update this user.'),
        });
      });
  }

  private resetPassword(u: UserListItem): void {
    this.confirm
      .ask({
        title: `Reset password for ${u.firstName} ${u.lastName}?`,
        body: 'A new temporary password will be generated. Share it with them directly — email delivery is not wired up in Phase 1.',
        confirmLabel: 'Generate password',
      })
      .then((confirmed) => {
        if (!confirmed) return;
        this.usersService.resetPassword(u.id).subscribe({
          next: (res) => this.toast.show(`Temporary password for ${u.username}: ${res.temporaryPassword}`, 8000),
          error: (err) => this.toast.show(err.error?.title ?? 'Could not reset the password.'),
        });
      });
  }
}
