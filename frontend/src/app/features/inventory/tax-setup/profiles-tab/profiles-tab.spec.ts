import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { todayIso } from '../gst-version-rules';
import type { TaxComponent, TaxProfile, TaxProfileDetail, TaxVersion } from '../tax-setup.models';
import { TaxSetupService } from '../tax-setup.service';
import { ProfilesTab } from './profiles-tab';

const comp = (id: string, kind: TaxComponent['kind']): TaxComponent => ({ id, countryId: 'in', countryCode: 'IN', code: id.toUpperCase(), name: id, kind, method: 'Percent', isActive: true, isUsed: false });
const version = (from: string, to: string | null, total: number, current: boolean, used = false): TaxVersion => ({
  id: 'v' + from, effectiveFrom: from, effectiveTo: to, totalPercent: total, isCurrent: current, isUsed: used, createdAt: '', components: [],
});
const detail: TaxProfileDetail = {
  id: 'p18', countryId: 'in', countryCode: 'IN', countryName: 'India', code: 'GST18', name: 'GST 18%', regime: 'Gst', treatment: 'Taxable', isActive: true, mirrorTaxId: 'm', isAssigned: true,
  versionCount: 2, currentVersion: version('2026-04-01', null, 18, true),
  versions: [version('2026-04-01', null, 18, true), version('2025-01-01', '2026-03-31', 12, false, true)],
};

function render(canManage = true, overrides: Record<string, unknown> = {}) {
  const service = {
    listProfiles: vi.fn(() => of([detail as TaxProfile])),
    getProfile: vi.fn(() => of(detail)),
    listComponents: vi.fn(() => of([comp('cgst', 'Cgst'), comp('sgst', 'Sgst'), comp('igst', 'Igst')])),
    addVersion: vi.fn(() => of(version(todayIso(), null, 20, true))),
    seedStandard: vi.fn(() => of({ countrySupported: true, componentsAdded: 4, profilesAdded: 5, profilesSkipped: 1, companyDefaultSet: false })),
    ...overrides,
  };
  const confirm = { ask: vi.fn(() => Promise.resolve(true)) };
  const toast = { show: vi.fn() };
  TestBed.configureTestingModule({
    imports: [ProfilesTab],
    providers: [
      { provide: TaxSetupService, useValue: service },
      { provide: ConfirmDialogService, useValue: confirm },
      { provide: ToastService, useValue: toast },
    ],
  });
  const fixture = TestBed.createComponent(ProfilesTab);
  fixture.componentRef.setInput('countries', [{ id: 'in', code: 'IN', name: 'India' }]);
  fixture.componentRef.setInput('canManage', canManage);
  fixture.detectChanges();
  return { fixture, el: fixture.nativeElement as HTMLElement, service, confirm, toast, cmp: fixture.componentInstance };
}

describe('ProfilesTab', () => {
  it('lists active profiles by default and shows the version timeline of the selected one', () => {
    const { fixture, el, service, cmp } = render();
    expect(service.listProfiles).toHaveBeenCalledWith(null, true);
    expect(el.querySelectorAll('[data-testid="profile-row"]').length).toBe(1);
    cmp.select('p18');
    fixture.detectChanges();
    const versions = Array.from(el.querySelectorAll('[data-testid="version"]')).map((v) => v.textContent);
    expect(versions[0]).toContain('From 2026-04-01 onwards');
    expect(versions[0]).toContain('Current');
    expect(versions[1]).toContain('From 2025-01-01 to 2026-03-31');
    expect(versions[1]).toContain('Used');
  });

  it('filters by country and status', () => {
    const { service, cmp } = render();
    cmp.setCountry('in');
    cmp.setActiveFilter('');
    expect(service.listProfiles).toHaveBeenLastCalledWith('in', null);
  });

  it('add-version: validates, then posts the version with the profile id and reloads the detail', () => {
    const { fixture, el, service, toast, cmp } = render();
    cmp.select('p18');
    cmp.addingVersion.set(true);
    fixture.detectChanges();
    const form = fixture.debugElement.query((d) => d.name === 'app-tax-version-form').componentInstance;

    cmp.submitVersion(detail);
    expect(service.addVersion).not.toHaveBeenCalled(); // empty rates fail BR3
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="rule-error"]')).not.toBeNull();

    form.quickTotal.set('20');
    form.fillFromTotal();
    cmp.submitVersion(detail);
    expect(service.addVersion).toHaveBeenCalledWith('p18', expect.objectContaining({ effectiveFrom: todayIso(), components: expect.arrayContaining([expect.objectContaining({ taxComponentId: 'igst', ratePercent: 20 })]) }));
    expect(toast.show).toHaveBeenCalledWith('New rate added.');
    expect(cmp.addingVersion()).toBe(false);
  });

  it('add-version: a future date is refused client side (T19)', () => {
    const { fixture, service, cmp } = render();
    cmp.select('p18');
    cmp.addingVersion.set(true);
    fixture.detectChanges();
    const form = fixture.debugElement.query((d) => d.name === 'app-tax-version-form').componentInstance;
    form.quickTotal.set('20');
    form.fillFromTotal();
    form.form.controls.effectiveFrom.setValue('2999-01-01');
    cmp.submitVersion(detail);
    expect(service.addVersion).not.toHaveBeenCalled();
    expect(form.fieldErrors().effectiveFrom).toContain('cannot start after today');
  });

  it('add-version: shows the server effectiveFrom error on the date', () => {
    const err = new HttpErrorResponse({ status: 400, error: { errors: { effectiveFrom: ['Already used by documents.'] } } });
    const { fixture, cmp } = render(true, { addVersion: vi.fn(() => throwError(() => err)) });
    cmp.select('p18');
    cmp.addingVersion.set(true);
    fixture.detectChanges();
    const form = fixture.debugElement.query((d) => d.name === 'app-tax-version-form').componentInstance;
    form.quickTotal.set('20');
    form.fillFromTotal();
    cmp.submitVersion(detail);
    expect(form.fieldErrors().effectiveFrom).toBe('Already used by documents.');
  });

  it('hides write actions without Tax.Manage', () => {
    const { fixture, el, cmp } = render(false);
    cmp.select('p18');
    fixture.detectChanges();
    expect(el.textContent).not.toContain('Seed standard profiles');
    expect(el.textContent).not.toContain('+ New rate from a date');
    expect(el.textContent).not.toContain('Deactivate');
  });

  it('"Seed standard profiles" confirms and reports the result', async () => {
    const { service, confirm, toast, cmp } = render();
    await cmp.seed();
    expect(confirm.ask).toHaveBeenCalled();
    expect(service.seedStandard).toHaveBeenCalled();
    expect(toast.show).toHaveBeenCalledWith('Added 5 profile(s) and 4 component(s); 1 skipped.');
  });
});
