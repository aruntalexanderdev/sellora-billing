import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { ContextService } from '../../core/context/context.service';
import { BranchSelect, type AccessibleBranch } from './branch-select';

const URL = `${environment.apiBaseUrl}/branches/accessible`;
const b = (id: string, name: string, code: string): AccessibleBranch => ({ id, name, code, isDefault: false });

@Component({ imports: [BranchSelect], template: `<app-branch-select [(value)]="picked" />` })
class Host {
  readonly picked = signal<string | null>(null);
}

function mount() {
  const version = signal(0);
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: ContextService, useValue: { contextVersion: version.asReadonly(), company: signal({ id: 'c1', name: 'X' }) } },
    ],
  });
  const fixture = TestBed.createComponent(Host);
  fixture.detectChanges();
  return { fixture, http: TestBed.inject(HttpTestingController), version, el: fixture.nativeElement as HTMLElement };
}

describe('BranchSelect', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('is hidden when the caller can reach a single branch', () => {
    const { fixture, http, el } = mount();
    http.expectOne(URL).flush([b('b1', 'Main', 'MAIN')]);
    fixture.detectChanges();
    expect(el.querySelector('select')).toBeNull();
  });

  it('is hidden when the request fails', () => {
    const { fixture, http, el } = mount();
    http.expectOne(URL).flush({}, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    expect(el.querySelector('select')).toBeNull();
  });

  it('lists "All branches" plus each branch with its code when several are reachable', () => {
    const { fixture, http, el } = mount();
    http.expectOne(URL).flush([b('b1', 'Main', 'MAIN'), b('b2', 'North', 'NTH')]);
    fixture.detectChanges();
    const options = Array.from(el.querySelectorAll('option')).map((o) => o.textContent?.trim());
    expect(options).toEqual(['All branches', 'Main (MAIN)', 'North (NTH)']);
  });

  it('writes the chosen branch id back to the model and null for "All branches"', () => {
    const { fixture, http, el } = mount();
    http.expectOne(URL).flush([b('b1', 'Main', 'MAIN'), b('b2', 'North', 'NTH')]);
    fixture.detectChanges();
    const sel = el.querySelector('select')!;
    sel.value = 'b2';
    sel.dispatchEvent(new Event('change'));
    expect(fixture.componentInstance.picked()).toBe('b2');
    sel.value = '';
    sel.dispatchEvent(new Event('change'));
    expect(fixture.componentInstance.picked()).toBeNull();
  });

  it('reloads the branches when the context version changes (branch switch)', () => {
    const { fixture, http, version, el } = mount();
    http.expectOne(URL).flush([b('b1', 'Main', 'MAIN'), b('b2', 'North', 'NTH')]);
    fixture.detectChanges();
    expect(el.querySelector('select')).not.toBeNull();
    version.set(1);
    fixture.detectChanges();
    http.expectOne(URL).flush([b('b1', 'Main', 'MAIN')]);
    fixture.detectChanges();
    expect(el.querySelector('select')).toBeNull();
  });
});
