import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import type { TaxAssignment, TaxAssignmentTree, TaxProfile, TaxProfileRef } from '../tax-setup.models';
import { TaxSetupService } from '../tax-setup.service';
import { MappingTab } from './mapping-tab';
import { effectiveText, sourceChip } from './tax-mapping.logic';

const ref = (id: string, name: string): TaxProfileRef => ({ id, code: id, name, regime: 'Gst', treatment: 'Taxable', isActive: true });
const GST5 = ref('gst5', 'GST 5%');
const GST18 = ref('gst18', 'GST 18%');
const assignment = (id: string, scopeType: TaxAssignment['scopeType'], scopeId: string | null, sales: TaxProfileRef): TaxAssignment => ({
  id, scopeType, scopeId, scopeName: '', countryId: 'in', salesProfile: sales, purchaseProfile: null,
});
const effective = (sales: TaxProfileRef, source: 'Company' | 'Category', sourceName: string) => ({ salesProfile: sales, purchaseProfile: sales, source, sourceName });

function tree(): TaxAssignmentTree {
  return {
    countryId: 'in', countryCode: 'IN', countryName: 'India',
    companyDefault: assignment('a-co', 'Company', null, GST18),
    categories: [{
      id: 'c1', code: 'BEV', name: 'Beverages', isActive: true, productCount: 12, overrideCount: 2,
      assignment: assignment('a-c1', 'Category', 'c1', GST18), effective: effective(GST18, 'Company', 'Company'),
      subcategories: [
        {
          id: 's1', code: 'SD', name: 'Soft Drinks', hsnCode: '2202', isActive: true, productCount: 8, overrideCount: 2,
          assignment: null, effective: effective(GST18, 'Category', 'Beverages'),
          overrides: [{ productId: 'p1', sku: 'COLA', name: 'Cola', assignment: assignment('a-p1', 'Product', 'p1', GST5) }],
        },
        { id: 's2', code: 'JU', name: 'Juices', hsnCode: null, isActive: true, productCount: 4, overrideCount: 0, assignment: assignment('a-s2', 'Subcategory', 's2', GST5), effective: effective(GST5, 'Category', 'Beverages'), overrides: [] },
      ],
      overrides: [],
    }],
    uncategorisedOverrideCount: 0, uncategorisedOverrides: [],
  };
}

function render(canManage = true) {
  const service = {
    tree: vi.fn(() => of(tree())),
    listProfiles: vi.fn(() => of([{ ...GST5, countryId: 'in' }, { ...GST18, countryId: 'in' }] as unknown as TaxProfile[])),
    upsertAssignment: vi.fn(() => of({})),
    deleteAssignment: vi.fn(() => of(undefined)),
    clearRedundantOverrides: vi.fn(() => of({ cleared: 3 })),
  };
  const confirm = { ask: vi.fn(() => Promise.resolve(true)) };
  const toast = { show: vi.fn() };
  TestBed.configureTestingModule({
    imports: [MappingTab],
    providers: [
      { provide: TaxSetupService, useValue: service },
      { provide: ConfirmDialogService, useValue: confirm },
      { provide: ToastService, useValue: toast },
    ],
  });
  const fixture = TestBed.createComponent(MappingTab);
  fixture.componentRef.setInput('countries', [{ id: 'in', code: 'IN', name: 'India' }]);
  fixture.componentRef.setInput('canManage', canManage);
  fixture.detectChanges();
  return { fixture, el: fixture.nativeElement as HTMLElement, service, confirm, toast };
}
const chips = (el: HTMLElement) => Array.from(el.querySelectorAll('[data-testid="source-chip"]')).map((c) => c.textContent?.trim());

describe('MappingTab', () => {
  it('loads the tree without overrides first and selects the returned country', () => {
    const { fixture, service } = render();
    expect(service.tree).toHaveBeenCalledWith(null, false);
    expect(fixture.componentInstance.countryId()).toBe('in');
  });

  it('renders company default, categories and (once expanded) subcategories with source chips', () => {
    const { fixture, el } = render();
    expect(chips(el)).toEqual(['Set here', 'Set here']); // company default, category
    fixture.componentInstance.toggle('cc1');
    fixture.detectChanges();
    expect(chips(el)).toEqual(['Set here', 'Set here', 'From category', 'Set here']);
    expect(el.textContent).toContain('Soft Drinks');
    expect(el.textContent).toContain('HSN 2202');
  });

  it('loads the product overrides only when a node asks for them', () => {
    const { fixture, el, service } = render();
    fixture.componentInstance.toggle('cc1');
    fixture.detectChanges();
    (el.querySelector('[data-testid="show-overrides"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(service.tree).toHaveBeenLastCalledWith('in', true);
    expect(chips(el)).toContain('Product override');
    expect(el.textContent).toContain('Cola');
  });

  it('choosing a subcategory profile upserts it, keeping the purchase profile', () => {
    const { fixture, service } = render();
    const row = fixture.componentInstance.subRow(tree().categories[0].subcategories[1]);
    fixture.componentInstance.onSales(row, 'gst18');
    expect(service.upsertAssignment).toHaveBeenCalledWith({ scopeType: 'Subcategory', scopeId: 's2', countryId: 'in', salesProfileId: 'gst18', purchaseProfileId: null });
  });

  it('"Inherit" on a set row deletes the assignment; a product override can be removed', () => {
    const { fixture, service } = render();
    fixture.componentInstance.onSales(fixture.componentInstance.categoryRow(tree().categories[0]), '');
    expect(service.deleteAssignment).toHaveBeenCalledWith('a-c1');
    fixture.componentInstance.removeOverride(assignment('a-p1', 'Product', 'p1', GST5));
    expect(service.deleteAssignment).toHaveBeenCalledWith('a-p1');
  });

  it('"Clear overrides equal to the inherited profile" confirms, posts the country, toasts the count and reloads', async () => {
    const { fixture, service, confirm, toast } = render();
    await fixture.componentInstance.clearRedundant();
    expect(confirm.ask).toHaveBeenCalledWith(expect.objectContaining({ title: expect.stringContaining('Clear overrides') }));
    expect(service.clearRedundantOverrides).toHaveBeenCalledWith('in');
    expect(toast.show).toHaveBeenCalledWith('3 overrides cleared.');
    expect(service.tree).toHaveBeenCalledTimes(2);
  });

  it('does nothing when the confirmation is declined', async () => {
    const { fixture, service, confirm } = render();
    confirm.ask.mockResolvedValueOnce(false);
    await fixture.componentInstance.clearRedundant();
    expect(service.clearRedundantOverrides).not.toHaveBeenCalled();
  });

  it('hides all write controls without Tax.Manage', () => {
    const { el } = render(false);
    expect(el.querySelectorAll('select.field-input').length).toBe(1); // the country filter only
    expect(el.textContent).not.toContain('Clear overrides equal');
  });
});

describe('mapping logic', () => {
  it('names the source of an inherited profile', () => {
    expect(sourceChip('Subcategory', null, effective(GST18, 'Company', 'Company')).text).toBe('From company default');
    expect(sourceChip('Subcategory', null, effective(GST18, 'Category', 'Beverages')).text).toBe('From category');
    expect(sourceChip('Product', assignment('x', 'Product', 'p', GST5), null)).toEqual({ text: 'Product override', tone: 'override' });
    expect(sourceChip('Category', null, null).text).toBe('Not mapped');
  });

  it('summarises the effective profiles', () => {
    expect(effectiveText(assignment('x', 'Category', 'c', GST5), null)).toBe('Sales GST 5%');
    expect(effectiveText(null, { ...effective(GST18, 'Company', 'Company'), purchaseProfile: GST5 })).toBe('Sales GST 18% · Purchase GST 5%');
    expect(effectiveText(null, null)).toBe('No profile applies');
  });
});
