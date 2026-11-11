import { HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { permissionGuard } from '../auth/auth.guard';
import { environment } from '../../../environments/environment';
import { contextInterceptor } from '../http/context.interceptor';
import { ConfirmDialogService } from '../../shared/ui/confirm-dialog/confirm-dialog.service';
import { MAIN, NORTH, authResponse, branchKey, me, storeSession, tick, user } from '../testing/auth-fixtures';
import { ToastService } from '../../shared/ui/toast/toast.service';
import { ALL_BRANCHES } from './context.models';
import { ContextService, HARD_NAVIGATE } from './context.service';

@Component({ template: '' })
class Dummy {}

const API = environment.apiBaseUrl;
const ME_URL = `${API}/auth/me`;

let httpRef: HttpTestingController | undefined;

function setup(opts: { session?: boolean } = {}) {
  const hardNavigate = vi.fn();
  if (opts.session !== false) storeSession();
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'dashboard', component: Dummy },
        { path: 'stock', component: Dummy, canActivate: [permissionGuard('Inventory.Adjust')] },
        { path: 'roles', component: Dummy, canActivate: [permissionGuard(['Roles.View', 'Branch.Roles.View'])] },
      ]),
      provideHttpClient(withInterceptors([contextInterceptor])),
      provideHttpClientTesting(),
      { provide: HARD_NAVIGATE, useValue: hardNavigate },
    ],
  });
  httpRef = TestBed.inject(HttpTestingController);
  return { ctx: TestBed.inject(ContextService), http: httpRef, hardNavigate };
}

describe('ContextService', () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
  });
  afterEach(() => httpRef?.verify());

  it('hydrates from /auth/me: company, branches, permissions, primary as working branch, no header', async () => {
    const { ctx, http } = setup();
    expect(ctx.status()).toBe('loading');

    const done = ctx.hydrate();
    const req = http.expectOne(ME_URL);
    expect(req.request.headers.has('X-Branch-Id')).toBe(false);
    req.flush(me({ governance: { memberId: 'g1', title: 'Owner', permissions: ['Company.Governance.View'] } }));
    await done;

    expect(ctx.status()).toBe('ready');
    expect(ctx.company()).toEqual({ id: 'c1', name: 'Sellora Mart' });
    expect(ctx.allowedBranches().map((b) => b.id)).toEqual([MAIN.id, NORTH.id]);
    expect(ctx.workingBranchId()).toBe(MAIN.id);
    expect(ctx.hasPermission('Sales.View')).toBe(true);
    expect(ctx.hasPermission('Sales.Create')).toBe(false);
    expect(ctx.hasGovernance('Company.Governance.View')).toBe(true);
    expect(ctx.hasGovernance('Company.Governance.ManageOwners')).toBe(false);
    expect(ctx.canSwitchBranch()).toBe(true);
    expect(ctx.canSwitchCompany()).toBe(false);
  });

  it('is a no-op without a session (status stays loading, no request)', async () => {
    const { ctx } = setup({ session: false });
    await ctx.hydrate();
    expect(ctx.status()).toBe('loading');
    expect(ctx.company()).toBeNull();
  });

  it('uses a valid stored branch preference (sends it as X-Branch-Id and keeps it)', async () => {
    const { ctx, http } = setup();
    sessionStorage.setItem(branchKey(), NORTH.id);

    const done = ctx.hydrate();
    const req = http.expectOne(ME_URL);
    expect(req.request.headers.get('X-Branch-Id')).toBe(NORTH.id);
    req.flush(me());
    await done;

    expect(ctx.workingBranchId()).toBe(NORTH.id);
    expect(ctx.workingBranch()?.name).toBe('North Branch');
  });

  it('falls back to the localStorage last-used branch when the tab has none', async () => {
    const { ctx, http } = setup();
    localStorage.setItem(branchKey(), NORTH.id);
    const done = ctx.hydrate();
    const req = http.expectOne(ME_URL);
    expect(req.request.headers.get('X-Branch-Id')).toBe(NORTH.id);
    req.flush(me());
    await done;
    expect(ctx.workingBranchId()).toBe(NORTH.id);
  });

  it('drops a stored branch the server rejects and retries without a header (primary)', async () => {
    const { ctx, http } = setup();
    sessionStorage.setItem(branchKey(), 'b-revoked');
    localStorage.setItem(branchKey(), 'b-revoked');

    const done = ctx.hydrate();
    http.expectOne(ME_URL).flush({ code: 'branch_access_denied' }, { status: 403, statusText: 'Forbidden' });
    await tick();
    const retry = http.expectOne(ME_URL);
    expect(retry.request.headers.has('X-Branch-Id')).toBe(false);
    retry.flush(me());
    await done;

    expect(ctx.status()).toBe('ready');
    expect(ctx.workingBranchId()).toBe(MAIN.id);
    expect(sessionStorage.getItem(branchKey())).toBeNull();
    expect(localStorage.getItem(branchKey())).toBeNull();
  });

  it('re-asks /me for the resolved branch when a stored branch is not in allowedBranches', async () => {
    const { ctx, http } = setup();
    sessionStorage.setItem(branchKey(), 'b-unknown');

    const done = ctx.hydrate();
    http.expectOne(ME_URL).flush(me());
    await tick();
    const again = http.expectOne(ME_URL);
    expect(again.request.headers.get('X-Branch-Id')).toBe(MAIN.id);
    again.flush(me());
    await done;
    expect(ctx.workingBranchId()).toBe(MAIN.id);
  });

  it('company-wide user without primary or preference sends no header and has no working branch', async () => {
    const { ctx, http } = setup();
    const done = ctx.hydrate();
    http.expectOne(ME_URL).flush(me({ accessScope: 'CompanyWide', primaryBranchId: null, allowedBranches: [{ ...MAIN, isPrimary: false }, NORTH] }));
    await done;
    expect(ctx.workingBranchId()).toBeNull();
    expect(ctx.workingBranch()?.id).toBe(MAIN.id); // display falls back to the default branch
  });

  it('status noBranch when /me lists no branches, or answers 403 no_branch_access', async () => {
    const a = setup();
    const done = a.ctx.hydrate();
    a.http.expectOne(ME_URL).flush(me({ allowedBranches: [], primaryBranchId: null }));
    await done;
    expect(a.ctx.status()).toBe('noBranch');
    a.http.verify();
    TestBed.resetTestingModule();

    const b = setup();
    const done2 = b.ctx.hydrate();
    b.http.expectOne(ME_URL).flush({ code: 'no_branch_access' }, { status: 403, statusText: 'Forbidden' });
    await done2;
    expect(b.ctx.status()).toBe('noBranch');
  });

  it('status error on a server failure; whenSettled resolves for waiters', async () => {
    const { ctx, http } = setup();
    const settled = ctx.whenSettled();
    const done = ctx.hydrate();
    http.expectOne(ME_URL).flush('boom', { status: 500, statusText: 'Server Error' });
    await done;
    expect(await settled).toBe('error');
  });

  describe('branch switch (D3)', () => {
    async function ready(ctx: ContextService, http: HttpTestingController) {
      const done = ctx.hydrate();
      http.expectOne(ME_URL).flush(me({ permissions: ['Inventory.View', 'Inventory.Adjust'] }));
      await done;
    }

    it('persists the choice, re-fetches /me with the new header and swaps permissions without re-login', async () => {
      const { ctx, http } = setup();
      await ready(ctx, http);
      const before = ctx.contextVersion();

      const sw = ctx.switchBranch(NORTH.id);
      await tick();
      const req = http.expectOne(ME_URL);
      expect(req.request.headers.get('X-Branch-Id')).toBe(NORTH.id);
      req.flush(me({ permissions: ['Sales.View'] }));

      expect(await sw).toBe(true);
      expect(ctx.workingBranchId()).toBe(NORTH.id);
      expect(ctx.hasPermission('Inventory.Adjust')).toBe(false);
      expect(ctx.hasPermission('Sales.View')).toBe(true);
      expect(sessionStorage.getItem(branchKey())).toBe(NORTH.id);
      expect(localStorage.getItem(branchKey())).toBe(NORTH.id);
      expect(ctx.contextVersion()).toBe(before + 1);
    });

    it('rejects a branch that is not allowed and does nothing for the current branch', async () => {
      const { ctx, http } = setup();
      await ready(ctx, http);
      expect(await ctx.switchBranch('b-other')).toBe(false);
      expect(await ctx.switchBranch(MAIN.id)).toBe(true);
      http.expectNone(ME_URL);
    });

    it('asks to confirm when the page has unsaved changes; Stay cancels without any request', async () => {
      const { ctx, http } = setup();
      await ready(ctx, http);
      ctx.registerUnsavedProbe(() => true);
      const confirm = TestBed.inject(ConfirmDialogService);

      const sw = ctx.switchBranch(NORTH.id);
      await tick();
      expect(confirm.active()?.title).toBe('Discard unsaved changes?');
      confirm.respond(false);

      expect(await sw).toBe(false);
      expect(ctx.workingBranchId()).toBe(MAIN.id);
      http.expectNone(ME_URL);
    });

    it('goes to /dashboard when the current route is no longer permitted', async () => {
      const { ctx, http } = setup();
      await ready(ctx, http);
      const router = TestBed.inject(Router);
      await router.navigateByUrl('/stock');
      expect(router.url).toBe('/stock');

      const sw = ctx.switchBranch(NORTH.id);
      await tick();
      http.expectOne(ME_URL).flush(me({ permissions: ['Inventory.View'] })); // lost Inventory.Adjust
      await sw;
      await tick();
      expect(router.url).toBe('/dashboard');
    });

    it('any-of route: stays when one listed code is still held, leaves when none is', async () => {
      const { ctx, http } = setup();
      const done = ctx.hydrate();
      http.expectOne(ME_URL).flush(me({ permissions: ['Branch.Roles.View'] }));
      await done;
      const router = TestBed.inject(Router);
      await router.navigateByUrl('/roles');
      expect(router.url).toBe('/roles');
      expect(ctx.routeAccessible()).toBe(true);

      const sw = ctx.switchBranch(NORTH.id);
      await tick();
      http.expectOne(ME_URL).flush(me({ permissions: ['Roles.View'] }));
      await sw;
      expect(ctx.routeAccessible()).toBe(true);

      const sw2 = ctx.switchBranch(MAIN.id);
      await tick();
      http.expectOne(ME_URL).flush(me({ permissions: ['Sales.View'] }));
      await sw2;
      await tick();
      expect(router.url).toBe('/dashboard');
    });

    it('keeps the current route when it is still permitted', async () => {
      const { ctx, http } = setup();
      await ready(ctx, http);
      const router = TestBed.inject(Router);
      await router.navigateByUrl('/stock');

      const sw = ctx.switchBranch(NORTH.id);
      await tick();
      http.expectOne(ME_URL).flush(me({ permissions: ['Inventory.Adjust'] }));
      await sw;
      expect(router.url).toBe('/stock');
    });

    it('revoked branch (server rejects the new header): resets to primary', async () => {
      const { ctx, http } = setup();
      await ready(ctx, http);
      const sw = ctx.switchBranch(NORTH.id);
      await tick();
      http.expectOne(ME_URL).flush({ code: 'branch_access_denied' }, { status: 403, statusText: 'Forbidden' });
      await tick();
      http.expectOne(ME_URL).flush(me());
      await sw;
      expect(ctx.workingBranchId()).toBe(MAIN.id);
    });
  });

  describe('probeBranchAccess (D5 branch_access_denied)', () => {
    it("'resource' when /me still accepts the header", async () => {
      const { ctx, http } = setup();
      const done = ctx.hydrate();
      http.expectOne(ME_URL).flush(me());
      await done;

      const probe = ctx.probeBranchAccess();
      const req = http.expectOne(ME_URL);
      expect(req.request.headers.get('X-Branch-Id')).toBe(MAIN.id);
      req.flush(me());
      expect(await probe).toBe('resource');
      expect(ctx.workingBranchId()).toBe(MAIN.id);
    });

    it("'revoked' when /me also rejects the header: reset to primary and bump the context version", async () => {
      const { ctx, http } = setup();
      let done = ctx.hydrate();
      http.expectOne(ME_URL).flush(me());
      await done;
      const sw = ctx.switchBranch(NORTH.id);
      await tick();
      http.expectOne(ME_URL).flush(me());
      await sw;
      expect(ctx.workingBranchId()).toBe(NORTH.id);
      const version = ctx.contextVersion();

      const probe = ctx.probeBranchAccess();
      http.expectOne(ME_URL).flush({ code: 'branch_access_denied' }, { status: 403, statusText: 'Forbidden' });
      await tick();
      http.expectOne(ME_URL).flush(me());
      expect(await probe).toBe('revoked');
      expect(ctx.workingBranchId()).toBe(MAIN.id);
      expect(ctx.contextVersion()).toBe(version + 1);
    });
  });

  it('company switch posts the refresh token, adopts the session, clears the preference and hard-navigates', async () => {
    const { ctx, http, hardNavigate } = setup();
    const done = ctx.hydrate();
    http.expectOne(ME_URL).flush(me());
    await done;
    sessionStorage.setItem(branchKey(), NORTH.id);

    let result: unknown;
    ctx.switchCompany('c2').subscribe((r) => (result = r));
    const req = http.expectOne(`${API}/auth/switch-company`);
    expect(req.request.body).toEqual({ companyId: 'c2', refreshToken: 'refresh-1' });
    req.flush(authResponse({ user: user({ companyId: 'c2', companyName: 'Other Co' }) }));

    expect(result).toBeTruthy();
    expect(sessionStorage.getItem(branchKey('u1', 'c1'))).toBeNull();
    expect(JSON.parse(localStorage.getItem('sellora.auth')!).accessToken).toBe('access-2');
    expect(hardNavigate).toHaveBeenCalledWith('/dashboard');
  });

  it('company switch failure surfaces the error and does not navigate', async () => {
    const { ctx, http, hardNavigate } = setup();
    let err: unknown;
    ctx.switchCompany('c2').subscribe({ error: (e) => (err = e) });
    http.expectOne(`${API}/auth/switch-company`).flush({ code: 'company_access_denied' }, { status: 403, statusText: 'Forbidden' });
    expect(err).toBeInstanceOf(HttpErrorResponse);
    expect(hardNavigate).not.toHaveBeenCalled();
  });

  it('reset clears everything', async () => {
    const { ctx, http } = setup();
    const done = ctx.hydrate();
    http.expectOne(ME_URL).flush(me());
    await done;
    ctx.reset();
    expect(ctx.status()).toBe('loading');
    expect(ctx.company()).toBeNull();
    expect(ctx.hasPermission('Sales.View')).toBe(false);
    expect(ctx.workingBranchId()).toBeNull();
  });
});

describe('All branches (D13) and switch rollback', () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
  });
  afterEach(() => httpRef?.verify());

  const WITH_ANY = { permissions: ['Sales.View'], permissionsAnyBranch: ['Sales.View', 'Sales.Create', 'Inventory.Adjust'] };

  async function ready(ctx: ContextService, http: HttpTestingController, over: Parameters<typeof me>[0] = {}) {
    const done = ctx.hydrate();
    http.expectOne(ME_URL).flush(me({ ...WITH_ANY, ...over }));
    await done;
  }

  it('is offered with 2+ branches or to company-wide members, not to a single-branch assigned user', async () => {
    const a = setup();
    await ready(a.ctx, a.http);
    expect(a.ctx.canChooseAll()).toBe(true);
    TestBed.resetTestingModule();
    const b = setup();
    await ready(b.ctx, b.http, { allowedBranches: [MAIN] });
    expect(b.ctx.canChooseAll()).toBe(false);
    expect(b.ctx.canSwitchBranch()).toBe(false);
    TestBed.resetTestingModule();
    const c = setup();
    await ready(c.ctx, c.http, { allowedBranches: [MAIN], accessScope: 'CompanyWide' });
    expect(c.ctx.canChooseAll()).toBe(true);
  });

  it('selecting All branches clears the working branch, sends no header, uses permissionsAnyBranch and persists "all"', async () => {
    const { ctx, http } = setup();
    await ready(ctx, http);
    expect(ctx.hasPermission('Sales.Create')).toBe(false);

    const sw = ctx.switchBranch(ALL_BRANCHES);
    await tick();
    const req = http.expectOne(ME_URL);
    expect(req.request.headers.has('X-Branch-Id')).toBe(false);
    req.flush(me(WITH_ANY));
    expect(await sw).toBe(true);

    expect(ctx.isAllBranches()).toBe(true);
    expect(ctx.workingBranchId()).toBeNull();
    expect(ctx.workingBranch()).toBeNull();
    expect(ctx.hasPermission('Sales.Create')).toBe(true);
    expect(sessionStorage.getItem(branchKey())).toBe(ALL_BRANCHES);
    expect(localStorage.getItem(branchKey())).toBe(ALL_BRANCHES);
  });

  it('falls back to the branch permissions when the server sends no permissionsAnyBranch', async () => {
    const { ctx, http } = setup();
    await ready(ctx, http, { permissionsAnyBranch: undefined });
    const sw = ctx.switchBranch(ALL_BRANCHES);
    await tick();
    http.expectOne(ME_URL).flush(me({ permissions: ['Sales.View'], permissionsAnyBranch: undefined }));
    await sw;
    expect(ctx.hasPermission('Sales.View')).toBe(true);
    expect(ctx.hasPermission('Sales.Create')).toBe(false);
  });

  it('the explicit choice survives a reload (not overridden by the primary default) and a later hydrate', async () => {
    sessionStorage.setItem(branchKey(), ALL_BRANCHES);
    const { ctx, http } = setup();
    await ready(ctx, http);
    expect(ctx.isAllBranches()).toBe(true);
    expect(ctx.workingBranchId()).toBeNull();
    const again = ctx.hydrate();
    const req = http.expectOne(ME_URL);
    expect(req.request.headers.has('X-Branch-Id')).toBe(false);
    req.flush(me(WITH_ANY));
    await again;
    expect(ctx.isAllBranches()).toBe(true);
  });

  it('a stored "all" is dropped when the member can no longer choose it (default branch is used)', async () => {
    sessionStorage.setItem(branchKey(), ALL_BRANCHES);
    const { ctx, http } = setup();
    await ready(ctx, http, { allowedBranches: [MAIN] });
    expect(ctx.isAllBranches()).toBe(false);
    expect(ctx.workingBranchId()).toBe(MAIN.id);
    expect(sessionStorage.getItem(branchKey())).toBeNull();
  });

  it('switching from All back to a branch sends that header and restores branch permissions', async () => {
    sessionStorage.setItem(branchKey(), ALL_BRANCHES);
    const { ctx, http } = setup();
    await ready(ctx, http);
    const sw = ctx.switchBranch(NORTH.id);
    await tick();
    const req = http.expectOne(ME_URL);
    expect(req.request.headers.get('X-Branch-Id')).toBe(NORTH.id);
    req.flush(me({ permissions: ['Sales.View'], permissionsAnyBranch: WITH_ANY.permissionsAnyBranch }));
    expect(await sw).toBe(true);
    expect(ctx.isAllBranches()).toBe(false);
    expect(ctx.workingBranchId()).toBe(NORTH.id);
    expect(ctx.hasPermission('Sales.Create')).toBe(false);
    expect(sessionStorage.getItem(branchKey())).toBe(NORTH.id);
  });

  it('rejects All when it is not offered', async () => {
    const { ctx, http } = setup();
    await ready(ctx, http, { allowedBranches: [MAIN] });
    expect(await ctx.switchBranch(ALL_BRANCHES)).toBe(false);
    http.expectNone(ME_URL);
  });

  it('rolls back to the previous branch (and its persisted value) when hydrate fails', async () => {
    const { ctx, http } = setup();
    await ready(ctx, http);
    expect(sessionStorage.getItem(branchKey())).toBeNull();

    const sw = ctx.switchBranch(NORTH.id);
    await tick();
    http.expectOne(ME_URL).flush('boom', { status: 500, statusText: 'Server Error' });

    expect(await sw).toBe(false);
    expect(ctx.workingBranchId()).toBe(MAIN.id);
    expect(ctx.status()).toBe('ready');
    expect(ctx.hasPermission('Sales.View')).toBe(true);
    expect(sessionStorage.getItem(branchKey())).toBe(MAIN.id);
    expect(localStorage.getItem(branchKey())).toBe(MAIN.id);
    expect(TestBed.inject(ToastService).messages().length).toBe(1);
  });

  it('rolls back to All branches when the failed switch started from All', async () => {
    sessionStorage.setItem(branchKey(), ALL_BRANCHES);
    const { ctx, http } = setup();
    await ready(ctx, http);
    const sw = ctx.switchBranch(NORTH.id);
    await tick();
    http.expectOne(ME_URL).flush('boom', { status: 500, statusText: 'Server Error' });
    expect(await sw).toBe(false);
    expect(ctx.isAllBranches()).toBe(true);
    expect(ctx.workingBranchId()).toBeNull();
    expect(sessionStorage.getItem(branchKey())).toBe(ALL_BRANCHES);
  });
});
