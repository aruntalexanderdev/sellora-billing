import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import type { Branch, UpdateBranchRequest } from '../branches.models';
import { BranchesService } from '../branches.service';
import { BranchForm } from './branch-form';

const branch = (o: Partial<Branch> = {}): Branch => ({
  id: 'b1', code: 'NTH', name: 'North', addressLine: 'A', city: null, state: null, phone: null, email: null, gstin: '27ABC',
  invoicePrefix: 'NT', effectiveInvoicePrefix: 'NT', isDefault: false, isActive: true, countryId: null, countryName: null, stateId: null, stateName: null, ...o,
});

function setup(id: string | null = null, loaded: Branch = branch()) {
  const service = { get: vi.fn(() => of(loaded)), create: vi.fn(() => of(branch())), update: vi.fn((_id: string, _r: UpdateBranchRequest) => of(branch())) };
  const router = { navigateByUrl: vi.fn() };
  TestBed.configureTestingModule({
    imports: [BranchForm],
    providers: [
      { provide: BranchesService, useValue: service },
      { provide: Router, useValue: router },
      { provide: ToastService, useValue: { show: vi.fn() } },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap(id ? { id } : {}) } } },
    ],
  });
  const component = TestBed.createComponent(BranchForm).componentInstance;
  return { component, service, router, c: component.form.controls };
}

describe('BranchForm', () => {
  it('maps blank optional fields to null and posts the create request', () => {
    const { component, service, router, c } = setup();
    c.code.setValue('NTH');
    c.name.setValue(' North ');
    component.submit();
    expect(service.create).toHaveBeenCalledWith({
      code: 'NTH', name: 'North', addressLine: null, city: null, phone: null, email: null, gstin: null, invoicePrefix: null, countryId: null, stateId: null,
    });
    expect(router.navigateByUrl).toHaveBeenCalledWith('/company/branches');
  });

  it('validates code and invoice prefix client-side', () => {
    const { component, service, c } = setup();
    c.code.setValue('TOOLONG1');
    c.name.setValue('X');
    c.invoicePrefix.setValue('-AB');
    component.submit();
    expect(c.code.errors?.['pattern']).toBeTruthy();
    expect(c.invoicePrefix.errors?.['pattern']).toBeTruthy();
    expect(service.create).not.toHaveBeenCalled();
  });

  it('edit: code is locked, update omits the code', () => {
    const { component, service, c } = setup('b1');
    component.ngOnInit();
    expect(c.code.disabled).toBe(true);
    component.submit();
    expect(service.update).toHaveBeenCalledWith('b1', expect.not.objectContaining({ code: expect.anything() }));
    expect(service.update.mock.calls[0][1]).toMatchObject({ name: 'North', gstin: '27ABC', invoicePrefix: 'NT' });
  });

  it('default branch: prefix disabled and sent as null', () => {
    const { component, service, c } = setup('b1', branch({ isDefault: true, invoicePrefix: null }));
    component.ngOnInit();
    expect(c.invoicePrefix.disabled).toBe(true);
    component.submit();
    expect(service.update.mock.calls[0][1].invoicePrefix).toBeNull();
  });

  it('maps server field errors (PascalCase) onto controls', () => {
    const { component, service, c } = setup();
    service.create.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { InvoicePrefix: ['Another branch already uses this invoice prefix.'], Code: ['A branch with this code already exists.'] } } })),
    );
    c.code.setValue('NTH');
    c.name.setValue('North');
    component.submit();
    expect(c.invoicePrefix.errors?.['server']).toContain('already uses');
    expect(c.code.errors?.['server']).toContain('already exists');
    expect(component.serverError()).toBeNull();
    expect(component.saving()).toBe(false);
  });

  it('posts countryId/stateId and never the legacy state text (D6)', () => {
    const { component, service, c } = setup();
    c.code.setValue('NTH');
    c.name.setValue('North');
    c.countryId.setValue('in');
    c.stateId.setValue('kl');
    component.submit();
    const body = service.create.mock.calls[0] as unknown[];
    expect(body[0]).toMatchObject({ countryId: 'in', stateId: 'kl' });
    expect('state' in (body[0] as object)).toBe(false);
  });

  it('a state without a country is never posted', () => {
    const { component, service, c } = setup();
    c.code.setValue('NTH');
    c.name.setValue('North');
    c.stateId.setValue('kl');
    component.submit();
    expect((service.create.mock.calls[0] as unknown[])[0]).toMatchObject({ countryId: null, stateId: null });
  });

  it('edit loads the stored links and the legacy hint only while stateId is null', () => {
    const linked = setup('b1', branch({ countryId: 'in', countryName: 'India', stateId: 'kl', stateName: 'Kerala', state: 'Kerla' }));
    linked.component.ngOnInit();
    expect(linked.c.countryId.value).toBe('in');
    expect(linked.c.stateId.value).toBe('kl');
    expect(linked.component.legacyState()).toBeNull();
    expect(linked.component.storedState()).toEqual({ id: 'kl', name: 'Kerala' });
    linked.component.submit();
    expect(linked.service.update.mock.calls[0][1]).toMatchObject({ countryId: 'in', stateId: 'kl' });
    TestBed.resetTestingModule();
    const unlinked = setup('b1', branch({ state: 'Kerla' }));
    unlinked.component.ngOnInit();
    expect(unlinked.component.legacyState()).toBe('Kerla');
    unlinked.component.submit();
    expect(unlinked.service.update.mock.calls[0][1]).toMatchObject({ countryId: null, stateId: null });
  });

  it('maps server errors on stateId onto the control', () => {
    const { component, service, c } = setup();
    service.create.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { StateId: ['State does not belong to the country.'] } } })));
    c.code.setValue('NTH');
    c.name.setValue('North');
    component.submit();
    expect(c.stateId.errors?.['server']).toContain('does not belong');
  });
});
