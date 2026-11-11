import { HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { authInterceptor } from '../../core/auth/auth.interceptor';
import { contextInterceptor } from '../../core/http/context.interceptor';
import { correlationInterceptor } from '../../core/http/correlation.interceptor';
import { errorInterceptor } from '../../core/http/error.interceptor';
import { PLATFORM_STORAGE_KEY, PlatformAuthService } from '../../core/platform/platform-auth.service';
import { platformInterceptor } from '../../core/platform/platform.interceptor';
import { ToastService } from '../../shared/ui/toast/toast.service';
import type { CurrencyItem, PlatformCompany, UnitItem } from './platform.models';
import { PlatformCompanies } from './platform-companies';
import { PlatformCompanyForm } from './platform-company-form';
import { PlatformCurrencies } from './platform-currencies';
import { PlatformLogin } from './platform-login';
import { PlatformUnits } from './platform-units';
import { PlatformService } from './platform.service';

const tick = () => new Promise<void>((r) => setTimeout(r, 0));
const problem = (status: number, body: Record<string, unknown>) => throwError(() => new HttpErrorResponse({ status, error: { status, ...body } }));
const pending = () => new Observable<never>(() => undefined);

function mountWith<T>(component: new () => T, service: Partial<Record<keyof PlatformService, unknown>>, extra: unknown[] = []) {
  const toast = { show: vi.fn() };
  TestBed.configureTestingModule({
    imports: [component],
    providers: [
      provideRouter([]),
      { provide: PlatformService, useValue: service },
      { provide: ToastService, useValue: toast },
      ...(extra as never[]),
    ],
  });
  const router = TestBed.inject(Router);
  const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(component);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const fire = (selector: string, event = 'input', value?: string) => {
    const node = el.querySelector<HTMLInputElement>(selector)!;
    expect(node, selector).toBeTruthy();
    if (value !== undefined) node.value = value;
    node.dispatchEvent(new Event(event));
    fixture.detectChanges();
  };
  const clickText = (text: string, selector = 'button') => {
    const b = Array.from(el.querySelectorAll<HTMLButtonElement>(selector)).find((x) => x.textContent?.trim() === text);
    expect(b, `button "${text}"`).toBeTruthy();
    b!.click();
    fixture.detectChanges();
  };
  const submit = (selector = 'form') => {
    el.querySelector(selector)!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  };
  return { fixture, component: fixture.componentInstance, el, toast, navigate, fire, clickText, submit };
}

// ---------------------------------------------------------------- login

describe('PlatformLogin', () => {
  const login = vi.fn();
  const mountLogin = (query: Record<string, string> = {}) => {
    login.mockReset();
    return mountWith(PlatformLogin as never, {}, [
      { provide: (PlatformAuthService), useValue: { login } },
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
    ]);
  };

  it('requires both fields and does not call the API when empty', () => {
    const { submit, el } = mountLogin();
    submit();
    expect(login).not.toHaveBeenCalled();
    expect(el.textContent).toContain('Enter your username or email.');
    expect(el.textContent).toContain('Enter your password.');
  });

  it('signs in and goes to /platform/companies', () => {
    const { submit, fire, navigate } = mountLogin();
    login.mockReturnValue(of({}));
    fire('#pl-user', 'input', 'pat');
    fire('#pl-pass', 'input', 'secret');
    submit();
    expect(login).toHaveBeenCalledWith({ usernameOrEmail: 'pat', password: 'secret' });
    expect(navigate).toHaveBeenCalledWith(['/platform/companies']);
  });

  it.each([
    ['invalid_credentials', 'Invalid username/email or password.'],
    ['account_disabled', 'Your account has been disabled. Contact an administrator.'],
    ['platform_admin_required', 'This account is not a platform administrator.'],
  ])('shows the readable message for %s', (code, text) => {
    const { submit, fire, el } = mountLogin();
    login.mockReturnValue(problem(401, { code }));
    fire('#pl-user', 'input', 'pat');
    fire('#pl-pass', 'input', 'x');
    submit();
    expect(el.querySelector('[role="alert"]')?.textContent).toContain(text);
  });

  it('?reason=expired explains that the platform session ended', () => {
    expect(mountLogin({ reason: 'expired' }).el.querySelector('[role="alert"]')?.textContent).toContain('platform session has ended');
    TestBed.resetTestingModule();
    expect(mountLogin().el.querySelector('[role="alert"]')).toBeNull();
  });
});

// ---------------------------------------------------------------- companies list

const company = (o: Partial<PlatformCompany> = {}): PlatformCompany => ({
  id: 'c1', code: 'ACME', name: 'Acme Retail', isActive: true, createdAt: '2026-01-01T00:00:00Z', memberCount: 4, ownerCount: 1, ...o,
});

describe('PlatformCompanies', () => {
  const service = () => ({ companies: vi.fn(() => of([company(), company({ id: 'c2', code: 'OLD', name: 'Old Co', isActive: false })])), setCompanyStatus: vi.fn(() => of(undefined)) });

  it('renders loading, empty and error states', () => {
    expect(mountWith(PlatformCompanies, { companies: pending }).el.querySelector('.dt-loading')).not.toBeNull();
    TestBed.resetTestingModule();
    expect(mountWith(PlatformCompanies, { companies: () => of([]) }).el.querySelector('.dt-state-title')?.textContent).toContain('No companies yet');
    TestBed.resetTestingModule();
    const failing = mountWith(PlatformCompanies, { companies: () => throwError(() => new HttpErrorResponse({ status: 500 })) });
    expect(failing.el.querySelector('.dt-state-title')?.textContent).toContain('Could not load this list');
  });

  it('shows each company with Active / Inactive status and the matching action', () => {
    const { el } = mountWith(PlatformCompanies, service());
    const rows = Array.from(el.querySelectorAll('tbody tr')).map((r) => r.textContent ?? '');
    expect(rows[0]).toContain('Acme Retail');
    expect(rows[0]).toContain('Active');
    expect(rows[0]).toContain('Deactivate');
    expect(rows[1]).toContain('Inactive');
    expect(rows[1]).toContain('Activate');
  });

  it('deactivate needs a reason: blocked when blank, then sends isActive=false with the trimmed reason', () => {
    const svc = service();
    const { el, clickText, fire, fixture } = mountWith(PlatformCompanies, svc);
    clickText('Deactivate', '.dt-cell-action');
    expect(el.querySelector('[role="group"]')?.textContent).toContain('Deactivate Acme Retail');
    clickText('Deactivate', '[role="group"] button');
    expect(svc.setCompanyStatus).not.toHaveBeenCalled();
    expect(el.textContent).toContain('A reason is required.');

    fire('#reason-input', 'input', '  Non-payment  ');
    clickText('Deactivate', '[role="group"] button');
    expect(svc.setCompanyStatus).toHaveBeenCalledWith('c1', { isActive: false, reason: 'Non-payment' });
    fixture.detectChanges();
    expect(el.querySelector('[role="group"]')).toBeNull();
    expect(svc.companies).toHaveBeenCalledTimes(2);
  });

  it('activate on an inactive company sends isActive=true with the reason', () => {
    const svc = service();
    const { el, fixture, clickText, fire } = mountWith(PlatformCompanies, svc);
    const activate = Array.from(el.querySelectorAll<HTMLButtonElement>('.dt-cell-action')).find((b) => b.textContent?.trim() === 'Activate')!;
    activate.click();
    fixture.detectChanges();
    fire('#reason-input', 'input', 'Paid up');
    clickText('Activate', '[role="group"] button');
    expect(svc.setCompanyStatus).toHaveBeenCalledWith('c2', { isActive: true, reason: 'Paid up' });
  });

  it('shows a server refusal inside the reason prompt and keeps it open', () => {
    const svc = service();
    svc.setCompanyStatus.mockReturnValueOnce(problem(400, { code: 'last_owner_protected' }) as never);
    const { el, clickText, fire } = mountWith(PlatformCompanies, svc);
    clickText('Deactivate', '.dt-cell-action');
    fire('#reason-input', 'input', 'x');
    clickText('Deactivate', '[role="group"] button');
    expect(el.querySelector('[role="group"] .server-error')?.textContent).toContain('last owner');
  });

  it('"+ New company" navigates to the create form', () => {
    const { clickText, navigate } = mountWith(PlatformCompanies, service());
    clickText('+ New company');
    expect(navigate).toHaveBeenCalledWith(['/platform/companies/new']);
  });
});

describe('PlatformCompanies 401 handling (real interceptors)', () => {
  it('a 401 platform_session_required clears the platform session and sends the operator to /platform/login', async () => {
    sessionStorage.clear();
    sessionStorage.setItem(PLATFORM_STORAGE_KEY, JSON.stringify({ accessToken: 'tok', expiresAt: new Date(Date.now() + 600_000).toISOString(), user: { id: 'p1', name: 'Pat' } }));
    TestBed.configureTestingModule({
      imports: [PlatformCompanies],
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([correlationInterceptor, platformInterceptor, authInterceptor, contextInterceptor, errorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(PlatformCompanies);
    fixture.detectChanges();
    const req = TestBed.inject(HttpTestingController).expectOne(`${environment.apiBaseUrl}/platform/companies`);
    expect(req.request.headers.get('Authorization')).toBe('Bearer tok');
    req.flush({ code: 'platform_session_required' }, { status: 401, statusText: 'Unauthorized' });
    await tick();
    fixture.detectChanges();
    expect(navigate).toHaveBeenCalledWith(['/platform/login'], expect.anything());
    expect(sessionStorage.getItem(PLATFORM_STORAGE_KEY)).toBeNull();
  });
});

// ---------------------------------------------------------------- company create

describe('PlatformCompanyForm', () => {
  const mountForm = (createCompany: () => unknown = () => of({ id: 'n', code: 'NEW', mainBranchId: 'm', ownerUserId: 'o' })) => {
    const svc = {
      activeCurrencies: vi.fn(() => of([{ id: 'cur1', code: 'INR', name: 'Rupee', symbol: '₹' }])),
      createCompany: vi.fn(createCompany),
    };
    return { svc, ...mountWith(PlatformCompanyForm, svc) };
  };
  const fillValid = (m: ReturnType<typeof mountForm>) => {
    m.component.form.patchValue({
      code: 'NEW', name: 'New Co', businessName: 'New Co Pvt', invoicePrefix: 'NC', baseCurrencyId: 'cur1',
      owner: { newUser: { firstName: 'O', lastName: 'W', username: 'owner', email: 'o@x.test', password: 'Passw0rd!23', confirmPassword: 'Passw0rd!23' } },
    });
  };

  it('lists the active currencies and blocks submit while required fields are empty', () => {
    const m = mountForm();
    expect(Array.from(m.el.querySelectorAll('#co-cur option')).map((o) => o.textContent?.trim())).toEqual(['Select a currency…', 'INR — Rupee']);
    m.submit();
    expect(m.svc.createCompany).not.toHaveBeenCalled();
    expect(m.el.textContent).toContain('Code is required.');
    expect(m.el.textContent).toContain('Choose the base currency.');
  });

  it('creates the company and returns to the list', () => {
    const m = mountForm();
    fillValid(m);
    m.submit();
    expect(m.svc.createCompany).toHaveBeenCalledWith(expect.objectContaining({ code: 'NEW', baseCurrencyId: 'cur1', owner: expect.objectContaining({ userId: null }) }));
    expect(m.navigate).toHaveBeenCalledWith(['/platform/companies']);
    expect(m.toast.show).toHaveBeenCalledWith('Company NEW created.');
  });

  it('company_creation_disabled shows the banner and disables the whole form', () => {
    const m = mountForm(() => problem(403, { code: 'company_creation_disabled' }));
    fillValid(m);
    m.submit();
    expect(m.el.querySelector('[role="alert"]')?.textContent).toContain('Company creation is disabled on this platform.');
    expect(m.el.querySelector('fieldset')!.disabled).toBe(true);
    expect(Array.from(m.el.querySelectorAll<HTMLButtonElement>('button')).find((b) => b.textContent?.includes('Create company'))!.disabled).toBe(true);
  });

  it('renders field errors (code, nested owner password) under the controls', () => {
    const m = mountForm(() => problem(400, { errors: { Code: ['Code already used.'], 'Owner.NewUser.Password': ['Password too weak.'] } }));
    fillValid(m);
    m.submit();
    expect(m.el.querySelector('#co-code')!.parentElement!.textContent).toContain('Code already used.');
    expect(m.el.querySelector('#own-pass')!.parentElement!.textContent).toContain('Password too weak.');
    expect(m.el.querySelector('[role="alert"]')).toBeNull();
  });

  it('flags a password mismatch', () => {
    const m = mountForm();
    fillValid(m);
    m.component.form.controls.owner.controls.newUser.controls.confirmPassword.setValue('different');
    m.submit();
    expect(m.svc.createCompany).not.toHaveBeenCalled();
    expect(m.el.textContent).toContain('Passwords do not match.');
  });
});

// ---------------------------------------------------------------- units CRUD

const unit = (o: Partial<UnitItem> = {}): UnitItem => ({
  id: 'u1', name: 'Kilogram', shortCode: 'kg', measurementCategory: 'Weight', decimalPlaces: 3, standardFactor: 1, isActive: true, isInUse: false, canDeactivate: true, ...o,
});

describe('PlatformUnits', () => {
  const svc = (items: UnitItem[] = [unit(), unit({ id: 'u2', name: 'Piece', shortCode: 'pc', measurementCategory: 'Count', decimalPlaces: 0, standardFactor: null, isInUse: true, canDeactivate: false })]) => ({
    units: vi.fn(() => of(items)),
    createUnit: vi.fn(() => of(unit())),
    updateUnit: vi.fn(() => of(unit())),
    setUnitActive: vi.fn(() => of(undefined)),
  });

  it('renders states, rows and edit / status actions', () => {
    expect(mountWith(PlatformUnits, { units: pending }).el.querySelector('.dt-loading')).not.toBeNull();
    TestBed.resetTestingModule();
    expect(mountWith(PlatformUnits, { units: () => of([]) }).el.querySelector('.dt-state-title')?.textContent).toContain('No units yet');
    TestBed.resetTestingModule();
    const { el } = mountWith(PlatformUnits, svc());
    const rows = Array.from(el.querySelectorAll('tbody tr')).map((r) => r.textContent ?? '');
    expect(rows[0]).toContain('Kilogram');
    expect(rows[0]).toContain('1 kg');
    expect(rows[0]).toContain('Edit');
    expect(rows[1]).toContain('Deactivate');
    const piece = el.querySelectorAll('tbody tr')[1].querySelectorAll<HTMLButtonElement>('.dt-cell-action');
    expect(piece[1].disabled).toBe(true); // used by products: cannot be deactivated
  });

  it('create: Count has no factor field and 0 decimals; Weight reveals the factor in kg', () => {
    const { el, clickText, fire, fixture } = mountWith(PlatformUnits, svc());
    clickText('+ New unit');
    expect(el.querySelector('form[aria-label="New unit"]')).not.toBeNull();
    expect(el.querySelector('#un-factor')).toBeNull();
    expect(el.querySelector<HTMLSelectElement>('#un-dec')!.disabled).toBe(true);
    fire('#un-cat', 'change', 'Weight');
    fixture.detectChanges();
    expect(el.querySelector('label[for="un-factor"]')?.textContent).toContain('(kg)');
    expect(el.querySelector<HTMLSelectElement>('#un-dec')!.disabled).toBe(false);
  });

  it('create sends the built request, toasts and reloads', () => {
    const s = svc();
    const { clickText, fire, submit, toast } = mountWith(PlatformUnits, s);
    clickText('+ New unit');
    fire('#un-name', 'input', ' Gram ');
    fire('#un-code', 'input', 'g');
    fire('#un-cat', 'change', 'Weight');
    fire('#un-factor', 'input', '0.001');
    submit('form[aria-label="New unit"]');
    expect(s.createUnit).toHaveBeenCalledWith({ name: 'Gram', shortCode: 'g', measurementCategory: 'Weight', decimalPlaces: 0, standardFactor: 0.001 });
    expect(toast.show).toHaveBeenCalledWith('Unit created.');
    expect(s.units).toHaveBeenCalledTimes(2);
  });

  it('create with blank required fields does not call the API', () => {
    const s = svc();
    const { clickText, submit } = mountWith(PlatformUnits, s);
    clickText('+ New unit');
    submit('form[aria-label="New unit"]');
    expect(s.createUnit).not.toHaveBeenCalled();
  });

  it('a server field error lands under its control; the banner stays empty', () => {
    const s = svc();
    s.createUnit.mockReturnValueOnce(problem(400, { errors: { ShortCode: ['Code already exists.'] } }) as never);
    const { el, clickText, fire, submit } = mountWith(PlatformUnits, s);
    clickText('+ New unit');
    fire('#un-name', 'input', 'Gram');
    fire('#un-code', 'input', 'g');
    submit('form[aria-label="New unit"]');
    expect(el.querySelector('#un-code')!.parentElement!.textContent).toContain('Code already exists.');
    expect(el.querySelector('form [role="alert"]')).toBeNull();
  });

  it('editing a unit used by products locks category, decimals and factor', () => {
    const { el, fixture } = mountWith(PlatformUnits, svc([unit({ isInUse: true })]));
    el.querySelector<HTMLButtonElement>('.dt-cell-action')!.click();
    fixture.detectChanges();
    expect(el.querySelector('form[aria-label="Edit unit"]')).not.toBeNull();
    expect(el.querySelector<HTMLSelectElement>('#un-cat')!.disabled).toBe(true);
    expect(el.querySelector<HTMLSelectElement>('#un-dec')!.disabled).toBe(true);
    expect(el.querySelector<HTMLInputElement>('#un-factor')!.disabled).toBe(true);
    expect(el.querySelector<HTMLInputElement>('#un-name')!.disabled).toBe(false);
  });

  it('edit saves through updateUnit; status action calls setUnitActive', () => {
    const s = svc([unit()]);
    const { el, fixture, submit, clickText } = mountWith(PlatformUnits, s);
    el.querySelector<HTMLButtonElement>('.dt-cell-action')!.click();
    fixture.detectChanges();
    submit('form[aria-label="Edit unit"]');
    expect(s.updateUnit).toHaveBeenCalledWith('u1', expect.objectContaining({ shortCode: 'kg' }));
    clickText('Deactivate', '.dt-cell-action');
    expect(s.setUnitActive).toHaveBeenCalledWith('u1', false);
  });
});

// ---------------------------------------------------------------- currencies CRUD

const cur = (o: Partial<CurrencyItem> = {}): CurrencyItem => ({
  id: 'k1', code: 'USD', name: 'US Dollar', symbol: '$', decimalPrecision: 2, isActive: true, isBase: false, ...o,
});

describe('PlatformCurrencies', () => {
  const svc = (items: CurrencyItem[] = [cur(), cur({ id: 'k2', code: 'INR', name: 'Rupee', symbol: '₹', isBase: true })]) => ({
    currencies: vi.fn(() => of(items)),
    createCurrency: vi.fn(() => of(cur())),
    updateCurrency: vi.fn(() => of(cur())),
    setCurrencyActive: vi.fn(() => of(undefined)),
  });

  it('renders states; the base currency offers Edit but no deactivate', () => {
    expect(mountWith(PlatformCurrencies, { currencies: pending }).el.querySelector('.dt-loading')).not.toBeNull();
    TestBed.resetTestingModule();
    expect(mountWith(PlatformCurrencies, { currencies: () => of([]) }).el.querySelector('.dt-state-title')?.textContent).toContain('No currencies yet');
    TestBed.resetTestingModule();
    const { el } = mountWith(PlatformCurrencies, svc());
    const [usd, inr] = Array.from(el.querySelectorAll('tbody tr')).map((r) => Array.from(r.querySelectorAll('.dt-cell-action')).map((b) => b.textContent?.trim()));
    expect(usd).toEqual(['Edit', 'Deactivate']);
    expect(inr).toEqual(['Edit']);
    expect(el.querySelectorAll('tbody tr')[1].textContent).toContain('Base');
  });

  it('create uppercases the code, sends precision and toasts', () => {
    const s = svc();
    const { clickText, fire, submit, toast } = mountWith(PlatformCurrencies, s);
    clickText('+ New currency');
    fire('#cu-code', 'input', 'eur');
    fire('#cu-name', 'input', ' Euro ');
    fire('#cu-sym', 'input', '€');
    submit('form[aria-label="New currency"]');
    expect(s.createCurrency).toHaveBeenCalledWith({ code: 'EUR', name: 'Euro', symbol: '€', decimalPrecision: 2 });
    expect(toast.show).toHaveBeenCalledWith('Currency created.');
  });

  it('rejects a code that is not 3 letters', () => {
    const s = svc();
    const { el, clickText, fire, submit } = mountWith(PlatformCurrencies, s);
    clickText('+ New currency');
    fire('#cu-code', 'input', 'E1');
    fire('#cu-name', 'input', 'Euro');
    fire('#cu-sym', 'input', '€');
    submit('form[aria-label="New currency"]');
    expect(s.createCurrency).not.toHaveBeenCalled();
    expect(el.textContent).toContain('Use a 3-letter code.');
  });

  it('shows a server field error under the control', () => {
    const s = svc();
    s.createCurrency.mockReturnValueOnce(problem(400, { errors: { Code: ['Currency code already exists.'] } }) as never);
    const { el, clickText, fire, submit } = mountWith(PlatformCurrencies, s);
    clickText('+ New currency');
    fire('#cu-code', 'input', 'EUR');
    fire('#cu-name', 'input', 'Euro');
    fire('#cu-sym', 'input', '€');
    submit('form[aria-label="New currency"]');
    expect(el.querySelector('#cu-code')!.parentElement!.textContent).toContain('Currency code already exists.');
  });

  it('edit goes through updateCurrency; deactivate through setCurrencyActive', () => {
    const s = svc([cur()]);
    const { el, fixture, submit, clickText } = mountWith(PlatformCurrencies, s);
    el.querySelector<HTMLButtonElement>('.dt-cell-action')!.click();
    fixture.detectChanges();
    submit('form[aria-label="Edit currency"]');
    expect(s.updateCurrency).toHaveBeenCalledWith('k1', { code: 'USD', name: 'US Dollar', symbol: '$', decimalPrecision: 2 });
    clickText('Deactivate', '.dt-cell-action');
    expect(s.setCurrencyActive).toHaveBeenCalledWith('k1', false);
  });
});
