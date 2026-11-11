import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableRowAction, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
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
 * Activate/Deactivate row action (used by the platform console; the company list is read-only, D12/O7). An active unit the server reports as `canDeactivate === false`
 * (a product base unit, or used by an active product unit) is rendered with a disabled Deactivate
 * and an explanatory title. `isInUse` only drives the unit-form locks, not this action.
 */
export function statusAction(
  u: Pick<UnitItem, 'isActive' | 'canDeactivate'>,
  onToggle: () => void,
): DataTableRowAction {
  if (!u.isActive) return { label: 'Activate', onClick: onToggle };
  if (!u.canDeactivate) return { label: 'Deactivate', onClick: () => undefined, disabled: true, title: IN_USE_DEACTIVATE_TITLE };
  return { label: 'Deactivate', onClick: onToggle };
}

const COLS: DataTableColumn[] = [
  { label: 'Name', align: 'left' },
  { label: 'Code', align: 'left' },
  { label: 'Category', align: 'left' },
  { label: 'Decimals', align: 'right' },
  { label: 'Standard factor', align: 'right' },
  { label: 'Status', align: 'left' },
];

@Component({
  selector: 'app-unit-list',
  standalone: true,
  imports: [UiDataTable],
  templateUrl: './unit-list.html',
  styleUrl: '../../../administration/users/user-list/user-list.scss',
})
export class UnitList implements OnInit {
  private readonly unitsService = inject(UnitsService);

  readonly cols = COLS;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<UnitItem[]>([]);

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((u) => ({
      cells: [
        { kind: 'text', text: u.name, weight: '600' },
        { kind: 'text', text: u.shortCode, color: 'var(--color-text-muted)' },
        { kind: 'text', text: u.measurementCategory, color: 'var(--color-text-muted)' },
        { kind: 'text', text: String(u.decimalPlaces), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: formatStandardFactor(u), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'badge', text: u.isActive ? 'Active' : 'Inactive', tone: u.isActive ? 'good' : 'mute' },
      ],
    })),
  );

  ngOnInit(): void {
    this.load();
  }

  retry(): void {
    this.load();
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
}
