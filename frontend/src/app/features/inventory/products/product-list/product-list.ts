import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../../shared/ui/button/button';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { ProductsService } from '../products.service';
import type { ProductListItem } from '../products.models';
import { problemText } from '../../../../shared/scope/problem-text';

const COLS: DataTableColumn[] = [
  { label: 'Product', align: 'left' },
  { label: 'Category', align: 'left' },
  { label: 'Base unit', align: 'left' },
  { label: 'Tax', align: 'left' },
  { label: 'Reorder level', align: 'right' },
  { label: 'Status', align: 'left' },
  { label: '', align: 'right' },
];

const PAGE_SIZE = 20;

@Component({
  selector: 'app-product-list',
  standalone: true,
  imports: [UiDataTable, UiButton],
  templateUrl: './product-list.html',
  styleUrl: '../../../administration/users/user-list/user-list.scss',
})
export class ProductList implements OnInit {
  private readonly productsService = inject(ProductsService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly search = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<ProductListItem[]>([]);
  readonly total = signal(0);

  readonly canCreate = computed(() => this.auth.hasPermission('Inventory.Create'));
  readonly canEdit = computed(() => this.auth.hasPermission('Inventory.Edit'));
  readonly canDelete = computed(() => this.auth.hasPermission('Inventory.Delete'));

  readonly hasMore = computed(() => this.page() * PAGE_SIZE < this.total());

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((p) => ({
      cells: [
        { kind: 'text', text: p.name, sub: p.sku, weight: '600' },
        { kind: 'text', text: p.category ?? '—', color: 'var(--color-text-muted)' },
        { kind: 'text', text: p.baseUnitLabel, color: 'var(--color-text-muted)' },
        { kind: 'text', text: p.taxLabel, color: 'var(--color-text-muted)' },
        { kind: 'text', text: String(p.reorderLevel), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'badge', text: p.isActive ? 'Active' : 'Inactive', tone: p.isActive ? 'good' : 'mute' },
        {
          kind: 'actions',
          actions: [
            ...(this.canEdit() ? [{ label: 'Edit', onClick: () => this.router.navigate(['/inventory/products', p.id, 'edit']) }] : []),
            ...(this.canEdit() ? [{ label: p.isActive ? 'Deactivate' : 'Activate', onClick: () => this.toggle(p) }] : []),
            ...(this.canDelete() ? [{ label: 'Delete', onClick: () => this.remove(p) }] : []),
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
    this.router.navigate(['/inventory/products/new']);
  }

  private load(): void {
    this.state.set('loading');
    this.productsService.list({ search: this.search(), page: this.page(), pageSize: PAGE_SIZE }).subscribe({
      next: (res) => {
        this.items.set(res.items);
        this.total.set(res.total);
        this.state.set('ready');
      },
      error: () => this.state.set('error'),
    });
  }

  private remove(p: ProductListItem): void {
    this.confirm
      .ask({
        title: `Delete ${p.name}?`,
        body:
          'The product, its units and prices are removed permanently. This only works for a product that was never ' +
          'purchased, sold, adjusted or transferred; otherwise deactivate it instead.',
        confirmLabel: 'Delete',
        destructive: true,
      })
      .then((confirmed) => {
        if (!confirmed) return;
        this.productsService.delete(p.id).subscribe({
          next: () => {
            this.toast.show(`${p.name} deleted.`);
            // Step back when the last row of a later page was removed.
            if (this.items().length === 1 && this.page() > 1) this.page.update((n) => n - 1);
            this.load();
          },
          error: (err) => this.toast.show(problemText(err, 'Could not delete this product.')),
        });
      });
  }

  private toggle(p: ProductListItem): void {
    const disabling = p.isActive;
    this.confirm
      .ask({
        title: disabling ? `Deactivate ${p.name}?` : `Activate ${p.name}?`,
        body: disabling
          ? 'This product will no longer be selectable on purchases or POS. This can be reversed later.'
          : 'This product will be selectable on purchases and POS again.',
        confirmLabel: disabling ? 'Deactivate' : 'Activate',
        destructive: disabling,
      })
      .then((confirmed) => {
        if (!confirmed) return;
        this.productsService.setActive(p.id, !p.isActive).subscribe({
          next: () => {
            this.toast.show(`${p.name} ${disabling ? 'deactivated' : 'activated'}.`);
            this.load();
          },
          error: (err) => this.toast.show(err.error?.title ?? 'Could not update this product.'),
        });
      });
  }
}
