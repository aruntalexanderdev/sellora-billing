import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../../shared/ui/button/button';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { UnitsService } from '../units.service';
import type { UnitItem } from '../units.models';

const COLS: DataTableColumn[] = [
  { label: 'Name', align: 'left' },
  { label: 'Short code', align: 'left' },
  { label: 'Type', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

@Component({
  selector: 'app-unit-list',
  standalone: true,
  imports: [UiDataTable, UiButton],
  templateUrl: './unit-list.html',
  styleUrl: '../../../administration/users/user-list/user-list.scss',
})
export class UnitList implements OnInit {
  private readonly unitsService = inject(UnitsService);
  private readonly toast = inject(ToastService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<UnitItem[]>([]);

  readonly canEdit = computed(() => this.auth.hasPermission('Masters.Edit'));

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((u) => ({
      cells: [
        { kind: 'text', text: u.name, weight: '600' },
        { kind: 'text', text: u.shortCode, color: 'var(--color-text-muted)' },
        { kind: 'text', text: u.type, color: 'var(--color-text-muted)' },
        { kind: 'badge', text: u.isActive ? 'Active' : 'Inactive', tone: u.isActive ? 'good' : 'mute' },
        {
          kind: 'actions',
          actions: this.canEdit()
            ? [
                { label: 'Edit', onClick: () => this.router.navigate(['/administration/units', u.id, 'edit']) },
                { label: u.isActive ? 'Deactivate' : 'Activate', onClick: () => this.toggle(u) },
              ]
            : [],
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
    this.router.navigate(['/administration/units/new']);
  }

  private load(): void {
    this.state.set('loading');
    this.unitsService.list().subscribe({
      next: (units) => {
        this.items.set(units);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  private toggle(u: UnitItem): void {
    this.unitsService.setActive(u.id, !u.isActive).subscribe({
      next: () => {
        this.toast.show(`${u.name} ${u.isActive ? 'deactivated' : 'activated'}.`);
        this.load();
      },
      error: (err) => this.toast.show(err.error?.title ?? 'Could not update this unit.'),
    });
  }
}
