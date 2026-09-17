import { Component, inject, signal } from '@angular/core';
import { UiButton } from '../../shared/ui/button/button';
import { UiBadge, type BadgeTone } from '../../shared/ui/badge/badge';
import { UiCard } from '../../shared/ui/card/card';
import { UiDataTable } from '../../shared/ui/data-table/data-table';
import { ToastService } from '../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../shared/ui/confirm-dialog/confirm-dialog.service';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../shared/ui/data-table/data-table.types';

const SAMPLE_COLS: DataTableColumn[] = [
  { label: 'Product', align: 'left' },
  { label: 'Stock', align: 'right' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

const SAMPLE_ROWS: DataTableRow[] = [
  {
    cells: [
      { kind: 'text', text: 'Ponni Rice', sub: 'RICE-BP10', weight: '600', wrap: 'normal' },
      { kind: 'text', text: '240 kg', align: 'right', weight: '600' },
      { kind: 'badge', text: 'In stock', tone: 'good' },
      { kind: 'actions', actions: [{ label: 'Edit', onClick: () => {} }] },
    ],
  },
  {
    cells: [
      { kind: 'text', text: 'Toor Dal', sub: 'DAL-TUR', weight: '600', wrap: 'normal' },
      { kind: 'text', text: '42 kg', align: 'right', weight: '600' },
      { kind: 'badge', text: 'Low', tone: 'warn' },
      { kind: 'actions', actions: [{ label: 'Edit', onClick: () => {} }] },
    ],
  },
  {
    cells: [
      { kind: 'text', text: 'Iodised Salt', sub: 'SALT-IOD', weight: '600', wrap: 'normal' },
      { kind: 'text', text: '0 kg', align: 'right', weight: '600' },
      { kind: 'badge', text: 'Out of stock', tone: 'bad' },
      { kind: 'actions', actions: [{ label: 'Edit', onClick: () => {} }] },
    ],
  },
];

@Component({
  selector: 'app-ui-showcase',
  standalone: true,
  imports: [UiButton, UiBadge, UiCard, UiDataTable],
  templateUrl: './ui-showcase.html',
  styleUrl: './ui-showcase.scss',
})
export class UiShowcase {
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);

  readonly badgeTones: BadgeTone[] = ['good', 'warn', 'bad', 'info', 'mute'];
  readonly tableState = signal<DataTableState>('ready');
  readonly cols = SAMPLE_COLS;
  readonly rows = signal<DataTableRow[]>(SAMPLE_ROWS);

  setTableState(state: DataTableState): void {
    this.tableState.set(state);
  }

  setEmpty(): void {
    this.tableState.set('ready');
    this.rows.set([]);
  }

  restoreRows(): void {
    this.rows.set(SAMPLE_ROWS);
  }

  showToast(): void {
    this.toast.show('GRN-0118 received · 300 kg of Ponni Rice added to stock.');
  }

  async askConfirm(): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: 'Deactivate this user?',
      body: 'They will no longer be able to sign in. This can be reversed later.',
      confirmLabel: 'Deactivate',
      destructive: true,
    });
    this.toast.show(confirmed ? 'User deactivated.' : 'Cancelled — no changes made.');
  }
}
