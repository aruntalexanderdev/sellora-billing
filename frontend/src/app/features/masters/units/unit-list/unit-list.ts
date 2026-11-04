import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../../shared/ui/button/button';
import type { DataTableColumn, DataTableRow, DataTableRowAction, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { AuthService } from '../../../../core/auth/auth.service';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import { UnitsService } from '../units.service';
import { REFERENCE_UNIT, isMeasurableCategory, type UnitItem } from '../units.models';

/** "0.001 kg" for measurable units with a standard factor; blank otherwise. */
export function formatStandardFactor(u: Pick<UnitItem, 'measurementCategory' | 'standardFactor'>): string {
  if (u.standardFactor === null || u.standardFactor === undefined || !isMeasurableCategory(u.measurementCategory)) {
    return '';
  }
  return `${u.standardFactor} ${REFERENCE_UNIT[u.measurementCategory]}`;
}

export const IN_USE_DEACTIVATE_TITLE = 'Used by products — cannot be deactivated';

/**
 * Activate/Deactivate row action. An active unit used as a product base unit cannot be
 * deactivated (server rejects it), so the action is rendered disabled with an explanatory title.
 */
export function statusAction(
  u: Pick<UnitItem, 'isActive' | 'isInUse'>,
  onToggle: () => void,
): DataTableRowAction {
  if (!u.isActive) return { label: 'Activate', onClick: onToggle };
  if (u.isInUse) return { label: 'Deactivate', onClick: () => undefined, disabled: true, title: IN_USE_DEACTIVATE_TITLE };
  return { label: 'Deactivate', onClick: onToggle };
}

const COLS: DataTableColumn[] = [
  { label: 'Name', align: 'left' },
  { label: 'Code', align: 'left' },
  { label: 'Category', align: 'left' },
  { label: 'Decimals', align: 'right' },
  { label: 'Standard factor', align: 'right' },
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
        { kind: 'text', text: u.measurementCategory, color: 'var(--color-text-muted)' },
        { kind: 'text', text: String(u.decimalPlaces), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: formatStandardFactor(u), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'badge', text: u.isActive ? 'Active' : 'Inactive', tone: u.isActive ? 'good' : 'mute' },
        {
          kind: 'actions',
          actions: this.canEdit()
            ? [
                { label: 'Edit', onClick: () => this.router.navigate(['/administration/units', u.id, 'edit']) },
                statusAction(u, () => this.toggle(u)),
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
      error: (err: HttpErrorResponse) => {
        const problem = err.error as ApiProblem | undefined;
        const firstFieldMessage = Object.values(problem?.errors ?? {}).find((m) => m?.length)?.[0];
        this.toast.show(firstFieldMessage ?? problem?.title ?? 'Could not update this unit.');
      },
    });
  }
}
