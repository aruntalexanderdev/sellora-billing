import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ContextService } from '../../../../core/context/context.service';
import { UiButton } from '../../../../shared/ui/button/button';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ReasonPrompt } from '../../../administration/access-ui/reason-prompt';
import { reasonFieldError, refusalMessage } from '../../company-admin';
import type { Branch } from '../branches.models';
import { BranchesService } from '../branches.service';

const COLS: DataTableColumn[] = [
  { label: 'Code', align: 'left' },
  { label: 'Name', align: 'left' },
  { label: 'City', align: 'left' },
  { label: 'GSTIN', align: 'left' },
  { label: 'Invoice prefix', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

@Component({
  selector: 'app-branch-list',
  standalone: true,
  imports: [UiDataTable, UiButton, ReasonPrompt],
  templateUrl: './branch-list.html',
  styleUrl: '../../company-admin.scss',
})
export class BranchList implements OnInit {
  private readonly service = inject(BranchesService);
  private readonly context = inject(ContextService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<Branch[]>([]);
  readonly includeInactive = signal(false);
  readonly notice = signal<string | null>(null);
  readonly statusTarget = signal<Branch | null>(null);
  readonly statusBusy = signal(false);
  readonly statusError = signal<string | null>(null);

  readonly canCreate = computed(() => this.context.hasPermission('Company.Branches.Create'));
  readonly canEdit = computed(() => this.context.hasPermission('Company.Branches.Edit'));
  readonly canDisable = computed(() => this.context.hasPermission('Company.Branches.Disable'));

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((b) => ({
      cells: [
        { kind: 'text', text: b.code, weight: '600' },
        { kind: 'text', text: b.name, sub: b.isDefault ? 'Default branch' : undefined },
        { kind: 'text', text: b.city ?? '', color: 'var(--color-text-muted)' },
        { kind: 'text', text: b.gstin ?? '', color: 'var(--color-text-muted)' },
        { kind: 'text', text: b.effectiveInvoicePrefix, color: 'var(--color-text-muted)' },
        { kind: 'badge', text: b.isActive ? 'Active' : 'Inactive', tone: b.isActive ? 'good' : 'mute' },
        {
          kind: 'actions',
          actions: [
            ...(this.canEdit() ? [{ label: 'Edit', onClick: () => this.router.navigate(['/company/branches', b.id, 'edit']) }] : []),
            ...(this.canDisable() && !(b.isDefault && b.isActive)
              ? [{ label: b.isActive ? 'Deactivate' : 'Activate', onClick: () => void this.toggle(b) }]
              : []),
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

  goToCreate(): void {
    this.router.navigate(['/company/branches/new']);
  }

  load(): void {
    this.state.set('loading');
    this.service.list(this.includeInactive()).subscribe({
      next: (items) => {
        this.items.set(items);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  /** D14: opens the reason prompt for the activate / deactivate action. */
  toggle(b: Branch): void {
    this.notice.set(null);
    this.statusError.set(null);
    this.statusTarget.set(b);
  }

  cancelStatus(): void {
    this.statusTarget.set(null);
    this.statusError.set(null);
  }

  confirmStatus(reason: string | null): void {
    const b = this.statusTarget();
    if (!b || !reason) return;
    const deactivate = b.isActive;
    this.statusBusy.set(true);
    this.statusError.set(null);
    this.service.setStatus(b.id, !b.isActive, reason).subscribe({
      next: () => {
        this.statusBusy.set(false);
        this.statusTarget.set(null);
        this.toast.show(`${b.name} ${deactivate ? 'deactivated' : 'activated'}.`);
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
        this.notice.set(refusalMessage(err, 'Could not change the branch status.'));
      },
    });
  }

  statusPromptTitle(): string {
    const b = this.statusTarget();
    return b ? `${b.isActive ? 'Deactivate' : 'Activate'} ${b.name}?` : '';
  }
}
