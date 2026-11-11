import { HttpClient, HttpHeaders, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { authInterceptor } from '../auth/auth.interceptor';
import { ContextService } from '../context/context.service';
import { contextInterceptor } from '../http/context.interceptor';
import { correlationInterceptor } from '../http/correlation.interceptor';
import { errorInterceptor } from '../http/error.interceptor';
import { MAIN, me, storeSession, tick } from '../testing/auth-fixtures';
import { PLATFORM_STORAGE_KEY, PlatformAuthService, type PlatformLoginResponse } from './platform-auth.service';
import { platformGuard } from './platform.guard';
import { platformInterceptor } from './platform.interceptor';

const BASE = environment.apiBaseUrl;
const future = () => new Date(Date.now() + 30 * 60_000).toISOString();

function storePlatform(over: Partial<PlatformLoginResponse> = {}): void {
  const s: PlatformLoginResponse = { accessToken: 'platform-token', expiresAt: future(), user: { id: 'p1', name: 'Pat Admin' }, ...over };
  sessionStorage.setItem(PLATFORM_STORAGE_KEY, JSON.stringify(s));
}

function setup(opts: { company?: boolean; platform?: boolean; expiredPlatform?: boolean } = {}) {
  localStorage.clear();
  sessionStorage.clear();
  if (opts.company) storeSession('company-token', 'company-refresh');
  if (opts.platform) storePlatform();
  if (opts.expiredPlatform) storePlatform({ expiresAt: new Date(Date.now() - 1000).toISOString() });
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      provideHttpClient(withInterceptors([correlationInterceptor, platformInterceptor, authInterceptor, contextInterceptor, errorInterceptor])),
      provideHttpClientTesting(),
    ],
  });
  const router = TestBed.inject(Router);
  const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
  return { http: TestBed.inject(HttpClient), mock: TestBed.inject(HttpTestingController), platform: TestBed.inject(PlatformAuthService), router, navigate };
}

describe('platform session', () => {
  it('stores the login response in sessionStorage (not localStorage) and exposes the user', () => {
    const { platform, mock } = setup();
    platform.login({ usernameOrEmail: 'pat', password: 'x' }).subscribe();
    const req = mock.expectOne(`${BASE}/auth/platform/login`);
    req.flush({ accessToken: 't', expiresAt: future(), user: { id: 'p1', name: 'Pat' } });
    expect(platform.isAuthenticated()).toBe(true);
    expect(platform.user()?.name).toBe('Pat');
    expect(sessionStorage.getItem(PLATFORM_STORAGE_KEY)).toContain('"accessToken":"t"');
    expect(localStorage.getItem(PLATFORM_STORAGE_KEY)).toBeNull();
  });

  it('treats an expired token as no session and drops it', () => {
    const { platform } = setup({ expiredPlatform: true });
    expect(platform.accessToken()).toBeNull();
    expect(sessionStorage.getItem(PLATFORM_STORAGE_KEY)).toBeNull();
  });
});

describe('platformInterceptor', () => {
  it('sends the platform token on /platform/* and never X-Branch-Id', () => {
    const { http, mock } = setup({ platform: true });
    http.get(`${BASE}/platform/companies`, { headers: new HttpHeaders({ 'X-Branch-Id': MAIN.id }) }).subscribe();
    const req = mock.expectOne(`${BASE}/platform/companies`);
    expect(req.request.headers.get('Authorization')).toBe('Bearer platform-token');
    expect(req.request.headers.has('X-Branch-Id')).toBe(false);
    expect(req.request.headers.has('X-Correlation-Id')).toBe(true);
  });

  it('the platform login is anonymous (no token, no branch)', () => {
    const { http, mock } = setup({ platform: true, company: true });
    http.post(`${BASE}/auth/platform/login`, {}).subscribe();
    const req = mock.expectOne(`${BASE}/auth/platform/login`);
    expect(req.request.headers.has('Authorization')).toBe(false);
    expect(req.request.headers.has('X-Branch-Id')).toBe(false);
  });

  it('never sends the company token to the platform API, nor the platform token to the company API', async () => {
    const { http, mock } = setup({ platform: true, company: true });
    const ctx = TestBed.inject(ContextService);
    sessionStorage.setItem('sellora.branch.u1.c1', MAIN.id);
    const done = ctx.hydrate();
    const meReq = mock.expectOne(`${BASE}/auth/me`);
    expect(meReq.request.headers.get('Authorization')).toBe('Bearer company-token');
    meReq.flush(me());
    await done;

    http.get(`${BASE}/platform/units`).subscribe();
    const platformReq = mock.expectOne(`${BASE}/platform/units`);
    expect(platformReq.request.headers.get('Authorization')).toBe('Bearer platform-token');

    http.get(`${BASE}/products`).subscribe();
    const companyReq = mock.expectOne(`${BASE}/products`);
    expect(companyReq.request.headers.get('Authorization')).toBe('Bearer company-token');
    expect(companyReq.request.headers.get('X-Branch-Id')).toBe(MAIN.id);
  });

  it('a company session alone sends no token to the platform API', () => {
    const { http, mock } = setup({ company: true });
    http.get(`${BASE}/platform/companies`).subscribe({ error: () => undefined });
    expect(mock.expectOne(`${BASE}/platform/companies`).request.headers.has('Authorization')).toBe(false);
  });

  it('401 on a platform call clears the platform session and goes to /platform/login; the company session survives', async () => {
    const { http, mock, platform, navigate } = setup({ platform: true, company: true });
    http.get(`${BASE}/platform/companies`).subscribe({ error: () => undefined });
    mock.expectOne(`${BASE}/platform/companies`).flush({ code: 'platform_session_required' }, { status: 401, statusText: 'Unauthorized' });
    await tick();
    expect(platform.isAuthenticated()).toBe(false);
    expect(navigate).toHaveBeenCalledWith(['/platform/login'], expect.anything());
    expect(navigate).not.toHaveBeenCalledWith(['/login'], expect.anything());
    expect(localStorage.getItem('sellora.auth')).not.toBeNull();
    mock.verify();
  });
});

describe('platformGuard', () => {
  const run = () => TestBed.runInInjectionContext(() => platformGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));

  it('allows a live platform session', () => {
    setup({ platform: true });
    expect(run()).toBe(true);
  });

  it('sends a visitor with only a company session to /platform/login', () => {
    const { router } = setup({ company: true });
    const r = run() as UrlTree;
    expect(router.serializeUrl(r)).toBe('/platform/login');
  });

  it('rejects an expired platform session', () => {
    setup({ expiredPlatform: true });
    expect(run()).not.toBe(true);
  });
});
