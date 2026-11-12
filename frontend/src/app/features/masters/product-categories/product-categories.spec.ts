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

  it('suggests the code from the name until the code is edited by hand', () => {
    const { c } = mount();
    c.startCategory();
    c.form.controls.name.setValue('Fresh Dairy');
    c.onNameInput();
    expect(c.form.controls.code.value).toBe('FRESH-DAIRY');
    c.form.controls.code.setValue('fd');
    c.onCodeInput();
    expect(c.form.controls.code.value).toBe('FD');
    c.form.controls.name.setValue('Something else');
    c.onNameInput();
    expect(c.form.controls.code.value).toBe('FD');
  });

  it('creates a category, rejects invalid codes, and maps server errors', () => {
    const { c, svc } = mount();
    c.startCategory();
    c.form.setValue({ code: 'bad code', name: 'X', sortOrder: 0 });
    c.submit();
    expect(svc.create).not.toHaveBeenCalled();
    c.form.setValue({ code: 'X1', name: 'X', sortOrder: 2 });
    c.submit();
    expect(svc.create).toHaveBeenCalledWith({ code: 'X1', name: 'X', sortOrder: 2 });
    expect(c.editor()).toBeNull();
    c.startCategory();
    c.applyServerErrors({ errors: { code: ['Code already exists.'] } });
    expect(c.form.controls.code.errors?.['server']).toBe('Code already exists.');
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
