import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ContextService } from '../../../core/context/context.service';
import { UiCompanyViewToggle } from './company-view-toggle';

function setup(companyPermissions: string[]) {
  const companyView = signal(false);
  const ctx = {
    companyPermissions: signal(new Set(companyPermissions)),
    companyViewActive: (code: string) => companyView() && companyPermissions.includes(code),
    setCompanyView: vi.fn((on: boolean) => companyView.set(on)),
  };
  TestBed.configureTestingModule({ imports: [UiCompanyViewToggle], providers: [{ provide: ContextService, useValue: ctx }] });
  const fixture = TestBed.createComponent(UiCompanyViewToggle);
  fixture.componentRef.setInput('permission', 'Reports.Sales');
  fixture.detectChanges();
  return { fixture, ctx, el: fixture.nativeElement as HTMLElement };
}

describe('UiCompanyViewToggle', () => {
  it('is hidden unless the page permission is held company-level', () => {
    expect(setup(['Reports.Stock']).el.querySelector('input')).toBeNull();
    TestBed.resetTestingModule();
    expect(setup([]).el.querySelector('input')).toBeNull();
  });

  it('is offered with company-level permission, off by default, without the indicator', () => {
    const { el } = setup(['Reports.Sales']);
    const input = el.querySelector('input') as HTMLInputElement;
    expect(input.checked).toBe(false);
    expect(el.querySelector('.cv-badge')).toBeNull();
  });

  it('turning it on updates the context, emits changed and shows the "Company view – all branches" indicator', () => {
    const { fixture, ctx, el } = setup(['Reports.Sales']);
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    const input = el.querySelector('input') as HTMLInputElement;
    input.checked = true;
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(ctx.setCompanyView).toHaveBeenCalledWith(true);
    expect(changed).toHaveBeenCalledWith(true);
    expect(el.querySelector('.cv-badge')?.textContent?.trim()).toBe('Company view – all branches');
  });

  it('resets to off when the page is left', () => {
    const { fixture, ctx } = setup(['Reports.Sales']);
    fixture.destroy();
    expect(ctx.setCompanyView).toHaveBeenLastCalledWith(false);
  });
});
