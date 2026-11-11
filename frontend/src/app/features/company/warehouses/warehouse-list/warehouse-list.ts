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
import type { Warehouse } from '../warehouses.models';
import { WarehousesService } from '../warehouses.service';

const COLS: DataTableColumn[] = [
  { label: 'Code', align: 'left' },
  { label: 'Name', align: 'left' },
  { label: 'Ownership', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

@Component({
  selector: 'app-warehouse-list',
  standalone: true,
  imports: [UiDataTable, UiButton, BranchSelect, ReasonPrompt],
  templateUrl: './warehouse-list.html',
  styleUrl: '../../company-admin.scss',
})
export class WarehouseList implements OnInit {
  private readonly service = inject(WarehousesService);
  private readonly context = inject(ContextService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<Warehouse[]>([]);
  readonly includeInactive = signal(false);
  readonly ownership = signal<'Company' | 'Branch' | ''>('');
  readonly branchId = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly statusTarget = signal<Warehouse | null>(null);
  readonly statusBusy = signal(false);
  readonly statusError = signal<string | null>(null);

  readonly canCreate = computed(() => this.context.hasPermission('Company.Warehouses.Create'));
  readonly canEdit = computed(() => this.context.hasPermission('Company.Warehouses.Edit'));
  readonly canDisable = computed(() => this.context.hasPermission('Company.Warehouses.Disable'));

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((w) => ({
      cells: [
        { kind: 'text', text: w.code, weight: '600' },
        { kind: 'text', text: w.name },
        { kind: 'text', text: w.ownership === 'Company' ? 'Company' : `Branch: ${w.branchName ?? ''} (${w.branchCode ?? ''})`, color: 'var(--color-text-muted)' },
        { kind: 'badge', text: w.isActive ? 'Active' : 'Inactive', tone: w.isActive ? 'good' : 'mute' },
        {
          kind: 'actions',
          actions: [
            ...(this.canEdit() ? [{ label: 'Edit', onClick: () => this.router.navigate(['/company/warehouses', w.id, 'edit']) }] : []),
            ...(this.canDisable() ? [{ label: w.isActive ? 'Deactivate' : 'Activate', onClick: () => void this.toggle(w) }] : []),
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

  setOwnership(value: string): void {
    this.ownership.set(value === 'Company' || value === 'Branch' ? value : '');
    this.load();
  }

  setBranch(id: string | null): void {
    this.branchId.set(id);
    this.load();
  }

  goToCreate(): void {
    this.router.navigate(['/company/warehouses/new']);
  }

  load(): void {
    this.state.set('loading');
    this.service
      .list({ branchId: this.branchId(), ownership: this.ownership(), includeInactive: this.includeInactive() })
      .subscribe({
        next: (items) => {
          this.items.set(items);
          this.state.set('ready');
        },
        error: () => this.state.set('error'),
      });
  }

  /** D14: opens the reason prompt for the activate / deactivate action. */
  toggle(w: Warehouse): void {
    this.notice.set(null);
    this.statusError.set(null);
    this.statusTarget.set(w);
  }

  cancelStatus(): void {
    this.statusTarget.set(null);
    this.statusError.set(null);
  }

  confirmStatus(reason: string | null): void {
    const w = this.statusTarget();
    if (!w || !reason) return;
    const deactivate = w.isActive;
    this.statusBusy.set(true);
    this.statusError.set(null);
    this.service.setStatus(w.id, !w.isActive, reason).subscribe({
      next: () => {
        this.statusBusy.set(false);
        this.statusTarget.set(null);
        this.toast.show(`${w.name} ${deactivate ? 'deactivated' : 'activated'}.`);
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
        this.notice.set(refusalMessage(err, 'Could not change the warehouse status.'));
      },
    });
  }

  statusPromptTitle(): string {
    const w = this.statusTarget();
    return w ? `${w.isActive ? 'Deactivate' : 'Activate'} ${w.name}?` : '';
  }
}
