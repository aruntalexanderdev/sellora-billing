import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { ContextService } from '../context/context.service';
import { contextInterceptor } from '../http/context.interceptor';
import { me, storeSession, tick } from '../testing/auth-fixtures';
import { authGuard, governanceGuard, permissionGuard } from './auth.guard';

const ME_URL = `${environment.apiBaseUrl}/auth/me`;
const route = {} as ActivatedRouteSnapshot;
const state = (url: string) => ({ url }) as RouterStateSnapshot;

function setup(opts: { session?: boolean } = {}) {
  localStorage.clear();
  sessionStorage.clear();
  if (opts.session !== false) storeSession();
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideHttpClient(withInterceptors([contextInterceptor])), provideHttpClientTesting()],
  });
  return { ctx: TestBed.inject(ContextService), http: TestBed.inject(HttpTestingController), router: TestBed.inject(Router) };
}

const run = (guard: ReturnType<typeof permissionGuard>, url = '/x') =>
  TestBed.runInInjectionContext(() => guard(route, state(url))) as Promise<boolean | UrlTree>;
const asUrl = (r: boolean | UrlTree, router: Router) => (typeof r === 'boolean' ? r : router.serializeUrl(r));

describe('route guards', () => {
  it('authGuard sends anonymous users to /login with the return url', async () => {
    const { router } = setup({ session: false });
    const r = await (TestBed.runInInjectionContext(() => authGuard(route, state('/sales'))) as Promise<boolean | UrlTree>);
    expect(asUrl(r, router)).toBe('/login?returnUrl=%2Fsales');
  });

  it('authGuard waits for the context and sends no-branch users to /no-branch-access', async () => {
    const { ctx, http, router } = setup();
    const pending = TestBed.runInInjectionContext(() => authGuard(route, state('/dashboard'))) as Promise<boolean | UrlTree>;
    let settled = false;
    void pending.then(() => (settled = true));
    await tick();
    expect(settled).toBe(false);

    const done = ctx.hydrate();
    http.expectOne(ME_URL).flush(me({ allowedBranches: [], primaryBranchId: null }));
    await done;
    expect(asUrl(await pending, router)).toBe('/no-branch-access');
  });

  it('permissionGuard waits until the context is ready, then allows a held permission', async () => {
    const { ctx, http } = setup();
    const pending = run(permissionGuard('Sales.View'));
    let settled = false;
    void pending.then(() => (settled = true));
    await tick();
    expect(settled).toBe(false); // would have been "denied" before /me answered

    const done = ctx.hydrate();
    http.expectOne(ME_URL).flush(me({ permissions: ['Sales.View'] }));
    await done;
    expect(await pending).toBe(true);
  });

  it('permissionGuard denies a missing permission with /permission-denied?permission=', async () => {
    const { ctx, http, router } = setup();
    const done = ctx.hydrate();
    http.expectOne(ME_URL).flush(me({ permissions: ['Sales.View'] }));
    await done;
    expect(asUrl(await run(permissionGuard('Inventory.Adjust')), router)).toBe('/permission-denied?permission=Inventory.Adjust');
  });

  it('permissionGuard sends anonymous users to /login and no-branch users to /no-branch-access', async () => {
    const a = setup({ session: false });
    expect(asUrl(await run(permissionGuard('Sales.View')), a.router)).toBe('/login');
    TestBed.resetTestingModule();

    const b = setup();
    const done = b.ctx.hydrate();
    b.http.expectOne(ME_URL).flush({ code: 'branch_required' }, { status: 403, statusText: 'Forbidden' });
    await done;
    expect(asUrl(await run(permissionGuard('Sales.View')), b.router)).toBe('/no-branch-access');
  });

  it('guards carry their requirement so the context can re-check the active route after a branch switch', () => {
    expect((permissionGuard('A.B') as unknown as { permissionCode: string }).permissionCode).toBe('A.B');
    const g = governanceGuard('Company.Governance.View') as unknown as { requiresGovernance: boolean; governanceCode: string };
    expect(g.requiresGovernance).toBe(true);
    expect(g.governanceCode).toBe('Company.Governance.View');
  });

  describe('permissionGuard any-of', () => {
    it('allows when any listed code is held, denies (listing all codes) otherwise, and carries the list marker', async () => {
      const { ctx, http, router } = setup();
      const done = ctx.hydrate();
      http.expectOne(ME_URL).flush(me({ permissions: ['Branch.Roles.View'] }));
      await done;
      const g = permissionGuard(['Roles.View', 'Branch.Roles.View']);
      expect(await run(g)).toBe(true);
      expect(asUrl(await run(permissionGuard(['Roles.Create', 'Branch.Roles.Manage'])), router))
        .toBe('/permission-denied?permission=Roles.Create,Branch.Roles.Manage');
      const req = g as unknown as { permissionCode?: string; permissionAnyOf?: string[] };
      expect(req.permissionAnyOf).toEqual(['Roles.View', 'Branch.Roles.View']);
      expect(req.permissionCode).toBeUndefined();
    });
  });

  describe('governanceGuard', () => {
    it('denies non-members, members without the code, and allows members with it', async () => {
      const { ctx, http, router } = setup();
      const done = ctx.hydrate();
      http.expectOne(ME_URL).flush(me({ governance: null }));
      await done;
      expect(asUrl(await run(governanceGuard('Company.Governance.View')), router)).toBe('/permission-denied?permission=Company.Governance.View');

      const again = ctx.hydrate();
      http.expectOne(ME_URL).flush(me({ governance: { memberId: 'g', title: 'Manager', permissions: ['Company.Governance.View'] } }));
      await again;
      expect(await run(governanceGuard('Company.Governance.View'))).toBe(true);
      expect(await run(governanceGuard())).toBe(true);
      expect(asUrl(await run(governanceGuard('Company.Governance.ManageOwners')), router)).toContain('/permission-denied');
    });
  });
});
