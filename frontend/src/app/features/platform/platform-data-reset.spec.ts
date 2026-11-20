import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { PlatformDataReset } from './platform-data-reset';
import { PlatformLayout } from './platform-layout';
import type { DataResetReport } from './platform.models';
import { PlatformService } from './platform.service';
import { PlatformAuthService } from '../../core/platform/platform-auth.service';

const tick = () => new Promise<void>((r) => setTimeout(r, 0));
const problem = (status: number, body: Record<string, unknown>) =>
  throwError(() => new HttpErrorResponse({ status, error: { status, ...body } }));

const report = (o: Partial<DataResetReport> = {}): DataResetReport => ({
  database: 'sellora_dev', applied: false, message: 'Preview only: nothing was changed.', keptMasters: ['customers', 'suppliers'],
  tables: [
    { area: '1 Masters', table: 'Taxes', rowsBefore: 3, rowsAfter: null, action: 'remove all' },
    { area: '4 Sales', table: 'Customers', rowsBefore: 4, rowsAfter: null, action: 'remove all except the walk-in customer' },
  ],
  ...o,
});
const done = (): DataResetReport => ({
  ...report(), applied: true, message: 'Reset done: business data removed and document numbers reset.', keptMasters: [],
  tables: [{ area: '1 Masters', table: 'Taxes', rowsBefore: 3, rowsAfter: 0, action: 'remove all' }],
});

function mount(extra: Record<string, unknown> = {}) {
  const service = {
    dataResetStatus: vi.fn(() => of({ enabled: true })),
    dataResetPreview: vi.fn(() => of(report())),
    dataReset: vi.fn(() => of(done())),
    ...extra,
  };
  TestBed.configureTestingModule({ imports: [PlatformDataReset], providers: [{ provide: PlatformService, useValue: service }] });
  const fixture = TestBed.createComponent(PlatformDataReset);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const type = (v: string) => {
    const i = q('confirm-input') as HTMLInputElement;
    i.value = v;
    i.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };
  const resetBtn = () => q('reset-button')!.querySelector('button') as HTMLButtonElement;
  return { fixture, el, service, q, type, resetBtn };
}

describe('PlatformDataReset', () => {
  it('shows the switched-off notice and makes no preview call', () => {
    const { q, service } = mount({ dataResetStatus: vi.fn(() => of({ enabled: false })) });
    expect(q('reset-off')?.textContent).toContain('Platform:DataResetEnabled');
    expect(service.dataResetPreview).not.toHaveBeenCalled();
    expect(q('confirm-panel')).toBeNull();
  });

  it('treats a 403 data_reset_disabled status as switched off', () => {
    const { q, service } = mount({ dataResetStatus: vi.fn(() => problem(403, { code: 'data_reset_disabled' })) });
    expect(q('reset-off')).not.toBeNull();
    expect(service.dataResetPreview).not.toHaveBeenCalled();
  });

  it('renders preview rows, kept masters and totals', () => {
    const { q, el } = mount();
    expect(el.querySelectorAll('[data-testid="preview-table"] tbody tr').length).toBe(2);
    expect(q('preview-totals')?.textContent).toContain('2 tables, 7 rows');
    expect(q('kept-masters')?.textContent).toContain('customers, suppliers');
    expect(q('db-name')?.textContent).toContain('sellora_dev');
  });

  it('reloads the preview when an option changes', () => {
    const { q, fixture, service } = mount();
    const box = q('opt-wipeCustomers') as HTMLInputElement;
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(service.dataResetPreview).toHaveBeenCalledTimes(2);
    expect(service.dataResetPreview).toHaveBeenLastCalledWith(expect.objectContaining({ wipeCustomers: true, keepTaxes: false }));
  });

  it('keeps Reset disabled until the exact database name is typed', () => {
    const { type, resetBtn } = mount();
    expect(resetBtn().disabled).toBe(true);
    type('sellora');
    expect(resetBtn().disabled).toBe(true);
    type('sellora_dev');
    expect(resetBtn().disabled).toBe(false);
  });

  it('resets with the typed name and options, then shows the result table', async () => {
    const { q, fixture, service, type, resetBtn, el } = mount();
    const box = q('opt-keepTaxes') as HTMLInputElement;
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    type('sellora_dev');
    resetBtn().click();
    await tick();
    fixture.detectChanges();
    expect(service.dataReset).toHaveBeenCalledWith('sellora_dev', expect.objectContaining({ keepTaxes: true }));
    expect(q('reset-success')?.textContent).toContain('Reset done');
    expect(el.querySelector('[data-testid="result-table"] tbody td:nth-child(4)')?.textContent?.trim()).toBe('0');
    expect(q('confirm-panel')).toBeNull();
  });

  it('shows a 400 confirmDatabase error at the input', async () => {
    const { q, fixture, type, resetBtn } = mount({
      dataReset: vi.fn(() => problem(400, { errors: { confirmDatabase: ['Typed name is not the connected database.'] } })),
    });
    type('sellora_dev');
    resetBtn().click();
    await tick();
    fixture.detectChanges();
    expect(q('confirm-error')?.textContent).toContain('not the connected database');
  });

  it('shows the 409 refusal title', async () => {
    const { q, fixture, type, resetBtn } = mount({
      dataReset: vi.fn(() => problem(409, { code: 'data_reset_refused', title: 'Refused: an import is running.' })),
    });
    type('sellora_dev');
    resetBtn().click();
    await tick();
    fixture.detectChanges();
    expect(q('reset-refused')?.textContent).toContain('Refused: an import is running.');
  });
});

describe('PlatformLayout data reset link', () => {
  function layout(status: unknown) {
    const service = { dataResetStatus: vi.fn(() => status) };
    TestBed.configureTestingModule({
      imports: [PlatformLayout],
      providers: [provideRouter([]), { provide: PlatformService, useValue: service }, { provide: PlatformAuthService, useValue: { user: () => null, clear: vi.fn() } }],
    });
    const f = TestBed.createComponent(PlatformLayout);
    f.detectChanges();
    return f.nativeElement as HTMLElement;
  }

  it('shows the link when enabled', () => {
    expect(layout(of({ enabled: true })).querySelector('[data-testid="nav-data-reset"]')).not.toBeNull();
  });
  it('hides the link when disabled or on error', () => {
    expect(layout(of({ enabled: false })).querySelector('[data-testid="nav-data-reset"]')).toBeNull();
    TestBed.resetTestingModule();
    expect(layout(throwError(() => new HttpErrorResponse({ status: 403 }))).querySelector('[data-testid="nav-data-reset"]')).toBeNull();
  });
});
