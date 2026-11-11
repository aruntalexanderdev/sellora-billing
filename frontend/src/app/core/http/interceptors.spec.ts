import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { ToastService } from '../../shared/ui/toast/toast.service';
import { authInterceptor } from '../auth/auth.interceptor';
import { AuthService } from '../auth/auth.service';
import { ContextService, HARD_NAVIGATE } from '../context/context.service';
import { MAIN, NORTH, authResponse, branchKey, me, storeSession, tick } from '../testing/auth-fixtures';
import { FORBIDDEN_MESSAGE, CONFLICT_MESSAGE } from './api-errors';
import { contextInterceptor } from './context.interceptor';
import { correlationInterceptor } from './correlation.interceptor';
import { errorInterceptor } from './error.interceptor';
import { primaryLoad } from './request-context';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const ORIGINAL_BASE = environment.apiBaseUrl;

for (const base of ['/api/v1', 'https://api.example.test/api/v1']) {
  describe(`HTTP interceptors (apiBaseUrl = ${base})`, () => {
    let http: HttpClient;
    let mock: HttpTestingController;
    let router: Router;
    let ctx: ContextService;
    let auth: AuthService;
    let toast: ToastService;
    let navigate: ReturnType<typeof vi.spyOn>;
    let hardNavigate: ReturnType<typeof vi.fn>;

    async function setWorkingBranch(id: string | null) {
      if (id) {
        sessionStorage.setItem(branchKey(), id);
        const done = ctx.hydrate();
        mock.expectOne(`${base}/auth/me`).flush(me());
        await done;
      }
    }

    beforeEach(() => {
      (environment as { apiBaseUrl: string }).apiBaseUrl = base;
      localStorage.clear();
      sessionStorage.clear();
      storeSession();
      hardNavigate = vi.fn();
      TestBed.configureTestingModule({
        providers: [
          { provide: HARD_NAVIGATE, useValue: hardNavigate },
          provideRouter([]),
          provideHttpClient(withInterceptors([correlationInterceptor, authInterceptor, contextInterceptor, errorInterceptor])),
          provideHttpClientTesting(),
        ],
      });
      http = TestBed.inject(HttpClient);
      mock = TestBed.inject(HttpTestingController);
      router = TestBed.inject(Router);
      ctx = TestBed.inject(ContextService);
      auth = TestBed.inject(AuthService);
      toast = TestBed.inject(ToastService);
      navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    });

    afterEach(() => {
      mock.match(`${base}/auth/logout`); // fire-and-forget logout calls are not under test here
      mock.verify();
      (environment as { apiBaseUrl: string }).apiBaseUrl = ORIGINAL_BASE;
    });

    describe('correlation', () => {
      it('adds a fresh UUID X-Correlation-Id to API calls only', () => {
        http.get(`${base}/things`).subscribe();
        http.get(`${base}/things`).subscribe();
        http.get('https://elsewhere.example.test/api/v1/things').subscribe();
        const [a, b] = mock.match(`${base}/things`);
        const other = mock.expectOne('https://elsewhere.example.test/api/v1/things');
        expect(a.request.headers.get('X-Correlation-Id')).toMatch(UUID);
        expect(b.request.headers.get('X-Correlation-Id')).toMatch(UUID);
        expect(a.request.headers.get('X-Correlation-Id')).not.toBe(b.request.headers.get('X-Correlation-Id'));
        expect(other.request.headers.has('X-Correlation-Id')).toBe(false);
      });
    });

    describe('X-Branch-Id (D2)', () => {
      it('is sent with the working branch on API calls', async () => {
        await setWorkingBranch(NORTH.id);
        http.get(`${base}/sales`).subscribe();
        const req = mock.expectOne(`${base}/sales`);
        expect(req.request.headers.get('X-Branch-Id')).toBe(NORTH.id);
        expect(req.request.headers.get('Authorization')).toBe('Bearer access-1');
      });

      it('is absent when there is no working branch', () => {
        http.get(`${base}/sales`).subscribe();
        expect(mock.expectOne(`${base}/sales`).request.headers.has('X-Branch-Id')).toBe(false);
      });

      it.each(['/auth/login', '/auth/refresh', '/auth/logout', '/platform/companies', '/platform/reference-data/units', '/auth/platform/login'])(
        'is absent (and no company token) on %s',
        async (path) => {
          await setWorkingBranch(MAIN.id);
          http.post(`${base}${path}`, {}).subscribe({ error: () => {} });
          const req = mock.expectOne(`${base}${path}`);
          expect(req.request.headers.has('X-Branch-Id')).toBe(false);
          expect(req.request.headers.has('Authorization')).toBe(false);
          req.flush({});
        },
      );

      it('is present on other API calls, absent (with no company token) on non-API URLs', async () => {
        await setWorkingBranch(MAIN.id);
        http.get(`${base}/branches/accessible`).subscribe();
        expect(mock.expectOne(`${base}/branches/accessible`).request.headers.get('X-Branch-Id')).toBe(MAIN.id);
        http.get('/assets/config.json').subscribe();
        const asset = mock.expectOne('/assets/config.json');
        expect(asset.request.headers.has('X-Branch-Id')).toBe(false);
        expect(asset.request.headers.has('Authorization')).toBe(false);
      });
    });

    describe('single-flight refresh (D4)', () => {
      it('5 parallel 401s trigger exactly 1 refresh, then each request is retried once with the new token', () => {
        const results: number[] = [];
        for (let i = 0; i < 5; i++) http.get<number>(`${base}/items/${i}`).subscribe((v) => results.push(v));

        for (let i = 0; i < 5; i++) {
          mock.expectOne(`${base}/items/${i}`).flush({ code: 'x' }, { status: 401, statusText: 'Unauthorized' });
        }
        const refreshes = mock.match(`${base}/auth/refresh`);
        expect(refreshes.length).toBe(1);
        expect(refreshes[0].request.body).toEqual({ refreshToken: 'refresh-1' });
        refreshes[0].flush(authResponse());

        for (let i = 0; i < 5; i++) {
          const retry = mock.expectOne(`${base}/items/${i}`);
          expect(retry.request.headers.get('Authorization')).toBe('Bearer access-2');
          retry.flush(i);
        }
        expect(results.sort()).toEqual([0, 1, 2, 3, 4]);
        expect(JSON.parse(localStorage.getItem('sellora.auth')!).refreshToken).toBe('refresh-2');
      });

      it('a late 401 for an already-rotated token retries with the current token without another refresh', () => {
        http.get(`${base}/a`).subscribe();
        http.get(`${base}/b`).subscribe();
        mock.expectOne(`${base}/a`).flush({}, { status: 401, statusText: 'Unauthorized' });
        mock.expectOne(`${base}/auth/refresh`).flush(authResponse());
        mock.expectOne(`${base}/a`).flush('ok');
        // /b was sent with access-1 and fails only now
        mock.expectOne(`${base}/b`).flush({}, { status: 401, statusText: 'Unauthorized' });
        expect(mock.match(`${base}/auth/refresh`).length).toBe(0);
        const retry = mock.expectOne(`${base}/b`);
        expect(retry.request.headers.get('Authorization')).toBe('Bearer access-2');
        retry.flush('ok');
      });

      it('adopts tokens rotated by another tab (storage event) and skips the refresh', () => {
        http.get(`${base}/a`).subscribe();
        const stale = mock.expectOne(`${base}/a`);
        const rotated = JSON.stringify({ accessToken: 'access-9', refreshToken: 'refresh-9', user: auth.currentUser() });
        localStorage.setItem('sellora.auth', rotated);
        window.dispatchEvent(new StorageEvent('storage', { key: 'sellora.auth', newValue: rotated }));
        expect(auth.accessToken()).toBe('access-9');

        stale.flush({}, { status: 401, statusText: 'Unauthorized' });
        expect(mock.match(`${base}/auth/refresh`).length).toBe(0);
        expect(mock.expectOne(`${base}/a`).request.headers.get('Authorization')).toBe('Bearer access-9');
      });

      it('another tab signing out clears this tab, clears the context and goes to /login', () => {
        const reset = vi.spyOn(ctx, 'reset');
        window.dispatchEvent(new StorageEvent('storage', { key: 'sellora.auth', newValue: null }));
        expect(auth.isAuthenticated()).toBe(false);
        expect(reset).toHaveBeenCalled();
        expect(navigate).toHaveBeenCalledWith(['/login']);
      });

      it('another tab switching company (different companyId) resets the context and hard-navigates; no stale X-Branch-Id', async () => {
        await setWorkingBranch(NORTH.id);
        const other = JSON.stringify({ accessToken: 'access-c2', refreshToken: 'refresh-c2', user: { ...auth.currentUser(), companyId: 'c2' } });
        localStorage.setItem('sellora.auth', other);
        window.dispatchEvent(new StorageEvent('storage', { key: 'sellora.auth', newValue: other }));
        expect(auth.accessToken()).toBe('access-c2');
        expect(ctx.workingBranchId()).toBeNull();
        expect(ctx.status()).toBe('loading');
        expect(hardNavigate).toHaveBeenCalledWith('/dashboard');
        http.get(`${base}/sales`).subscribe();
        expect(mock.expectOne(`${base}/sales`).request.headers.has('X-Branch-Id')).toBe(false);
      });

      it('another tab signing in as a different user also resets and hard-navigates', () => {
        const other = JSON.stringify({ accessToken: 'a', refreshToken: 'r', user: { ...auth.currentUser(), id: 'u2' } });
        window.dispatchEvent(new StorageEvent('storage', { key: 'sellora.auth', newValue: other }));
        expect(hardNavigate).toHaveBeenCalledWith('/dashboard');
      });

      it('a rotated token for the same user and company neither resets nor navigates', () => {
        const rotated = JSON.stringify({ accessToken: 'access-9', refreshToken: 'refresh-9', user: auth.currentUser() });
        window.dispatchEvent(new StorageEvent('storage', { key: 'sellora.auth', newValue: rotated }));
        expect(hardNavigate).not.toHaveBeenCalled();
        expect(navigate).not.toHaveBeenCalled();
      });

      it('401 when the session has no refresh token ends the session (login?reason=expired)', () => {
        const noRefresh = JSON.stringify({ accessToken: 'access-1', refreshToken: '', user: auth.currentUser() });
        localStorage.setItem('sellora.auth', noRefresh);
        window.dispatchEvent(new StorageEvent('storage', { key: 'sellora.auth', newValue: noRefresh }));
        http.get(`${base}/things`).subscribe({ error: () => {} });
        mock.expectOne(`${base}/things`).flush({}, { status: 401, statusText: 'Unauthorized' });
        expect(mock.match(`${base}/auth/refresh`).length).toBe(0);
        expect(auth.isAuthenticated()).toBe(false);
        expect(navigate).toHaveBeenCalledWith(['/login'], { queryParams: { reason: 'expired' } });
      });

      it('refresh failure ends the session: login?reason=expired, one logout attempt, caller gets the error', () => {
        const errors: unknown[] = [];
        for (let i = 0; i < 3; i++) http.get(`${base}/items/${i}`).subscribe({ error: (e) => errors.push(e) });
        for (let i = 0; i < 3; i++) mock.expectOne(`${base}/items/${i}`).flush({}, { status: 401, statusText: 'Unauthorized' });
        const refreshes = mock.match(`${base}/auth/refresh`);
        expect(refreshes.length).toBe(1);
        refreshes[0].flush({ code: 'invalid_refresh_token' }, { status: 401, statusText: 'Unauthorized' });

        expect(errors.length).toBe(3);
        expect(auth.isAuthenticated()).toBe(false);
        expect(navigate).toHaveBeenCalledWith(['/login'], { queryParams: { reason: 'expired' } });
        expect((navigate.mock.calls as unknown as string[][][]).filter((c) => c[0][0] === '/login').length).toBe(1); // a single owner ends the session
        expect(mock.match(`${base}/auth/logout`).length).toBe(1);
      });
    });

    describe('D5 error codes', () => {
      const fail = (status: number, code?: string, url = `${base}/things`, method = 'GET') => {
        http.request(method, url, {}).subscribe({ error: () => {} });
        mock.expectOne(url).flush(code ? { code } : {}, { status, statusText: 'err' });
      };

      it('401 company_context_invalid: one refresh, retry still 401 -> logout, reason revoked', () => {
        http.get(`${base}/things`).subscribe({ error: () => {} });
        mock.expectOne(`${base}/things`).flush({ code: 'company_context_invalid' }, { status: 401, statusText: 'Unauthorized' });
        mock.expectOne(`${base}/auth/refresh`).flush(authResponse());
        mock.expectOne(`${base}/things`).flush({ code: 'company_context_invalid' }, { status: 401, statusText: 'Unauthorized' });
        expect(auth.isAuthenticated()).toBe(false);
        expect(navigate).toHaveBeenCalledWith(['/login'], { queryParams: { reason: 'revoked' } });
      });

      it('401 company_context_invalid with a failing refresh -> logout, reason revoked', () => {
        http.get(`${base}/things`).subscribe({ error: () => {} });
        mock.expectOne(`${base}/things`).flush({ code: 'company_context_invalid' }, { status: 401, statusText: 'Unauthorized' });
        mock.expectOne(`${base}/auth/refresh`).flush({ code: 'invalid_refresh_token' }, { status: 401, statusText: 'Unauthorized' });
        expect(navigate).toHaveBeenCalledWith(['/login'], { queryParams: { reason: 'revoked' } });
      });

      it('401 invalid_refresh_token (e.g. switch-company) -> logout, reason expired, no refresh attempt', () => {
        fail(401, 'invalid_refresh_token', `${base}/auth/switch-company`, 'POST');
        expect(mock.match(`${base}/auth/refresh`).length).toBe(0);
        expect(auth.isAuthenticated()).toBe(false);
        expect(navigate).toHaveBeenCalledWith(['/login'], { queryParams: { reason: 'expired' } });
      });

      it('401 invalid_credentials at login does not end anything', () => {
        fail(401, 'invalid_credentials', `${base}/auth/login`, 'POST');
        expect(navigate).not.toHaveBeenCalled();
        expect(auth.isAuthenticated()).toBe(true);
      });

      it.each(['branch_required', 'no_branch_access'])('403 %s -> no-branch-access page', (code) => {
        const mark = vi.spyOn(ctx, 'markNoBranch');
        fail(403, code);
        expect(mark).toHaveBeenCalled();
        expect(ctx.status()).toBe('noBranch');
        expect(navigate).toHaveBeenCalledWith(['/no-branch-access']);
      });

      it('403 no_branch_access at login / switch-company stays on the form', () => {
        fail(403, 'no_branch_access', `${base}/auth/login`, 'POST');
        fail(403, 'no_branch_access', `${base}/auth/switch-company`, 'POST');
        expect(navigate).not.toHaveBeenCalled();
      });

      it('400 branch_header_invalid -> clear selection and re-hydrate', () => {
        const spy = vi.spyOn(ctx, 'clearSelectionAndRehydrate').mockResolvedValue();
        fail(400, 'branch_header_invalid');
        expect(spy).toHaveBeenCalledTimes(1);
      });

      it('403 branch_access_denied, /me also rejects -> revoked: reset + toast', async () => {
        vi.spyOn(ctx, 'probeBranchAccess').mockImplementation(async () => 'revoked');
        fail(403, 'branch_access_denied');
        await tick();
        expect(toast.messages().some((m) => m.text.includes('access to that branch was removed'))).toBe(true);
      });

      it('403 branch_access_denied, /me accepts -> resource-level: no toast, no redirect', async () => {
        const probe = vi.spyOn(ctx, 'probeBranchAccess').mockImplementation(async () => 'resource');
        fail(403, 'branch_access_denied');
        await tick();
        expect(probe).toHaveBeenCalled();
        expect(toast.messages().length).toBe(0);
        expect(navigate).not.toHaveBeenCalled();
      });

      it('403 branch_access_denied from /auth/me itself is left to the context service', () => {
        const probe = vi.spyOn(ctx, 'probeBranchAccess');
        fail(403, 'branch_access_denied', `${base}/auth/me`);
        expect(probe).not.toHaveBeenCalled();
      });

      it('403 without a code on a primary-load GET -> resync /me, toast, /permission-denied', () => {
        const resync = vi.spyOn(ctx, 'resyncPermissions').mockResolvedValue();
        http.get(`${base}/things`, { context: primaryLoad() }).subscribe({ error: () => {} });
        mock.expectOne(`${base}/things`).flush({}, { status: 403, statusText: 'err' });
        expect(resync).toHaveBeenCalledTimes(1);
        expect(toast.messages().map((m) => m.text)).toContain(FORBIDDEN_MESSAGE);
        expect(navigate).toHaveBeenCalledWith(['/permission-denied']);
      });

      it('403 without a code on a background GET -> toast only, the page stays', () => {
        const resync = vi.spyOn(ctx, 'resyncPermissions').mockResolvedValue();
        fail(403);
        expect(resync).toHaveBeenCalledTimes(1);
        expect(toast.messages().map((m) => m.text)).toContain(FORBIDDEN_MESSAGE);
        expect(navigate).not.toHaveBeenCalled();
      });

      it('/AUTH/* and /PLATFORM/* are matched case-insensitively (no company token or branch header)', async () => {
        await setWorkingBranch(MAIN.id);
        for (const path of ['/Auth/Login', '/PLATFORM/companies', '/Auth/Platform/Login']) {
          http.post(`${base}${path}`, {}).subscribe({ error: () => {} });
          const req = mock.expectOne(`${base}${path}`);
          expect(req.request.headers.has('X-Branch-Id')).toBe(false);
          expect(req.request.headers.has('Authorization')).toBe(false);
          req.flush({});
        }
      });

      it('403 without a code on a write -> resync + toast, stays on the page', () => {
        const resync = vi.spyOn(ctx, 'resyncPermissions').mockResolvedValue();
        fail(403, undefined, `${base}/things`, 'POST');
        expect(resync).toHaveBeenCalled();
        expect(toast.messages().map((m) => m.text)).toContain(FORBIDDEN_MESSAGE);
        expect(navigate).not.toHaveBeenCalled();
      });

      it('403 with a domain code (role / governance / access) is left to the form', () => {
        const resync = vi.spyOn(ctx, 'resyncPermissions').mockResolvedValue();
        fail(403, 'role_escalation_forbidden', `${base}/roles`, 'POST');
        fail(403, 'last_owner_protected', `${base}/governance/members/1`, 'DELETE');
        expect(resync).not.toHaveBeenCalled();
        expect(toast.messages().length).toBe(0);
        expect(navigate).not.toHaveBeenCalled();
      });

      it('409 -> "Reload and try again"', () => {
        fail(409, undefined, `${base}/things`, 'PUT');
        expect(toast.messages().map((m) => m.text)).toContain(CONFLICT_MESSAGE);
      });

      it('the error always continues to the caller', () => {
        let caught: unknown;
        http.get(`${base}/things`).subscribe({ error: (e) => (caught = e) });
        mock.expectOne(`${base}/things`).flush({ code: 'last_owner_protected' }, { status: 403, statusText: 'Forbidden' });
        expect((caught as { error: { code: string } }).error.code).toBe('last_owner_protected');
      });

      it('platform 401 -> /platform/login (never the company login); platform login failure stays', () => {
        fail(401, 'platform_session_required', `${base}/platform/companies`);
        expect(navigate).toHaveBeenCalledWith(['/platform/login']);
        expect(auth.isAuthenticated()).toBe(true);
        navigate.mockClear();
        fail(401, 'invalid_credentials', `${base}/auth/platform/login`, 'POST');
        expect(navigate).not.toHaveBeenCalled();
      });
    });
  });
}
