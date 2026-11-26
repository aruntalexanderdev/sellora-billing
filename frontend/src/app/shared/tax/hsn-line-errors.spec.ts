import { FormArray, FormControl, FormGroup } from '@angular/forms';
import { applyPurchaseProblem } from '../../features/purchases/purchase-tax';
import { applySaleProblem } from '../../features/sales/pos/pos-tax';
import { hsnLineIndex, hsnLineMessage, problemMessagesLabelled } from './hsn';
import { savedToast } from './save-warnings';

const line = () => new FormGroup({ productId: new FormControl(''), qty: new FormControl(1) });
const form = () => new FormGroup({ customerId: new FormControl(''), billDiscountPercent: new FormControl(null), billDiscountAmount: new FormControl(null), billCharges: new FormControl(0), lines: new FormArray([line(), line()]) });

describe('lines[i].hsnCode errors (T11)', () => {
  it('recognises the key case-insensitively', () => {
    expect(hsnLineIndex('lines[2].hsnCode')).toBe(2);
    expect(hsnLineIndex('Lines[0].HsnCode')).toBe(0);
    expect(hsnLineIndex('lines[0].qty')).toBeNull();
  });

  it('labels the item and maps request index back to the row', () => {
    expect(hsnLineMessage('lines[1].hsnCode', 'No HSN.')).toBe('Item 2: No HSN.');
    expect(hsnLineMessage('lines[1].hsnCode', 'No HSN.', [0, 3])).toBe('Item 4: No HSN.');
    expect(problemMessagesLabelled({ 'lines[0].hsnCode': ['A'], billCharges: ['B'] }, [2])).toEqual(['Item 3: A', 'B']);
  });

  it('POS: puts it on the cart row product control and in the banner', () => {
    const f = form();
    const banner = applySaleProblem(f, { status: 400, errors: { 'lines[1].hsnCode': ['Add an HSN code.'] } } as never);
    expect(f.get('lines.1.productId')?.errors?.['server']).toBe('Add an HSN code.');
    expect(f.get('lines.0.productId')?.errors).toBeNull();
    expect(banner).toBe('Item 2: Add an HSN code.');
  });

  it('purchase: puts it on the line product control and in the banner', () => {
    const f = form();
    const banner = applyPurchaseProblem(f, { errors: { 'lines[0].hsnCode': ['Add an HSN code.'] } }, 'percent');
    expect(f.get('lines.0.productId')?.errors?.['server']).toBe('Add an HSN code.');
    expect(banner).toBe('Item 1: Add an HSN code.');
  });
});

describe('savedToast', () => {
  it('keeps the base text and duration without warnings', () => {
    expect(savedToast('Saved.', null)).toEqual({ text: 'Saved.', durationMs: 3200 });
    expect(savedToast('Saved.', [])).toEqual({ text: 'Saved.', durationMs: 3200 });
  });
  it('appends the warnings and stays longer', () => {
    const t = savedToast('Saved.', [{ field: 'hsnCode', message: '3 products have no HSN.' }]);
    expect(t.text).toBe('Saved. 3 products have no HSN.');
    expect(t.durationMs).toBeGreaterThan(3200);
  });
});
