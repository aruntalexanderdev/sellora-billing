import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
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

  readonly cols = COLS;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<RoleListItem[]>([]);

  readonly canCreate = computed(() => this.auth.hasPermission('Roles.Create'));
  readonly canEdit = computed(() => this.auth.hasPermission('Roles.Edit'));
  readonly canDuplicate = computed(() => this.auth.hasPermission('Roles.Duplicate'));
  readonly canDisable = computed(() => this.auth.hasPermission('Roles.Disable'));

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((r) => ({
      cells: [
        { kind: 'text', text: r.name, sub: r.description ?? undefined, weight: '600', wrap: 'normal' },
        { kind: 'text', text: String(r.userCount), align: 'right', color: 'var(--color-text-muted)' },
        {
          kind: 'badge',
          text: r.isSystem ? 'System' : r.isActive ? 'Active' : 'Inactive',
          tone: r.isSystem ? 'info' : r.isActive ? 'good' : 'mute',
        },
        {
          kind: 'actions',
          actions: [
            ...(this.canEdit() && !r.isSystem
              ? [{ label: 'Edit', onClick: () => this.router.navigate(['/administration/roles', r.id, 'edit']) }]
              : []),
            ...(this.canDuplicate() ? [{ label: 'Duplicate', onClick: () => this.duplicate(r) }] : []),
            ...(this.canDisable() && !r.isSystem
              ? [{ label: r.isActive ? 'Deactivate' : 'Activate', onClick: () => this.toggleActive(r) }]
              : []),
          ],
        },
      ],
    })),
  );

  ngOnInit(): void {
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
    this.rolesService.list().subscribe({
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
