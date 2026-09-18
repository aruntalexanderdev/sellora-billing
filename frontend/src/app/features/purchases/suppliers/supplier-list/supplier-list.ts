import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../../shared/ui/button/button';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { SuppliersService } from '../suppliers.service';
import type { SupplierItem } from '../suppliers.models';

const COLS: DataTableColumn[] = [
  { label: 'Supplier', align: 'left' },
  { label: 'City', align: 'left' },
  { label: 'Terms', align: 'left' },
  { label: 'Currency', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

const PAGE_SIZE = 20;

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
  private readonly confirm = inject(ConfirmDialogService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly search = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<SupplierItem[]>([]);
  readonly total = signal(0);

  readonly canCreate = computed(() => this.auth.hasPermission('Suppliers.Create'));
  readonly canEdit = computed(() => this.auth.hasPermission('Suppliers.Edit'));
  readonly canDisable = computed(() => this.auth.hasPermission('Suppliers.Disable'));

  readonly hasMore = computed(() => this.page() * PAGE_SIZE < this.total());

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((s) => ({
      cells: [
        { kind: 'text', text: s.name, sub: s.gstin ?? undefined, weight: '600' },
        { kind: 'text', text: s.city ?? '—', color: 'var(--color-text-muted)' },
        { kind: 'text', text: s.paymentTerms ?? '—', color: 'var(--color-text-muted)' },
        { kind: 'text', text: s.currencyCode, color: 'var(--color-text-muted)' },
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
    this.page.set(1);
    this.load();
  }

  onPageChange(page: number): void {
    this.page.set(page);
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
    this.suppliersService.list({ search: this.search(), page: this.page(), pageSize: PAGE_SIZE }).subscribe({
      next: (res) => {
        this.items.set(res.items);
        this.total.set(res.total);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  private toggle(s: SupplierItem): void {
    const disabling = s.isActive;
    this.confirm
      .ask({
        title: disabling ? `Deactivate ${s.name}?` : `Activate ${s.name}?`,
        body: disabling
          ? 'This supplier will no longer be selectable on new purchases. This can be reversed later.'
          : 'This supplier will be selectable on new purchases again.',
        confirmLabel: disabling ? 'Deactivate' : 'Activate',
        destructive: disabling,
      })
      .then((confirmed) => {
        if (!confirmed) return;
        this.suppliersService.setActive(s.id, !s.isActive).subscribe({
          next: () => {
            this.toast.show(`${s.name} ${disabling ? 'deactivated' : 'activated'}.`);
            this.load();
          },
          error: (err) => this.toast.show(err.error?.title ?? 'Could not update this supplier.'),
        });
      });
  }
}
