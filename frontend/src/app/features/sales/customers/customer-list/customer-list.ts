import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../../shared/ui/button/button';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { CustomersService } from '../customers.service';
import type { CustomerItem } from '../customers.models';

const COLS: DataTableColumn[] = [
  { label: 'Customer', align: 'left' },
  { label: 'Phone', align: 'left' },
  { label: 'Address', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

const PAGE_SIZE = 20;

@Component({
  selector: 'app-customer-list',
  standalone: true,
  imports: [UiDataTable, UiButton],
  templateUrl: './customer-list.html',
  styleUrl: '../../../administration/users/user-list/user-list.scss',
})
export class CustomerList implements OnInit {
  private readonly customersService = inject(CustomersService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly search = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<CustomerItem[]>([]);
  readonly total = signal(0);

  readonly canCreate = computed(() => this.auth.hasPermission('Customers.Create'));
  readonly canEdit = computed(() => this.auth.hasPermission('Customers.Edit'));
  readonly canDisable = computed(() => this.auth.hasPermission('Customers.Disable'));

  readonly hasMore = computed(() => this.page() * PAGE_SIZE < this.total());

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((c) => ({
      cells: [
        { kind: 'text', text: c.name, sub: c.isWalkIn ? 'Walk-in' : undefined, weight: '600' },
        { kind: 'text', text: c.phone ?? '—', color: 'var(--color-text-muted)' },
        { kind: 'text', text: c.address ?? '—', color: 'var(--color-text-muted)' },
        { kind: 'badge', text: c.isActive ? 'Active' : 'Inactive', tone: c.isActive ? 'good' : 'mute' },
        {
          kind: 'actions',
          actions: [
            ...(this.canEdit() ? [{ label: 'Edit', onClick: () => this.router.navigate(['/sales/customers', c.id, 'edit']) }] : []),
            ...(this.canDisable() && !c.isWalkIn
              ? [{ label: c.isActive ? 'Deactivate' : 'Activate', onClick: () => this.toggle(c) }]
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
    this.router.navigate(['/sales/customers/new']);
  }

  private load(): void {
    this.state.set('loading');
    this.customersService.list({ search: this.search(), page: this.page(), pageSize: PAGE_SIZE }).subscribe({
      next: (res) => {
        this.items.set(res.items);
        this.total.set(res.total);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  private toggle(c: CustomerItem): void {
    const disabling = c.isActive;
    this.confirm
      .ask({
        title: disabling ? `Deactivate ${c.name}?` : `Activate ${c.name}?`,
        body: disabling
          ? 'This customer will no longer be selectable at POS. This can be reversed later.'
          : 'This customer will be selectable at POS again.',
        confirmLabel: disabling ? 'Deactivate' : 'Activate',
        destructive: disabling,
      })
      .then((confirmed) => {
        if (!confirmed) return;
        this.customersService.setActive(c.id, !c.isActive).subscribe({
          next: () => {
            this.toast.show(`${c.name} ${disabling ? 'deactivated' : 'activated'}.`);
            this.load();
          },
          error: (err) => this.toast.show(err.error?.title ?? 'Could not update this customer.'),
        });
      });
  }
}
