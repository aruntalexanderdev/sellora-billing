import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { PermissionsService } from '../permissions.service';
import type { PermissionCatalogEntry } from '../permissions.models';

const COLS: DataTableColumn[] = [
  { label: 'Code', align: 'left' },
  { label: 'Module', align: 'left' },
  { label: 'Action', align: 'left' },
  { label: 'Description', align: 'left' },
];

@Component({
  selector: 'app-permission-list',
  standalone: true,
  imports: [UiDataTable],
  templateUrl: './permission-list.html',
  styleUrl: '../../users/user-list/user-list.scss',
})
export class PermissionList implements OnInit {
  private readonly permissionsService = inject(PermissionsService);

  readonly cols = COLS;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<PermissionCatalogEntry[]>([]);

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((p) => ({
      cells: [
        { kind: 'text', text: p.code, weight: '600' },
        { kind: 'text', text: p.module, color: 'var(--color-text-muted)' },
        { kind: 'text', text: p.action, color: 'var(--color-text-muted)' },
        { kind: 'text', text: p.description, color: 'var(--color-text-muted)', wrap: 'normal' },
      ],
    })),
  );

  ngOnInit(): void {
    this.permissionsService.list().subscribe({
      next: (list) => {
        this.items.set(list);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  retry(): void {
    this.ngOnInit();
  }
}
