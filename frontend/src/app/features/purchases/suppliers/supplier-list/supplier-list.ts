import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../../shared/ui/button/button';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { SuppliersService } from '../suppliers.service';
import type { SupplierItem } from '../suppliers.models';

const COLS: DataTableColumn[] = [
  { label: 'Supplier', align: 'left' },
  { label: 'City', align: 'left' },
  { label: 'Terms', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

@Component({
  selector: 'app-supplier-list',
  standalone: true,
  imports: [UiDataTable, UiButton],
  templateUrl: './supplier-list.html',
  styleUrl: '../../../administration/users/user-list/user-list.scss',
})
export class SupplierList implements OnInit {
  private readonly suppliersService = inject(SuppliersService);
  private readonly toast = inject(ToastService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly search = signal('');
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<SupplierItem[]>([]);
  readonly total = signal(0);

  readonly canCreate = computed(() => this.auth.hasPermission('Suppliers.Create'));
  readonly canEdit = computed(() => this.auth.hasPermission('Suppliers.Edit'));
  readonly canDisable = computed(() => this.auth.hasPermission('Suppliers.Disable'));

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((s) => ({
      cells: [
        { kind: 'text', text: s.name, sub: s.gstin ?? undefined, weight: '600' },
        { kind: 'text', text: s.city ?? '—', color: 'var(--color-text-muted)' },
        { kind: 'text', text: s.paymentTerms ?? '—', color: 'var(--color-text-muted)' },
        { kind: 'badge', text: s.isActive ? 'Active' : 'Inactive', tone: s.isActive ? 'good' : 'mute' },
        {
          kind: 'actions',
          actions: [
            ...(this.canEdit() ? [{ label: 'Edit', onClick: () => this.router.navigate(['/purchases/suppliers', s.id, 'edit']) }] : []),
            ...(this.canDisable()
              ? [{ label: s.isActive ? 'Deactivate' : 'Activate', onClick: () => this.toggle(s) }]
              : []),
          ],
        },
      ],
    })),
  );

  ngOnInit(): void {
    this.load();
  }

  onSearch(value: string): void {
    this.search.set(value);
    this.load();
  }

  retry(): void {
    this.load();
  }

  goToCreate(): void {
    this.router.navigate(['/purchases/suppliers/new']);
  }

  private load(): void {
    this.state.set('loading');
    this.suppliersService.list(this.search()).subscribe({
      next: (res) => {
        this.items.set(res.items);
        this.total.set(res.total);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  private toggle(s: SupplierItem): void {
    this.suppliersService.setActive(s.id, !s.isActive).subscribe({
      next: () => {
        this.toast.show(`${s.name} ${s.isActive ? 'deactivated' : 'activated'}.`);
        this.load();
      },
      error: (err) => this.toast.show(err.error?.title ?? 'Could not update this supplier.'),
    });
  }
}
