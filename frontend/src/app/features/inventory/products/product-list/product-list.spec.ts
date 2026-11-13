import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ProductList } from './product-list';
import { ProductsService } from '../products.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import type { ProductListItem } from '../products.models';

const RICE: ProductListItem = {
  id: 'p1', sku: 'RICE-1', name: 'Rice', category: null, baseUnitLabel: 'kg', taxLabel: 'GST 5%', reorderLevel: 0, isActive: true,
  canDelete: true,
};

function setup(opts: { permissions?: string[]; confirm?: boolean; remove?: () => unknown; item?: Partial<ProductListItem> } = {}) {
  const permissions = opts.permissions ?? [];
  const toast = { show: vi.fn() };
  const ask = vi.fn(() => Promise.resolve(opts.confirm ?? true));
  const list = vi.fn(() => of({ items: [{ ...RICE, ...opts.item }], total: 1, page: 1, pageSize: 20 }));
  const remove = vi.fn(opts.remove ?? (() => of(undefined)));
  TestBed.configureTestingModule({
    imports: [ProductList],
    providers: [
      provideRouter([]),
      { provide: ProductsService, useValue: { list, delete: remove, setActive: vi.fn() } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
      { provide: ConfirmDialogService, useValue: { ask } },
      { provide: ToastService, useValue: toast },
    ],
  });
  vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(ProductList);
  fixture.detectChanges();
  const component = fixture.componentInstance;
  const actions = () => (component.rows()[0].cells.at(-1) as { actions: { label: string; onClick: () => void }[] }).actions;
  const click = async (label: string) => {
    actions().find((a) => a.label === label)!.onClick();
    await Promise.resolve();
    await Promise.resolve();
  };
  return { component, toast, ask, list, remove, actions, click };
}

describe('ProductList delete', () => {
  it('hides Delete without Inventory.Delete', () => {
    const { actions } = setup({ permissions: ['Inventory.Edit'] });
    expect(actions().map((a) => a.label)).not.toContain('Delete');
  });

  it('shows Delete with Inventory.Delete only', () => {
    const { actions } = setup({ permissions: ['Inventory.Delete'] });
    expect(actions().map((a) => a.label)).toEqual(['Delete']);
  });

  it('hides Delete for a product with history even with Inventory.Delete', () => {
    const { actions } = setup({ permissions: ['Inventory.Edit', 'Inventory.Delete'], item: { canDelete: false } });
    expect(actions().map((a) => a.label)).toEqual(['Edit', 'Deactivate']);
  });

  it('deletes after confirmation, toasts and reloads', async () => {
    const { click, ask, remove, toast, list } = setup({ permissions: ['Inventory.Delete'] });
    await click('Delete');
    expect(ask).toHaveBeenCalledWith(expect.objectContaining({ destructive: true, confirmLabel: 'Delete' }));
    expect(remove).toHaveBeenCalledWith('p1');
    expect(toast.show).toHaveBeenCalledWith('Rice deleted.');
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('does nothing when the confirmation is cancelled', async () => {
    const { click, remove } = setup({ permissions: ['Inventory.Delete'], confirm: false });
    await click('Delete');
    expect(remove).not.toHaveBeenCalled();
  });

  it('shows the server reason when the product has history', async () => {
    const message = 'This product has history (sales) and cannot be deleted. Deactivate it instead.';
    const { click, toast } = setup({
      permissions: ['Inventory.Delete'],
      remove: () => throwError(() => new HttpErrorResponse({ status: 400, error: { status: 400, errors: { Product: [message] } } })),
    });
    await click('Delete');
    expect(toast.show).toHaveBeenCalledWith(message);
  });
});
