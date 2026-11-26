import { provideHttpClient } from '@angular/common/http';
import { HttpErrorResponse } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { provideScopeStubs } from '../../shared/scope/scope-testing';
import { GstSummaryReport } from './gst-summary-report/gst-summary-report';
import { HsnSummaryReport } from './hsn-summary-report/hsn-summary-report';
import type { GstSummary, HsnSummary, VatSummary } from './reports.models';
import { ReportsService } from './reports.service';
import { currentMonthRange, periodError } from './tax-report-common/tax-report-common';
import { VatSummaryReport } from './vat-summary-report/vat-summary-report';

const amounts = (taxable: number) => ({ taxableValue: taxable, cgst: 0, sgstUtgst: 0, igst: 0, cess: 0, legacyTax: 0, totalTax: 0 });
const gst = (over: Partial<GstSummary> = {}): GstSummary => ({
  dateFrom: '2026-11-01', dateTo: '2026-11-25', branchId: null, currencyCode: 'INR',
  rows: [
    { section: 'Outward', gstin: '32AAA', placeOfSupplyStateId: 's', placeOfSupplyStateName: 'Kerala', placeOfSupplyStateCode: '32', supplyType: 'IntraState', treatment: 'Taxable', ratePercent: 18, isLegacy: false, taxableValue: 1000, cgst: 90, sgstUtgst: 90, igst: 0, cess: 0, legacyTax: 0, documentCount: 3 },
    { section: 'Outward', gstin: '32AAA', placeOfSupplyStateId: null, placeOfSupplyStateName: null, placeOfSupplyStateCode: null, supplyType: null, treatment: null, ratePercent: 5, isLegacy: true, taxableValue: 200, cgst: 0, sgstUtgst: 0, igst: 0, cess: 0, legacyTax: 10, documentCount: 1 },
  ],
  blocks: [{ gstin: '32AAA', sections: [{ section: 'Outward', amounts: amounts(1200), documentCount: 4 }], outwardNet: amounts(1200), inwardNet: amounts(0) }],
  limitations: [{ code: 'NoPartyGstin', message: 'Every outward supply is shown as B2C.' }],
  ...over,
});

function mount<T>(cmp: new () => T, service: Partial<ReportsService>) {
  TestBed.configureTestingModule({ providers: [...provideScopeStubs(), { provide: ReportsService, useValue: service }] });
  const fixture = TestBed.createComponent(cmp);
  fixture.detectChanges();
  return { fixture, c: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
}
const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`);

describe('tax report service', () => {
  it('sends the period, branch and direction (and the company scope when on)', () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    const s = TestBed.inject(ReportsService);
    s.gstSummary({ dateFrom: '2026-11-01', dateTo: '2026-11-30', branchId: 'b1' }).subscribe();
    const a = http.expectOne((r) => r.url === `${environment.apiBaseUrl}/reports/tax/gst-summary`);
    expect(a.request.params.get('dateFrom')).toBe('2026-11-01');
    expect(a.request.params.get('dateTo')).toBe('2026-11-30');
    expect(a.request.params.get('branchId')).toBe('b1');
    a.flush({});
    s.hsnSummary({ dateFrom: '2026-11-01', dateTo: '2026-11-30', direction: 'Purchases' }).subscribe();
    const b = http.expectOne((r) => r.url.endsWith('/reports/tax/hsn-summary'));
    expect(b.request.params.get('direction')).toBe('Purchases');
    expect(b.request.params.has('branchId')).toBe(false);
    b.flush({});
    s.vatSummary({ dateFrom: '2026-11-01', dateTo: '2026-11-30' }).subscribe();
    http.expectOne((r) => r.url.endsWith('/reports/tax/vat-summary')).flush({});
    http.verify();
  });
});

describe('period helpers', () => {
  it('defaults to the current month and validates', () => {
    expect(currentMonthRange(new Date(2026, 10, 25))).toEqual({ from: '2026-11-01', to: '2026-11-25' });
    expect(periodError('', '2026-11-01')).not.toBeNull();
    expect(periodError('2026-11-05', '2026-11-01')).not.toBeNull();
    expect(periodError('2025-01-01', '2026-11-01')).toMatch(/366/);
    expect(periodError('2026-11-01', '2026-11-30')).toBeNull();
  });
});

describe('GstSummaryReport', () => {
  it('requests the current month on open and renders the banner, rows, legacy label and nets', () => {
    const gstSummary = vi.fn(() => of(gst()));
    const { el } = mount(GstSummaryReport, { gstSummary });
    const month = currentMonthRange();
    expect(gstSummary).toHaveBeenCalledWith({ dateFrom: month.from, dateTo: month.to, branchId: null });
    expect(q(el, 'limitation-NoPartyGstin')?.textContent).toContain('B2C');
    expect(el.querySelectorAll('[data-testid="gst-row"]')).toHaveLength(2);
    expect(q(el, 'legacy-label')?.textContent).toContain('Legacy (unsplit)');
    expect(q(el, 'legacy-label')?.getAttribute('title')).toBeTruthy();
    expect(q(el, 'outward-net')?.textContent).toContain('1,200.00');
    expect(el.querySelector('.tr-scroll')).not.toBeNull();
  });

  it('reloads with the new dates and shows a 400 dateFrom error', () => {
    const gstSummary = vi.fn()
      .mockReturnValueOnce(of(gst()))
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { dateFrom: ['The period is too long.'] } } })));
    const { fixture, c, el } = mount(GstSummaryReport, { gstSummary });
    c.dateFrom.set('2026-10-01');
    c.load();
    fixture.detectChanges();
    expect(gstSummary).toHaveBeenLastCalledWith(expect.objectContaining({ dateFrom: '2026-10-01' }));
    expect(q(el, 'report-error')?.textContent).toContain('The period is too long.');
  });

  it('does not call the server for an invalid period', () => {
    const gstSummary = vi.fn(() => of(gst()));
    const { fixture, c, el } = mount(GstSummaryReport, { gstSummary });
    gstSummary.mockClear();
    c.dateFrom.set('2024-01-01');
    c.load();
    fixture.detectChanges();
    expect(gstSummary).not.toHaveBeenCalled();
    expect(q(el, 'report-error')?.textContent).toMatch(/366/);
  });

  it('builds CSV rows with raw numbers and the legacy label', () => {
    const { c } = mount(GstSummaryReport, { gstSummary: () => of(gst()) });
    const rows = c.csvRows();
    expect(rows[0].slice(0, 2)).toEqual(['Outward supplies (sales)', '32AAA']);
    expect(rows[0][7]).toBe(1000);
    expect(rows[1][5]).toBe('Legacy (unsplit)');
    expect(rows.some((r) => r[0] === 'Net outward')).toBe(true);
  });
});

const hsn = (direction: 'Sales' | 'Purchases' = 'Sales'): HsnSummary => ({
  dateFrom: '2026-11-01', dateTo: '2026-11-25', branchId: null, direction, currencyCode: 'INR',
  rows: [
    { hsnCode: '1006', unitId: 'u', unitCode: 'kg', uqc: 'KGS', decimalPlaces: 3, ratePercent: 5, baseQty: 12.5, taxableValue: 500, cgst: 12.5, sgstUtgst: 12.5, igst: 0, cess: 0, legacyTax: 0, totalValue: 525 },
    { hsnCode: null, unitId: 'p', unitCode: 'pc', uqc: 'NOS', decimalPlaces: 0, ratePercent: 12, baseQty: 4, taxableValue: 100, cgst: 0, sgstUtgst: 0, igst: 0, cess: 0, legacyTax: 12, totalValue: 112 },
  ],
  totals: { taxableValue: 600, totalTax: 37, totalValue: 637 },
  limitations: [{ code: 'HsnNotRecorded', message: 'Some lines predate HSN snapshots.' }],
});

describe('HsnSummaryReport', () => {
  it('shows UQC, precision-aware quantity, the "HSN not recorded" label, totals and the banner', () => {
    const { el } = mount(HsnSummaryReport, { hsnSummary: () => of(hsn()) });
    const rows = el.querySelectorAll('[data-testid="hsn-row"]');
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('KGS');
    expect(rows[0].textContent).toContain('12.500');
    expect(rows[1].textContent).toContain('NOS4');
    expect(q(el, 'hsn-missing')?.textContent).toContain('HSN not recorded');
    expect(q(el, 'hsn-totals')?.textContent).toContain('637.00');
    expect(q(el, 'limitation-HsnNotRecorded')).not.toBeNull();
  });

  it('toggles the direction and reloads', () => {
    const hsnSummary = vi.fn((q: { direction: 'Sales' | 'Purchases' }) => of(hsn(q.direction)));
    const { fixture, el } = mount(HsnSummaryReport, { hsnSummary });
    expect(hsnSummary).toHaveBeenLastCalledWith(expect.objectContaining({ direction: 'Sales' }));
    (q(el, 'dir-Purchases') as HTMLElement).click();
    fixture.detectChanges();
    expect(hsnSummary).toHaveBeenLastCalledWith(expect.objectContaining({ direction: 'Purchases' }));
    expect(hsnSummary).toHaveBeenCalledTimes(2);
  });

  it('CSV: not-recorded label, escaped values', () => {
    const { c } = mount(HsnSummaryReport, { hsnSummary: () => of(hsn()) });
    const rows = c.csvRows();
    expect(rows[1][0]).toBe('HSN not recorded');
    expect(rows[0][2]).toBe('12.500');
    expect(rows[0][4]).toBe(500);
  });
});

const vat: VatSummary = {
  dateFrom: '2026-11-01', dateTo: '2026-11-25', branchId: null, currencyCode: 'AED',
  rows: [
    { section: 'Output', category: 'StandardRated', regionStateId: 'd', regionName: 'Dubai', ratePercent: 5, taxableValue: 1000, vatAmount: 50, documentCount: 2 },
    { section: 'Output', category: 'Legacy', regionStateId: null, regionName: null, ratePercent: 5, taxableValue: 100, vatAmount: 5, documentCount: 1 },
    { section: 'Input', category: 'Exempt', regionStateId: null, regionName: null, ratePercent: 0, taxableValue: 80, vatAmount: 0, documentCount: 1 },
  ],
  sections: [{ section: 'Output', taxableValue: 1100, vatAmount: 55, documentCount: 3 }],
  outputVat: { taxableValue: 1100, vatAmount: 55 }, inputVat: { taxableValue: 80, vatAmount: 0 },
  limitations: [{ code: 'NoPartyTaxRegistration', message: 'No B2B/B2C split.' }],
};

describe('VatSummaryReport', () => {
  it('renders sections, the legacy label, output and input VAT and the banner', () => {
    const { el } = mount(VatSummaryReport, { vatSummary: () => of(vat) });
    expect(el.querySelectorAll('[data-testid="vat-row"]')).toHaveLength(3);
    expect(q(el, 'legacy-label')?.textContent).toContain('Legacy (unsplit)');
    expect(q(el, 'output-vat')?.textContent).toContain('55.00');
    expect(q(el, 'input-vat')).not.toBeNull();
    expect(q(el, 'limitation-NoPartyTaxRegistration')).not.toBeNull();
  });
  it('CSV rows carry raw numbers and the net rows', () => {
    const { c } = mount(VatSummaryReport, { vatSummary: () => of(vat) });
    const rows = c.csvRows();
    expect(rows[0]).toEqual(['Output VAT (sales)', 'Standard rated', 'Dubai', 5, 1000, 50, 2]);
    expect(rows[1][1]).toBe('Legacy (unsplit)');
    expect(rows.at(-2)?.[5]).toBe(55);
  });
});
