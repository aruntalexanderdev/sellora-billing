import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../../shared/ui/button/button';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { TaxesService } from '../taxes.service';
import type { TaxItem } from '../taxes.models';

const COLS: DataTableColumn[] = [
  { label: 'Name', align: 'left' },
  { label: 'Rate', align: 'right' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

@Component({
  selector: 'app-tax-list',
  standalone: true,
  imports: [UiDataTable, UiButton],
  templateUrl: './tax-list.html',
  styleUrl: '../../../administration/users/user-list/user-list.scss',
})
export class TaxList implements OnInit {
  private readonly taxesService = inject(TaxesService);
  private readonly toast = inject(ToastService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<TaxItem[]>([]);

  readonly canEdit = computed(() => this.auth.hasPermission('Masters.Edit'));

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((t) => ({
      cells: [
        { kind: 'text', text: t.name, weight: '600' },
        { kind: 'text', text: t.ratePercent + '%', align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'badge', text: t.isActive ? 'Active' : 'Inactive', tone: t.isActive ? 'good' : 'mute' },
        {
          kind: 'actions',
          actions: this.canEdit()
            ? [
                { label: 'Edit', onClick: () => this.router.navigate(['/administration/taxes', t.id, 'edit']) },
                { label: t.isActive ? 'Deactivate' : 'Activate', onClick: () => this.toggle(t) },
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
    this.router.navigate(['/administration/taxes/new']);
  }

  private load(): void {
    this.state.set('loading');
    this.taxesService.list().subscribe({
      next: (taxes) => {
        this.items.set(taxes);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  private toggle(t: TaxItem): void {
    this.taxesService.setActive(t.id, !t.isActive).subscribe({
      next: () => {
        this.toast.show(`${t.name} ${t.isActive ? 'deactivated' : 'activated'}.`);
        this.load();
      },
      error: (err) => this.toast.show(err.error?.title ?? 'Could not update this tax.'),
    });
  }
}
