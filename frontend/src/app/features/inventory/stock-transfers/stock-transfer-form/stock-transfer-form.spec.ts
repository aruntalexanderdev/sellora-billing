import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../../shared/scope/scope-testing';
import { ProductsService } from '../../products/products.service';
import { StockTransfersService } from '../stock-transfers.service';
import { StockTransferForm } from './stock-transfer-form';
import { line, transfer } from '../test-fixtures';

const unit = (id: string, unitCode: string, factor: number, dp: number, isBaseUnit = false) => ({
  id, unitCode, displayName: unitCode, decimalPlaces: dp, conversionFactorToBase: factor, isBaseUnit, isActive: true,
});

function setup(options: { id?: string; query?: Record<string, string>; service?: Record<string, unknown> } = {}) {
  const service = { get: vi.fn(() => of(transfer('Draft', [line({ productUnitId: 'box', requestedQty: 5 })]))), create: vi.fn(() => of(transfer('Draft'))), update: vi.fn(() => of(transfer('Draft'))), submit: vi.fn(() => of(transfer('Requested'))), ...options.service };
  TestBed.configureTestingModule({
    imports: [StockTransferForm],
    providers: [
      provideRouter([]),
      ...provideScopeStubs(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap(options.id ? { id: options.id } : {}), queryParamMap: convertToParamMap(options.query ?? {}) } } },
      { provide: StockTransfersService, useValue: service },
      {
        provide: ProductsService,
        useValue: {
          list: () => of({ items: [{ id: 'pen', name: 'Pen', sku: 'PEN-1', isActive: true }], total: 1, page: 1, pageSize: 100 }),
          get: () => of({ id: 'pen', units: [unit('pc', 'pc', 1, 0, true), unit('box', 'box', 10, 0)] }),
        },
      },
      { provide: AuthService, useValue: { hasPermission: () => true } },
    ],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(StockTransferForm);
  fixture.detectChanges();
  const component = fixture.componentInstance;
  const fill = (over: { qty?: number | null; source?: string; destination?: string; submitNow?: boolean } = {}) => {
    component.form.patchValue({ sourceLocationId: over.source ?? 'store', destinationLocationId: over.destination ?? 'van', submitNow: over.submitNow ?? false, notes: ' hello ' });
    const row = component.form.controls.lines.at(0);
    row.patchValue({ productId: 'pen', productUnitId: 'box', quantity: over.qty === undefined ? 2 : over.qty });
    fixture.detectChanges();
  };
  return { fixture, component, el: fixture.nativeElement as HTMLElement, service, navigate, fill };
}

const http = (status: number, errors?: Record<string, string[]>, extra: object = {}) => new HttpErrorResponse({ status, error: errors ? { errors, ...extra } : extra });

describe('StockTransferForm (create)', () => {
  it('prefills the endpoints from query params', () => {
    const { component } = setup({ query: { sourceLocationId: 'store', destinationLocationId: 'van' } });
    expect(component.form.controls.sourceLocationId.value).toBe('store');
    expect(component.form.controls.destinationLocationId.value).toBe('van');
  });

  it('creates a draft with trimmed notes, an Idempotency-Key and navigates to the detail', () => {
    const { component, service, navigate, fill } = setup();
    fill();
    component.submit();
    expect(service.create).toHaveBeenCalledWith(
      { sourceLocationId: 'store', destinationLocationId: 'van', notes: 'hello', submit: false, lines: [{ productId: 'pen', productUnitId: 'box', quantity: 2 }] },
      expect.any(String),
    );
    expect(navigate).toHaveBeenCalledWith(['/inventory/stock-transfers', 't1']);
  });

  it('"submit now" sends submit: true', () => {
    const { component, service, fill } = setup();
    fill({ submitNow: true });
    component.submit();
    expect((service.create.mock.calls[0] as unknown as [{ submit: boolean }])[0].submit).toBe(true);
  });

  it('refuses identical source and destination, and a fractional quantity of a whole-number unit', () => {
    const { component, service, fill } = setup();
    fill({ source: 'store', destination: 'store' });
    component.submit();
    expect(component.locationError('destinationLocationId')).toMatch(/different/i);

    fill({ qty: 1.5 });
    component.submit();
    expect(component.form.controls.lines.at(0).controls.quantity.errors?.['maxDecimalPlaces']).toBeTruthy();
    expect(service.create).not.toHaveBeenCalled();
  });

  it('maps field errors onto the endpoint and line controls and everything else onto the banner', () => {
    const create = vi.fn(() => throwError(() => http(400, {
      'lines[0].quantity': ['Not enough stock.'], destinationLocationId: ['Destination is inactive.'], status: ['Odd status.'], IdempotencyKey: ['Bad key.'],
    })));
    const { component, el, fill, fixture } = setup({ service: { create } });
    fill();
    component.submit();
    fixture.detectChanges();
    expect(component.form.controls.lines.at(0).controls.quantity.errors?.['server']).toBe('Not enough stock.');
    expect(component.locationError('destinationLocationId')).toBe('Destination is inactive.');
    expect(component.serverError()).toContain('Odd status.');
    expect(el.querySelector('[data-testid="destination-error"]')!.textContent).toContain('inactive');
    expect(el.querySelector('[data-testid="line-qty-error-0"]')!.textContent).toContain('Not enough stock.');
  });

  it('keeps the Idempotency-Key when retrying after a network failure and uses a new one after a rejection and a success', () => {
    const keys: string[] = [];
    const outcomes = [() => throwError(() => http(0)), () => throwError(() => http(400, { sourceLocationId: ['Bad.'] })), () => of(transfer('Draft'))];
    const create = vi.fn((_r: unknown, key: string) => {
      keys.push(key);
      return outcomes[keys.length - 1]();
    });
    const { component, fill } = setup({ service: { create } });
    fill();
    component.submit();
    component.submit();
    expect(keys[1]).toBe(keys[0]);
    // The 400 was definitive: the user changes something and sends a new submission.
    component.submit();
    expect(keys[2]).not.toBe(keys[1]);
  });

  it('is not dirty-blocked after saving', () => {
    const { component, fill } = setup();
    fill();
    component.form.markAsDirty();
    expect(component.hasUnsavedChanges()).toBe(true);
    component.submit();
    expect(component.hasUnsavedChanges()).toBe(false);
  });
});

describe('StockTransferForm (edit draft)', () => {
  it('loads the draft and saves it with PUT (never POST)', () => {
    const { component, service, navigate } = setup({ id: 't1' });
    expect(component.editingId()).toBe('t1');
    expect(component.form.controls.lines.at(0).controls.productId.value).toBe('pen');
    expect(component.form.controls.sourceLocationId.value).toBe('store');
    component.submit();
    expect(service.update).toHaveBeenCalledWith('t1', expect.objectContaining({ sourceLocationId: 'store', destinationLocationId: 'van', lines: [{ productId: 'pen', productUnitId: 'box', quantity: 5 }] }));
    expect(service.create).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalled();
  });

  it('"submit now" saves with PUT and then submits the draft', () => {
    const { component, service } = setup({ id: 't1' });
    component.form.controls.submitNow.setValue(true);
    component.submit();
    expect(service.update).toHaveBeenCalled();
    expect(service.submit).toHaveBeenCalledWith('t1');
  });

  it('refuses to edit a transfer that is no longer a draft', () => {
    const { component } = setup({ id: 't1', service: { get: () => of(transfer('Requested')) } });
    expect(component.loadError()).toMatch(/only a draft/i);
  });
});
