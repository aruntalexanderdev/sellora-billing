import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { CategoryFilter } from './category-filter';
import { ProductCategoriesService } from './product-categories.service';
import { suggestCode, type CategorySelection, type ProductCategory } from './product-categories.models';

const cats: ProductCategory[] = [
  { id: 'c1', code: 'G', name: 'Grains', sortOrder: 0, isActive: true, productCount: 2, subcategories: [
    { id: 's1', categoryId: 'c1', code: 'R', name: 'Rice', sortOrder: 0, isActive: true, productCount: 1 },
  ] },
  { id: 'c2', code: 'D', name: 'Dairy', sortOrder: 1, isActive: true, productCount: 0, subcategories: [] },
];

function mount() {
  TestBed.configureTestingModule({ providers: [{ provide: ProductCategoriesService, useValue: { list: () => of(cats) } }] });
  const f = TestBed.createComponent(CategoryFilter);
  const events: CategorySelection[] = [];
  f.componentInstance.changed.subscribe((e) => events.push(e));
  f.detectChanges();
  const selects = () => Array.from((f.nativeElement as HTMLElement).querySelectorAll('select')) as HTMLSelectElement[];
  return { c: f.componentInstance, f, selects, events };
}

describe('CategoryFilter', () => {
  it('disables the subcategory select until a category is chosen', () => {
    const { c, f, selects } = mount();
    expect(selects()[1].disabled).toBe(true);
    c.onCategory('c1');
    f.detectChanges();
    expect(selects()[1].disabled).toBe(false);
    expect(c.subcategoryOptions().map((s) => s.id)).toEqual(['s1']);
  });

  it('changing the category clears the subcategory and emits ids', () => {
    const { c, events } = mount();
    c.onCategory('c1');
    c.onSubcategory('s1');
    c.onCategory('c2');
    expect(c.subcategoryId()).toBeNull();
    expect(events).toEqual([
      { categoryId: 'c1', subcategoryId: null },
      { categoryId: 'c1', subcategoryId: 's1' },
      { categoryId: 'c2', subcategoryId: null },
    ]);
  });
});

describe('suggestCode', () => {
  it('upper-cases, collapses non-alphanumerics, trims and caps at 20', () => {
    expect(suggestCode('  Fresh & Dairy  ')).toBe('FRESH-DAIRY');
    expect(suggestCode('--a__b--')).toBe('A-B');
    expect(suggestCode('abcdefghij klmnopqrst uvw')).toBe('ABCDEFGHIJ-KLMNOPQRST'.slice(0, 20));
    expect(suggestCode('!!!')).toBe('');
  });
});
