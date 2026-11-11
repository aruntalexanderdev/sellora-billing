import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { BranchSelect } from '../../../../shared/branches/branch-select';
import type { StockLocation, StockLocationType } from '../../../../shared/locations/locations.models';
import { StockLocationsService } from '../../../../shared/locations/stock-locations.service';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';

export const LOCATION_TYPES: readonly { value: StockLocationType; label: string }[] = [
  { value: 'BranchStore', label: 'Branch store' },
  { value: 'BranchWarehouse', label: 'Branch warehouse' },
  { value: 'CompanyWarehouse', label: 'Company warehouse' },
  { value: 'Vehicle', label: 'Vehicle' },
  { value: 'Transit', label: 'In transit' },
];

const COLS: DataTableColumn[] = [
  { label: 'Code', align: 'left' },
  { label: 'Name', align: 'left' },
  { label: 'Type', align: 'left' },
  { label: 'Branch', align: 'left' },
  { label: 'Can sell', align: 'center' },
  { label: 'Can receive purchases', align: 'center' },
  { label: 'Status', align: 'left' },
];

/** Read-only: locations are created through branches, warehouses and vehicles. */
@Component({
  selector: 'app-stock-location-list',
  standalone: true,
  imports: [UiDataTable, BranchSelect],
  templateUrl: './stock-location-list.html',
  styleUrl: '../../company-admin.scss',
})
export class StockLocationList implements OnInit {
  private readonly service = inject(StockLocationsService);

  readonly cols = COLS;
  readonly types = LOCATION_TYPES;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<StockLocation[]>([]);
  readonly type = signal<StockLocationType | ''>('');
  readonly branchId = signal<string | null>(null);
  readonly includeInactive = signal(false);

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((l) => ({
      cells: [
        { kind: 'text', text: l.code, weight: '600' },
        { kind: 'text', text: l.name, sub: l.isSystemManaged ? 'System managed' : undefined },
        { kind: 'text', text: LOCATION_TYPES.find((t) => t.value === l.locationType)?.label ?? l.locationType, color: 'var(--color-text-muted)' },
        { kind: 'text', text: l.branchId ? `${l.branchName ?? ''} (${l.branchCode ?? ''})` : 'Company', color: 'var(--color-text-muted)' },
        { kind: 'badge', text: l.canSell ? 'Yes' : 'No', tone: l.canSell ? 'good' : 'mute', align: 'center' },
        { kind: 'badge', text: l.canReceivePurchase ? 'Yes' : 'No', tone: l.canReceivePurchase ? 'good' : 'mute', align: 'center' },
        { kind: 'badge', text: l.isActive ? 'Active' : 'Inactive', tone: l.isActive ? 'good' : 'mute' },
      ],
    })),
  );

  ngOnInit(): void {
    this.load();
  }

  setType(value: string): void {
    this.type.set(LOCATION_TYPES.some((t) => t.value === value) ? (value as StockLocationType) : '');
    this.load();
  }

  setBranch(id: string | null): void {
    this.branchId.set(id);
    this.load();
  }

  setIncludeInactive(value: boolean): void {
    this.includeInactive.set(value);
    this.load();
  }

  load(): void {
    this.state.set('loading');
    this.service
      .list({ type: this.type() || undefined, branchId: this.branchId() ?? undefined, includeInactive: this.includeInactive() })
      .subscribe({
        next: (items) => {
          this.items.set(items);
          this.state.set('ready');
        },
        error: () => this.state.set('error'),
      });
  }
}
