import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ContextService } from '../../../../core/context/context.service';
import { BranchSelect } from '../../../../shared/branches/branch-select';
import { UiButton } from '../../../../shared/ui/button/button';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ReasonPrompt } from '../../../administration/access-ui/reason-prompt';
import { reasonFieldError, refusalMessage } from '../../company-admin';
import { VEHICLE_TYPES, type Vehicle, type VehicleType } from '../vehicles.models';
import { VehiclesService } from '../vehicles.service';

const COLS: DataTableColumn[] = [
  { label: 'Number', align: 'left' },
  { label: 'Name', align: 'left' },
  { label: 'Type', align: 'left' },
  { label: 'Assigned branch', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

@Component({
  selector: 'app-vehicle-list',
  standalone: true,
  imports: [UiDataTable, UiButton, BranchSelect, ReasonPrompt],
  templateUrl: './vehicle-list.html',
  styleUrl: '../../company-admin.scss',
})
export class VehicleList implements OnInit {
  private readonly service = inject(VehiclesService);
  private readonly context = inject(ContextService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly vehicleTypes = VEHICLE_TYPES;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<Vehicle[]>([]);
  readonly includeInactive = signal(false);
  readonly vehicleType = signal<VehicleType | ''>('');
  readonly branchId = signal<string | null>(null);
  readonly unassigned = signal(false);
  readonly notice = signal<string | null>(null);
  readonly statusTarget = signal<Vehicle | null>(null);
  readonly statusBusy = signal(false);
  readonly statusError = signal<string | null>(null);

  readonly canCreate = computed(() => this.context.hasPermission('Company.Vehicles.Create'));
  readonly canEdit = computed(() => this.context.hasPermission('Company.Vehicles.Edit'));
  readonly canDisable = computed(() => this.context.hasPermission('Company.Vehicles.Disable'));

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((v) => ({
      cells: [
        { kind: 'text', text: v.vehicleNumber, weight: '600' },
        { kind: 'text', text: v.vehicleName },
        { kind: 'text', text: v.vehicleType, color: 'var(--color-text-muted)' },
        {
          kind: 'text',
          text: v.assignedBranchId ? `${v.assignedBranchName ?? ''} (${v.assignedBranchCode ?? ''})` : 'Central',
          color: 'var(--color-text-muted)',
        },
        { kind: 'badge', text: v.isActive ? 'Active' : 'Inactive', tone: v.isActive ? 'good' : 'mute' },
        {
          kind: 'actions',
          actions: [
            ...(this.canEdit() ? [{ label: 'Edit', onClick: () => this.router.navigate(['/company/vehicles', v.id, 'edit']) }] : []),
            ...(this.canDisable() ? [{ label: v.isActive ? 'Deactivate' : 'Activate', onClick: () => void this.toggle(v) }] : []),
          ],
        },
      ],
    })),
  );

  ngOnInit(): void {
    this.load();
  }

  setIncludeInactive(value: boolean): void {
    this.includeInactive.set(value);
    this.load();
  }

  setType(value: string): void {
    this.vehicleType.set((VEHICLE_TYPES as readonly string[]).includes(value) ? (value as VehicleType) : '');
    this.load();
  }

  setBranch(id: string | null): void {
    this.branchId.set(id);
    if (id) this.unassigned.set(false);
    this.load();
  }

  setUnassigned(value: boolean): void {
    this.unassigned.set(value);
    if (value) this.branchId.set(null);
    this.load();
  }

  goToCreate(): void {
    this.router.navigate(['/company/vehicles/new']);
  }

  load(): void {
    this.state.set('loading');
    this.service
      .list({
        assignedBranchId: this.branchId(),
        unassigned: this.unassigned(),
        vehicleType: this.vehicleType(),
        includeInactive: this.includeInactive(),
      })
      .subscribe({
        next: (items) => {
          this.items.set(items);
          this.state.set('ready');
        },
        error: () => this.state.set('error'),
      });
  }

  /** D14: opens the reason prompt for the activate / deactivate action. */
  toggle(v: Vehicle): void {
    this.notice.set(null);
    this.statusError.set(null);
    this.statusTarget.set(v);
  }

  cancelStatus(): void {
    this.statusTarget.set(null);
    this.statusError.set(null);
  }

  confirmStatus(reason: string | null): void {
    const v = this.statusTarget();
    if (!v || !reason) return;
    const deactivate = v.isActive;
    this.statusBusy.set(true);
    this.statusError.set(null);
    this.service.setStatus(v.id, !v.isActive, reason).subscribe({
      next: () => {
        this.statusBusy.set(false);
        this.statusTarget.set(null);
        this.toast.show(`${v.vehicleNumber} ${deactivate ? 'deactivated' : 'activated'}.`);
        this.load();
      },
      error: (err: unknown) => {
        this.statusBusy.set(false);
        const reasonError = reasonFieldError(err);
        if (reasonError) {
          this.statusError.set(reasonError); // stays in the prompt so the user can fix it
          return;
        }
        this.statusTarget.set(null);
        this.notice.set(refusalMessage(err, 'Could not change the vehicle status.'));
      },
    });
  }

  statusPromptTitle(): string {
    const v = this.statusTarget();
    return v ? `${v.isActive ? 'Deactivate' : 'Activate'} ${v.vehicleNumber}?` : '';
  }
}
