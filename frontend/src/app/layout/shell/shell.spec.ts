import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { ContextService, HARD_NAVIGATE } from '../../core/context/context.service';
import { MAIN, NORTH, authResponse, me, storeSession, tick } from '../../core/testing/auth-fixtures';
import { ConfirmDialogService } from '../../shared/ui/confirm-dialog/confirm-dialog.service';
import { Shell } from './shell';

@Component({ template: '' })
class Dummy {}

const API = environment.apiBaseUrl;
const ME_URL = `${API}/auth/me`;

async function mount(meResponse: ReturnType<typeof me>) {
  localStorage.clear();
  sessionStorage.clear();
  storeSession();
  const hardNavigate = vi.fn();
  TestBed.configureTestingModule({
    imports: [Shell],
    providers: [
      provideRouter([{ path: 'dashboard', component: Dummy }]),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: HARD_NAVIGATE, useValue: hardNavigate },
    ],
  });
  const http = TestBed.inject(HttpTestingController);
  const ctx = TestBed.inject(ContextService);
  const done = ctx.hydrate();
  http.expectOne(ME_URL).flush(meResponse);
  await done;
  const fixture = TestBed.createComponent(Shell);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, ctx, http, hardNavigate, el: fixture.nativeElement as HTMLElement };
}

const select = (el: HTMLElement, label: 'Branch' | 'Company') => el.querySelector<HTMLSelectElement>(`select[aria-label="${label}"]`);
const navLinks = (el: HTMLElement) => Array.from(el.querySelectorAll('.sb-item-label')).map((n) => n.textContent?.trim());
const choose = (fixture: ComponentFixture<Shell>, sel: HTMLSelectElement, value: string) => {
  sel.value = value;
  sel.dispatchEvent(new Event('change'));
  fixture.detectChanges();
};

describe('Shell header selectors', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('hides both selectors with one branch and one company', async () => {
    const { el } = await mount(me({ allowedBranches: [MAIN], companies: [{ id: 'c1', name: 'Sellora Mart' }] }));
    expect(select(el, 'Branch')).toBeNull();
    expect(select(el, 'Company')).toBeNull();
  });

  it('shows only the branch selector with several branches and one company', async () => {
    const { el } = await mount(me({ allowedBranches: [MAIN, NORTH] }));
    expect(select(el, 'Branch')).not.toBeNull();
    expect(Array.from(select(el, 'Branch')!.options).map((o) => o.textContent?.trim())).toEqual(['All branches', 'Main Branch', 'North Branch']);
    expect(select(el, 'Branch')!.value).toBe(MAIN.id);
    expect(select(el, 'Company')).toBeNull();
  });

  it('D13: offers All branches (not with a single assigned branch); a company-wide member gets it even with one branch', async () => {
    const one = await mount(me({ allowedBranches: [MAIN] }));
    expect(select(one.el, 'Branch')).toBeNull();
    TestBed.resetTestingModule();
    const wide = await mount(me({ allowedBranches: [MAIN], accessScope: 'CompanyWide' }));
    expect(Array.from(select(wide.el, 'Branch')!.options).map((o) => o.textContent?.trim())).toEqual(['All branches', 'Main Branch']);
  });

  it('D13: choosing All branches shows it in the header and sidebar, sends no header and uses permissionsAnyBranch for the nav', async () => {
    const { fixture, ctx, http, el } = await mount(me({ permissions: ['Sales.View'], permissionsAnyBranch: ['Sales.View', 'Inventory.View'] }));
    expect(navLinks(el)).not.toContain('Products');

    choose(fixture, select(el, 'Branch')!, 'all');
    await tick();
    const req = http.expectOne(ME_URL);
    expect(req.request.headers.has('X-Branch-Id')).toBe(false);
    req.flush(me({ permissions: ['Sales.View'], permissionsAnyBranch: ['Sales.View', 'Inventory.View'] }));
    await tick();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(ctx.isAllBranches()).toBe(true);
    expect(select(el, 'Branch')!.value).toBe('all');
    expect(el.querySelector('.sb-branch')?.textContent?.trim()).toBe('All branches');
    expect(navLinks(el)).toContain('Products');
  });

  it('shows only the company selector with several companies and one branch', async () => {
    const { el } = await mount(me({ allowedBranches: [MAIN], companies: [{ id: 'c1', name: 'Sellora Mart' }, { id: 'c2', name: 'Other Co' }] }));
    expect(select(el, 'Branch')).toBeNull();
    expect(select(el, 'Company')).not.toBeNull();
    expect(select(el, 'Company')!.value).toBe('c1');
  });

  it('shows both selectors with several branches and companies', async () => {
    const { el } = await mount(me({ companies: [{ id: 'c1', name: 'Sellora Mart' }, { id: 'c2', name: 'Other Co' }] }));
    expect(select(el, 'Branch')).not.toBeNull();
    expect(select(el, 'Company')).not.toBeNull();
  });

  it('cancelled branch switch (confirm declined) resets the select, keeps the branch and calls nothing', async () => {
    const { fixture, ctx, el } = await mount(me());
    ctx.registerUnsavedProbe(() => true);
    const ask = vi.spyOn(TestBed.inject(ConfirmDialogService), 'ask').mockResolvedValue(false);

    choose(fixture, select(el, 'Branch')!, NORTH.id);
    await tick();
    fixture.detectChanges();

    expect(ask).toHaveBeenCalledTimes(1);
    expect(select(el, 'Branch')!.value).toBe(MAIN.id);
    expect(ctx.workingBranchId()).toBe(MAIN.id);
    expect(el.querySelector('.sb-branch')?.textContent?.trim()).toBe('Main Branch');
  });

  it('confirmed branch switch re-hydrates for the new branch and the sidebar shows it', async () => {
    const { fixture, ctx, http, el } = await mount(me());
    ctx.registerUnsavedProbe(() => true);
    vi.spyOn(TestBed.inject(ConfirmDialogService), 'ask').mockResolvedValue(true);

    choose(fixture, select(el, 'Branch')!, NORTH.id);
    await tick();
    const req = http.expectOne(ME_URL);
    req.flush(me({ permissions: ['Sales.View'] }));
    await tick();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(ctx.workingBranchId()).toBe(NORTH.id);
    expect(el.querySelector('.sb-branch')?.textContent?.trim()).toBe('North Branch');
    expect(select(el, 'Branch')!.value).toBe(NORTH.id);
  });

  it('cancelled company switch resets the select and posts nothing', async () => {
    const { fixture, ctx, http, el, hardNavigate } = await mount(
      me({ companies: [{ id: 'c1', name: 'Sellora Mart' }, { id: 'c2', name: 'Other Co' }] }),
    );
    ctx.registerUnsavedProbe(() => true);
    vi.spyOn(TestBed.inject(ConfirmDialogService), 'ask').mockResolvedValue(false);

    choose(fixture, select(el, 'Company')!, 'c2');
    await tick();
    fixture.detectChanges();

    http.expectNone(`${API}/auth/switch-company`);
    expect(select(el, 'Company')!.value).toBe('c1');
    expect(hardNavigate).not.toHaveBeenCalled();
  });

  it('confirmed company switch posts the new company and hard-navigates to the dashboard', async () => {
    const { fixture, http, el, hardNavigate } = await mount(
      me({ companies: [{ id: 'c1', name: 'Sellora Mart' }, { id: 'c2', name: 'Other Co' }] }),
    );
    choose(fixture, select(el, 'Company')!, 'c2');
    await tick();
    const req = http.expectOne(`${API}/auth/switch-company`);
    expect(req.request.body).toMatchObject({ companyId: 'c2' });
    req.flush(authResponse());
    await tick();
    expect(hardNavigate).toHaveBeenCalledWith('/dashboard');
  });
});

describe('Shell sidebar', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('shows the company name and the working branch name', async () => {
    const { el } = await mount(me());
    expect(el.querySelector('.sb-tenant')?.textContent?.trim()).toBe('Sellora Mart');
    expect(el.querySelector('.sb-branch')?.textContent?.trim()).toBe('Main Branch');
  });

  it('lists only the always-visible entries when the user holds no permission', async () => {
    const { el } = await mount(me({ permissions: [] }));
    expect(navLinks(el)).toContain('Dashboard');
    expect(navLinks(el)).not.toContain('Branches');
    expect(navLinks(el)).not.toContain('Users');
    expect(navLinks(el)).not.toContain('Governance');
  });

  it('filters entries by the working-branch permission set', async () => {
    const { el } = await mount(me({ permissions: ['Company.Branches.View', 'Users.View'] }));
    expect(navLinks(el)).toEqual(expect.arrayContaining(['Dashboard', 'Branches', 'Users']));
    expect(navLinks(el)).not.toContain('Warehouses');
    expect(navLinks(el)).not.toContain('Stock Overview');
  });

  it.each([['Roles.View'], ['Branch.Roles.View']])('any-of entry "Roles & Permissions" is visible with only %s', async (code) => {
    const { el } = await mount(me({ permissions: [code] }));
    expect(navLinks(el)).toContain('Roles & Permissions');
  });

  it('any-of entry is hidden when neither code is held', async () => {
    const { el } = await mount(me({ permissions: ['Users.View'] }));
    expect(navLinks(el)).not.toContain('Roles & Permissions');
  });

  it('governance entry appears only for a governance member holding the code', async () => {
    const member = await mount(me({ permissions: [], governance: { memberId: 'g1', title: 'Owner', permissions: ['Company.Governance.View'] } }));
    expect(navLinks(member.el)).toContain('Governance');
    TestBed.resetTestingModule();

    const plain = await mount(me({ permissions: ['Company.Governance.View'], governance: null }));
    expect(navLinks(plain.el)).not.toContain('Governance');
    TestBed.resetTestingModule();

    const other = await mount(me({ permissions: [], governance: { memberId: 'g1', title: 'Owner', permissions: ['Company.Governance.Manage'] } }));
    expect(navLinks(other.el)).not.toContain('Governance');
  });
});
