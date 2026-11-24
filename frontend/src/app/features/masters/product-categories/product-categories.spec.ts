import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { ProductCategories } from './product-categories';
import { AuthService } from '../../../core/auth/auth.service';
import { ProductCategoriesService } from '../../../shared/product-categories/product-categories.service';
import { ConfirmDialogService } from '../../../shared/ui/confirm-dialog/confirm-dialog.service';
import type { ProductCategory } from '../../../shared/product-categories/product-categories.models';

const cats: ProductCategory[] = [
  { id: 'c1', code: 'GRAINS', name: 'Grains', sortOrder: 0, isActive: true, productCount: 3, subcategories: [
    { id: 's1', categoryId: 'c1', code: 'RICE', name: 'Rice', sortOrder: 0, isActive: true, productCount: 2 },
    { id: 's2', categoryId: 'c1', code: 'OLD', name: 'Old', sortOrder: 1, isActive: false, productCount: 0 },
  ] },
];

function mount(canManage = true) {
  const svc = {
    list: vi.fn(() => of(cats)),
    create: vi.fn(() => of(cats[0])),
    update: vi.fn(() => of(cats[0])),
    createSubcategory: vi.fn(() => of(cats[0].subcategories[0])),
    setActive: vi.fn(() => of(cats[0])),
  };
  TestBed.configureTestingModule({
    providers: [
      { provide: ProductCategoriesService, useValue: svc },
      { provide: AuthService, useValue: { hasPermission: (c: string) => canManage && c === 'Inventory.Categories' } },
    ],
  });
  const f = TestBed.createComponent(ProductCategories);
  f.detectChanges();
  return { c: f.componentInstance, f, svc, el: f.nativeElement as HTMLElement };
}

describe('ProductCategories master', () => {
  it('lists categories with counts; subcategories expand and inactive ones follow the toggle', () => {
    const { c, f, svc, el } = mount();
    expect(svc.list).toHaveBeenLastCalledWith(false);
    expect(el.textContent).toContain('3 products');
    c.toggleExpanded('c1');
    expect(c.visibleSubcategories(cats[0]).map((s) => s.id)).toEqual(['s1']);
    c.setShowInactive(true);
    expect(svc.list).toHaveBeenLastCalledWith(true);
    expect(c.visibleSubcategories(cats[0]).map((s) => s.id)).toEqual(['s1', 's2']);
    f.detectChanges();
    expect(el.querySelectorAll('.sub-row').length).toBe(2);
  });

  it('hides management actions without Inventory.Categories', () => {
    const { el } = mount(false);
    expect(el.textContent).not.toContain('New category');
    expect(el.textContent).not.toContain('Deactivate');
  });

  it('opens the editor modal; Add subcategory carries the category context', () => {
    const { c, f, el } = mount();
    expect(el.querySelector('[role="dialog"]')).toBeNull();

    c.startSubcategory(cats[0]);
    f.detectChanges();
    const dialog = el.querySelector('[role="dialog"]')!;
    expect(dialog).not.toBeNull();
    expect(dialog.textContent).toContain('New subcategory');
    expect(dialog.textContent).toContain('Under category: Grains');
    expect(c.isExpanded('c1')).toBe(true);

    c.closeEditor();
    f.detectChanges();
    expect(el.querySelector('[role="dialog"]')).toBeNull();

    c.startSubcategory(cats[0], cats[0].subcategories[0]);
    f.detectChanges();
    expect(el.querySelector('[role="dialog"]')!.textContent).toContain('Edit subcategory');
    expect((el.querySelector('#ce-name') as HTMLInputElement).value).toBe('Rice');
  });

  it('category create uses the same modal and reloads after save', () => {
    const { c, f, el, svc } = mount();
    c.startCategory();
    f.detectChanges();
    expect(el.querySelector('[role="dialog"]')!.textContent).toContain('New category');
    expect(el.querySelector('.context')).toBeNull();
    const calls = svc.list.mock.calls.length;
    c.onSaved();
    f.detectChanges();
    expect(el.querySelector('[role="dialog"]')).toBeNull();
    expect(svc.list.mock.calls.length).toBe(calls + 1);
  });

  it('deactivation asks for confirmation first', async () => {
    const { c, svc } = mount();
    const ask = vi.spyOn(TestBed.inject(ConfirmDialogService), 'ask').mockResolvedValue(true);
    c.toggleCategory(cats[0]);
    await Promise.resolve();
    expect(ask).toHaveBeenCalled();
    await Promise.resolve();
    expect(svc.setActive).toHaveBeenCalledWith('c1', false);
  });
});
