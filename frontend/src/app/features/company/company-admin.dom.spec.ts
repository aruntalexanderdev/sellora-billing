import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Type, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { ContextService } from '../../core/context/context.service';
import { ConfirmDialogService } from '../../shared/ui/confirm-dialog/confirm-dialog.service';
import { ToastService } from '../../shared/ui/toast/toast.service';
import { BranchList } from './branches/branch-list/branch-list';
import { BranchesService } from './branches/branches.service';
import { StockLocationList } from './stock-locations/stock-location-list/stock-location-list';
import { StockLocationsService } from '../../shared/locations/stock-locations.service';
import { VehicleForm } from './vehicles/vehicle-form/vehicle-form';
import { VehicleList } from './vehicles/vehicle-list/vehicle-list';
import { VehiclesService } from './vehicles/vehicles.service';
import { WarehouseList } from './warehouses/warehouse-list/warehouse-list';
import { WarehousesService } from './warehouses/warehouses.service';

const http500 = () => throwError(() => new HttpErrorResponse({ status: 500 }));
const refusal = (errors: Record<string, string[]>) => throwError(() => new HttpErrorResponse({ status: 400, error: { status: 400, errors } }));

interface ListCase {
  name: string;
  component: Type<unknown>;
  service: Type<unknown>;
  row: Record<string, unknown>;
  create: string;
  edit: string;
  disable: string;
  refusal: string;
  rowText: string;
}

const CASES: ListCase[] = [
  {
    name: 'BranchList', component: BranchList, service: BranchesService, create: 'Company.Branches.Create', edit: 'Company.Branches.Edit', disable: 'Company.Branches.Disable',
    row: { id: 'b1', code: 'NTH', name: 'North Branch', isDefault: false, isActive: true, effectiveInvoicePrefix: 'INV-NTH' },
    refusal: 'This branch still holds stock or has open transfers.', rowText: 'North Branch',
  },
  {
    name: 'WarehouseList', component: WarehouseList, service: WarehousesService, create: 'Company.Warehouses.Create', edit: 'Company.Warehouses.Edit', disable: 'Company.Warehouses.Disable',
    row: { id: 'w1', code: 'WH1', name: 'Main WH', ownership: 'Company', branchId: null, isActive: true },
    refusal: 'This warehouse still holds stock or has open transfers.', rowText: 'Main WH',
  },
  {
    name: 'VehicleList', component: VehicleList, service: VehiclesService, create: 'Company.Vehicles.Create', edit: 'Company.Vehicles.Edit', disable: 'Company.Vehicles.Disable',
    row: { id: 'v1', vehicleNumber: 'KA-01-1234', vehicleName: 'Van 1', vehicleType: 'Van', assignedBranchId: null, isActive: true },
    refusal: 'This vehicle still holds stock or has open transfers.', rowText: 'KA-01-1234',
  },
];

/** Clicks Deactivate on the row, types the reason into the prompt and confirms. */
async function submitReason(fixture: ComponentFixture<unknown>, el: HTMLElement, reason: string): Promise<void> {
  if (!el.querySelector('app-reason-prompt')) {
    Array.from(el.querySelectorAll<HTMLButtonElement>('.dt-cell-action')).find((b) => b.textContent?.trim() === 'Deactivate')!.click();
    fixture.detectChanges();
  }
  const input = el.querySelector<HTMLTextAreaElement>('app-reason-prompt textarea')!;
  input.value = reason;
  input.dispatchEvent(new Event('input'));
  fixture.detectChanges();
  Array.from(el.querySelectorAll<HTMLButtonElement>('app-reason-prompt button')).find((b) => b.textContent?.trim() === 'Deactivate')!.click();
  await tick();
  fixture.detectChanges();
}

function mount(c: ListCase, perms: string[], list: () => Observable<unknown[]>) {
  const service = { list: vi.fn(list), setStatus: vi.fn(() => of({})) };
  const ask = vi.fn(() => Promise.resolve(true));
  TestBed.configureTestingModule({
    imports: [c.component],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: c.service, useValue: service },
      {
        provide: ContextService,
        useValue: { hasPermission: (p: string) => perms.includes(p), contextVersion: signal(0).asReadonly(), company: signal(null) },
      },
      { provide: ConfirmDialogService, useValue: { ask } },
      { provide: ToastService, useValue: { show: vi.fn() } },
      { provide: Router, useValue: { navigate: vi.fn() } },
    ],
  });
  const fixture = TestBed.createComponent(c.component);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  return { fixture, el, service, ask };
}

const buttons = (el: HTMLElement) => Array.from(el.querySelectorAll('button')).map((b) => b.textContent?.trim());
const tick = () => new Promise<void>((r) => setTimeout(r, 0));

describe.each(CASES)('$name DOM', (c) => {
  it('shows the loading skeleton while the list is pending', () => {
    const { el } = mount(c, [], () => new Observable<unknown[]>(() => undefined));
    expect(el.querySelector('.dt-loading')).not.toBeNull();
    expect(el.querySelector('table')).toBeNull();
  });

  it('shows the empty state with no rows', () => {
    const { el } = mount(c, [], () => of([]));
    expect(el.querySelector('.dt-state-title')?.textContent).toMatch(/^No /);
    expect(el.querySelector('table')).toBeNull();
  });

  it('shows the error state with a Retry that reloads the list', () => {
    const { fixture, el, service } = mount(c, [], http500);
    expect(el.querySelector('.dt-state-title')?.textContent).toContain('Could not load this list');
    service.list.mockImplementation(() => of([c.row]));
    el.querySelector<HTMLButtonElement>('.dt-primary-btn')!.click();
    fixture.detectChanges();
    expect(service.list).toHaveBeenCalledTimes(2);
    expect(el.querySelector('table')?.textContent).toContain(c.rowText);
  });

  it('hides create / edit / deactivate without permission', () => {
    const { el } = mount(c, [], () => of([c.row]));
    expect(el.querySelector('table')?.textContent).toContain(c.rowText);
    const labels = buttons(el);
    expect(labels.some((l) => l?.startsWith('+ New'))).toBe(false);
    expect(labels).not.toContain('Edit');
    expect(labels).not.toContain('Deactivate');
  });

  it('shows create / edit / deactivate with the matching permissions', () => {
    const { el } = mount(c, [c.create, c.edit, c.disable], () => of([c.row]));
    const labels = buttons(el);
    expect(labels.some((l) => l?.startsWith('+ New'))).toBe(true);
    expect(labels).toContain('Edit');
    expect(labels).toContain('Deactivate');
  });

  it('renders the server IsActive refusal in the notice banner (verbatim, no generic text) and can dismiss it', async () => {
    const { fixture, el, service } = mount(c, [c.disable], () => of([c.row]));
    service.setStatus.mockImplementationOnce(() => refusal({ IsActive: [c.refusal] }));
    await submitReason(fixture, el, 'Closing down');
    const notice = el.querySelector('[data-testid="notice"]');
    expect(notice?.textContent).toContain(c.refusal);
    expect(notice?.textContent).not.toContain('Could not');
    notice!.querySelector<HTMLButtonElement>('.dismiss')!.click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="notice"]')).toBeNull();
  });

  it('D14: deactivate opens the reason prompt, requires a reason, and posts it', async () => {
    const { fixture, el, service } = mount(c, [c.disable], () => of([c.row]));
    Array.from(el.querySelectorAll<HTMLButtonElement>('.dt-cell-action')).find((b) => b.textContent?.trim() === 'Deactivate')!.click();
    fixture.detectChanges();
    expect(el.querySelector('app-reason-prompt')).not.toBeNull();
    const confirmBtn = () => Array.from(el.querySelectorAll<HTMLButtonElement>('app-reason-prompt button')).find((b) => b.textContent?.trim() === 'Deactivate')!;
    confirmBtn().click();
    fixture.detectChanges();
    expect(service.setStatus).not.toHaveBeenCalled(); // empty reason is blocked client-side
    await submitReason(fixture, el, '  Moved premises ');
    expect(service.setStatus).toHaveBeenCalledWith(expect.any(String), false, 'Moved premises');
    expect(el.querySelector('app-reason-prompt')).toBeNull();
  });

  it('D14: a 400 on `reason` stays in the prompt; cancelling never calls the API', async () => {
    const { fixture, el, service } = mount(c, [c.disable], () => of([c.row]));
    service.setStatus.mockImplementationOnce(() => refusal({ reason: ['Reason must be 1-500 characters.'] }));
    await submitReason(fixture, el, 'x');
    expect(el.querySelector('app-reason-prompt .server-error')?.textContent).toContain('Reason must be 1-500 characters.');
    expect(el.querySelector('[data-testid="notice"]')).toBeNull();
    Array.from(el.querySelectorAll<HTMLButtonElement>('app-reason-prompt button')).find((b) => b.textContent?.trim() === 'Cancel')!.click();
    fixture.detectChanges();
    expect(el.querySelector('app-reason-prompt')).toBeNull();
    expect(service.setStatus).toHaveBeenCalledTimes(1);
  });
});

describe('StockLocationList DOM (read-only)', () => {
  const loc = { id: 'l1', code: 'MAIN-S', name: 'Main Store', locationType: 'BranchStore', branchId: 'b1', branchCode: 'MAIN', branchName: 'Main', isActive: true, canSell: true, canReceivePurchase: true, isSystemManaged: true };

  function mountLocations(list: () => Observable<unknown[]>) {
    const service = { list: vi.fn(list) };
    TestBed.configureTestingModule({
      imports: [StockLocationList],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: StockLocationsService, useValue: service },
        { provide: ContextService, useValue: { hasPermission: () => true, contextVersion: signal(0).asReadonly(), company: signal(null) } },
      ],
    });
    const fixture = TestBed.createComponent(StockLocationList);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders rows without any create / edit / status action even for a user holding every permission', () => {
    const el = mountLocations(() => of([loc]));
    expect(el.querySelector('table')?.textContent).toContain('Main Store');
    expect(Array.from(el.querySelectorAll('button')).map((b) => b.textContent?.trim())).not.toEqual(
      expect.arrayContaining(['Edit']),
    );
    expect(el.querySelector('.dt-cell-action')).toBeNull();
    expect(el.textContent).not.toContain('+ New');
  });

  it('shows empty and error states', () => {
    expect(mountLocations(() => of([])).querySelector('.dt-state-title')?.textContent).toContain('No stock locations');
    TestBed.resetTestingModule();
    expect(mountLocations(http500).querySelector('.dt-state-title')?.textContent).toContain('Could not load this list');
  });
});

describe('VehicleForm DOM server errors', () => {
  function mountForm() {
    const service = { get: vi.fn(), create: vi.fn(() => of({})), update: vi.fn(() => of({})) };
    TestBed.configureTestingModule({
      imports: [VehicleForm],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: VehiclesService, useValue: service },
        { provide: BranchesService, useValue: { accessible: () => of([{ id: 'b1', code: 'NTH', name: 'North' }]) } },
        { provide: Router, useValue: { navigateByUrl: vi.fn() } },
        { provide: ToastService, useValue: { show: vi.fn() } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({}) } } },
      ],
    });
    const fixture = TestBed.createComponent(VehicleForm);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const fill = (id: string, value: string) => {
      const input = el.querySelector<HTMLInputElement>(`#${id}`)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    };
    fill('v-number', 'KA-1');
    fill('v-name', 'Van');
    return { fixture, el, service };
  }

  it('renders errors.AssignedBranchId under the assigned-branch control, not in the banner', () => {
    const { fixture, el, service } = mountForm();
    const msg = 'This vehicle still holds stock or has open transfers. Transfer, return or reconcile its stock before reassigning it.';
    service.create.mockReturnValueOnce(refusal({ AssignedBranchId: [msg] }));
    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="reassign-error"]')?.textContent).toContain(msg);
    expect(el.querySelector('[data-testid="server-error"]')).toBeNull();
    expect(el.querySelector('#v-branch')!.parentElement!.textContent).toContain(msg);
  });

  it('puts an unmapped server message in the banner', () => {
    const { fixture, el, service } = mountForm();
    service.create.mockReturnValueOnce(refusal({ Something: ['Unmapped problem.'] }));
    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="server-error"]')?.textContent).toContain('Unmapped problem.');
  });

  it('shows required-field messages and does not call the API when the name is blank', () => {
    const { fixture, el, service } = mountForm();
    const name = el.querySelector<HTMLInputElement>('#v-name')!;
    name.value = '';
    name.dispatchEvent(new Event('input'));
    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    expect(service.create).not.toHaveBeenCalled();
    expect(el.textContent).toContain('Name is required.');
  });
});
