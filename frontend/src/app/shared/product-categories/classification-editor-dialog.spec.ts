import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { ClassificationEditorDialog, type ClassificationEditorTarget, type ClassificationSaved } from './classification-editor-dialog';
import { ProductCategoriesService } from './product-categories.service';
import type { ProductCategory, ProductSubcategory } from './product-categories.models';

const cats: ProductCategory[] = [
  { id: 'c1', code: 'GRAINS', name: 'Grains', sortOrder: 0, isActive: true, productCount: 0, subcategories: [] },
  { id: 'c2', code: 'OLD', name: 'Retired', sortOrder: 1, isActive: false, productCount: 0, subcategories: [] },
];
const created: ProductSubcategory = { id: 's9', categoryId: 'c1', code: 'OTHERS', name: 'Others', sortOrder: 0, isActive: true, productCount: 0 };

function mount(target: ClassificationEditorTarget) {
  const svc = {
    create: vi.fn(() => of(cats[0])),
    update: vi.fn(() => of(cats[0])),
    createSubcategory: vi.fn(() => of(created)),
    updateSubcategory: vi.fn(() => of(created)),
  };
  TestBed.configureTestingModule({ providers: [{ provide: ProductCategoriesService, useValue: svc }] });
  const f = TestBed.createComponent(ClassificationEditorDialog);
  f.componentRef.setInput('target', target);
  f.componentRef.setInput('categories', cats);
  const saved: ClassificationSaved[] = [];
  let closed = 0;
  f.componentInstance.saved.subscribe((s) => saved.push(s));
  f.componentInstance.closed.subscribe(() => closed++);
  f.detectChanges();
  return { c: f.componentInstance, f, svc, el: f.nativeElement as HTMLElement, saved, closed: () => closed };
}

describe('ClassificationEditorDialog', () => {
  it('suggests the code from the name until the code is edited by hand', () => {
    const { c } = mount({ kind: 'category', categoryId: null, id: null });
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
    const { c, svc, saved } = mount({ kind: 'category', categoryId: null, id: null });
    c.form.patchValue({ code: 'bad code', name: 'X', sortOrder: 0 });
    c.submit();
    expect(svc.create).not.toHaveBeenCalled();
    c.form.patchValue({ code: 'X1', name: 'X', sortOrder: 2 });
    c.submit();
    expect(svc.create).toHaveBeenCalledWith({ code: 'X1', name: 'X', sortOrder: 2 });
    expect(saved).toEqual([{ kind: 'category', created: true, category: cats[0] }]);
    c.applyServerErrors({ errors: { code: ['Code already exists.'] } });
    expect(c.form.controls.code.errors?.['server']).toBe('Code already exists.');
    c.applyServerErrors({ title: 'Boom' });
    expect(c.serverError()).toBe('Boom');
  });

  it('edit prefills and keeps the existing code', () => {
    const { c, svc } = mount({ kind: 'category', categoryId: null, id: 'c1', existing: cats[0] });
    expect(c.title()).toBe('Edit category');
    c.form.controls.name.setValue('Cereals');
    c.onNameInput();
    expect(c.form.controls.code.value).toBe('GRAINS');
    c.submit();
    expect(svc.update).toHaveBeenCalledWith('c1', { code: 'GRAINS', name: 'Cereals', sortOrder: 0 });
  });

  it('subcategory shows a read-only "Under category" line and saves under it', () => {
    const { c, el, svc, saved } = mount({ kind: 'subcategory', categoryId: 'c1', id: null });
    expect(el.querySelector('.context')?.textContent).toContain('Under category: Grains');
    expect(el.querySelector('#ce-category')).toBeNull();
    c.form.patchValue({ name: 'Others', code: 'OTHERS' });
    c.submit();
    expect(svc.createSubcategory).toHaveBeenCalledWith('c1', { code: 'OTHERS', name: 'Others', sortOrder: 0 });
    expect(saved).toEqual([{ kind: 'subcategory', created: true, subcategory: created }]);
  });

  it('subcategory without a category shows a required picker of active categories', () => {
    const { c, f, el, svc } = mount({ kind: 'subcategory', categoryId: null, id: null });
    expect(el.querySelector('.context')).toBeNull();
    expect(c.pickableCategories().map((x) => x.id)).toEqual(['c1']);
    c.form.patchValue({ name: 'Others', code: 'OTHERS' });
    c.submit();
    f.detectChanges();
    expect(svc.createSubcategory).not.toHaveBeenCalled();
    expect(el.textContent).toContain('Select a category.');
    c.form.controls.categoryId.setValue('c1');
    c.submit();
    expect(svc.createSubcategory).toHaveBeenCalledWith('c1', expect.objectContaining({ code: 'OTHERS' }));
  });

  it('Cancel emits closed', () => {
    const { el, closed } = mount({ kind: 'category', categoryId: null, id: null });
    const cancel = Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Cancel')!;
    cancel.click();
    expect(closed()).toBe(1);
  });
});

describe('ClassificationEditorDialog HSN code (subcategories)', () => {
  it('shows the HSN field for a subcategory', () => {
    expect(mount({ kind: 'subcategory', categoryId: 'c1', id: null }).el.querySelector('#ce-hsn')).not.toBeNull();
  });

  it('has no HSN field for a category', () => {
    expect(mount({ kind: 'category', categoryId: null, id: null }).el.querySelector('#ce-hsn')).toBeNull();
  });

  it('posts a 4/6/8 digit code on create', () => {
    const { c, svc } = mount({ kind: 'subcategory', categoryId: 'c1', id: null });
    c.form.patchValue({ code: 'OTH', name: 'Others', hsnCode: '2202' });
    c.submit();
    expect(svc.createSubcategory).toHaveBeenCalledWith('c1', { code: 'OTH', name: 'Others', sortOrder: 0, hsnCode: '2202' });
  });

  it('prefills the code on edit and posts null when it is emptied', () => {
    const { c, svc } = mount({ kind: 'subcategory', categoryId: 'c1', id: 's9', existing: { code: 'OTH', name: 'Others', sortOrder: 0, hsnCode: '100630' } });
    expect(c.form.controls.hsnCode.value).toBe('100630');
    c.form.controls.hsnCode.setValue('');
    c.submit();
    expect(svc.updateSubcategory).toHaveBeenCalledWith('c1', 's9', expect.objectContaining({ hsnCode: null }));
  });

  it('rejects an invalid HSN and maps a server hsnCode error', () => {
    const { c, svc } = mount({ kind: 'subcategory', categoryId: 'c1', id: null });
    c.form.patchValue({ code: 'OTH', name: 'Others', hsnCode: '12345' });
    c.submit();
    expect(svc.createSubcategory).not.toHaveBeenCalled();
    expect(c.fieldError('hsnCode')).toBe('The HSN/SAC code must be 4, 6 or 8 digits.');
    c.applyServerErrors({ errors: { hsnCode: ['Bad code.'] } });
    expect(c.fieldError('hsnCode')).toBe('Bad code.');
  });
});
