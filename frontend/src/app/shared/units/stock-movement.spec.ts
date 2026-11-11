import { STOCK_MOVEMENT_TYPES, enteredLabel, movementTone, movementTypeLabel } from './stock-movement';

describe('stock-movement helpers', () => {
  it('includes the Phase 6a types', () => {
    expect(STOCK_MOVEMENT_TYPES).toEqual(expect.arrayContaining(['PurchaseCancellation', 'OpeningStock', 'SaleCancellation', 'SaleReturn', 'PurchaseReturn', 'Adjustment']));
  });

  it('maps tones', () => {
    for (const t of ['Purchase', 'SaleReturn', 'SaleCancellation', 'OpeningStock'] as const) expect(movementTone(t)).toBe('good');
    for (const t of ['Sale', 'PurchaseReturn'] as const) expect(movementTone(t)).toBe('info');
    for (const t of ['PurchaseCancellation', 'Adjustment'] as const) expect(movementTone(t)).toBe('warn');
  });

  it('labels adjustments with their reason', () => {
    expect(movementTypeLabel('Adjustment', 'Damage')).toBe('Adjustment - Damage');
    expect(movementTypeLabel('Adjustment', null)).toBe('Adjustment');
    expect(movementTypeLabel('OpeningStock', 'OpeningStock')).toBe('Opening stock');
    expect(movementTypeLabel('PurchaseCancellation')).toBe('Purchase cancellation');
  });

  it('builds the entered label', () => {
    expect(enteredLabel({})).toBeNull();
    expect(enteredLabel({ enteredQuantity: 2, enteredUnitCode: 'BOX', conversionFactorSnapshot: 10, enteredDecimalPlaces: 0 })).toBe('2 BOX (x10)');
    expect(enteredLabel({ enteredQuantity: -1.5, enteredUnitCode: 'KG', conversionFactorSnapshot: 1, enteredDecimalPlaces: 3 })).toBe('1.500 KG');
    expect(enteredLabel({ enteredQuantity: null, enteredUnitCode: 'BOX', conversionFactorSnapshot: 10 })).toBe('in BOX');
  });

  it('covers transfer movements', () => {
    expect(STOCK_MOVEMENT_TYPES).toEqual(expect.arrayContaining(['TransferOut', 'TransferIn']));
    expect(movementTypeLabel('TransferOut')).toBe('Transfer out');
    expect(movementTypeLabel('TransferIn')).toBe('Transfer in');
    expect(movementTone('TransferOut')).toBe('info');
    expect(movementTone('TransferIn')).toBe('good');
  });
});
