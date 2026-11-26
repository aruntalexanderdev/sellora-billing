import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';
import { PREVIEW_DEBOUNCE_MS, TaxPreviewService, planKey, previewStates, type PreviewPlan, type PreviewState } from './tax-preview.service';
import type { TaxDocumentPreview, TaxDocumentPreviewRequest } from './tax-preview.models';

const request = (qty: number): TaxDocumentPreviewRequest => ({
  direction: 'Purchase', partyId: 's1', date: '2026-11-01', ratesIncludeTax: false, billDiscountPercent: null, billDiscountAmount: null, billCharges: 0,
  lines: [{ productId: 'p', productUnitId: 'u', qty, unitPrice: 10, discount: 0, otherCharges: 0 }],
});
const plan = (qty: number): PreviewPlan => ({ request: request(qty), rows: [0] });
const result = (total: number) => ({ grandTotal: total, lines: [], warnings: [] }) as unknown as TaxDocumentPreview;

describe('TaxPreviewService', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] }));

  it('POSTs the request body to /tax/preview', () => {
    const http = TestBed.inject(HttpTestingController);
    TestBed.inject(TaxPreviewService).preview(request(3)).subscribe();
    const req = http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/tax/preview'));
    expect(req.request.body).toEqual(request(3));
    expect(req.request.body.direction).toBe('Purchase');
    expect(req.request.body.lines[0]).toEqual({ productId: 'p', productUnitId: 'u', qty: 3, unitPrice: 10, discount: 0, otherCharges: 0 });
  });
});

describe('previewStates', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('debounces keystrokes into one call with the last plan', () => {
    const plans = new Subject<PreviewPlan | null>();
    const call = vi.fn((r: TaxDocumentPreviewRequest) => of(result(r.lines[0].qty)));
    const states: PreviewState[] = [];
    previewStates(plans, call).subscribe((s) => states.push(s));

    plans.next(plan(1));
    vi.advanceTimersByTime(100);
    plans.next(plan(2));
    vi.advanceTimersByTime(100);
    plans.next(plan(3));
    expect(call).not.toHaveBeenCalled();
    vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);

    expect(call).toHaveBeenCalledTimes(1);
    expect(call.mock.calls[0][0].lines[0].qty).toBe(3);
    expect(states).toHaveLength(1);
    expect(states[0].kind).toBe('ok');
  });

  it('cancels the call in flight when a newer plan arrives (a stale response never lands)', () => {
    const plans = new Subject<PreviewPlan | null>();
    const slow = new Subject<TaxDocumentPreview>();
    let slowUnsubscribed = false;
    const call = (r: TaxDocumentPreviewRequest): Observable<TaxDocumentPreview> =>
      r.lines[0].qty === 1
        ? new Observable<TaxDocumentPreview>((sub) => {
            const inner = slow.subscribe((v) => sub.next(v));
            return () => {
              slowUnsubscribed = true;
              inner.unsubscribe();
            };
          })
        : of(result(2));
    const states: PreviewState[] = [];
    previewStates(plans, call).subscribe((s) => states.push(s));

    plans.next(plan(1));
    vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);
    plans.next(plan(2));
    vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);
    slow.next(result(1));

    expect(slowUnsubscribed).toBe(true);
    expect(states.map((s) => (s.kind === 'ok' ? s.preview.grandTotal : s.kind))).toEqual([2]);
  });

  it('skips an identical plan', () => {
    const plans = new Subject<PreviewPlan | null>();
    const call = vi.fn(() => of(result(1)));
    previewStates(plans, call).subscribe();
    plans.next(plan(1));
    vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);
    plans.next(plan(1));
    vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);
    expect(call).toHaveBeenCalledTimes(1);
  });

  it('yields idle for a null plan and an error state (stream stays alive) for a failing call', () => {
    const plans = new Subject<PreviewPlan | null>();
    const states: PreviewState[] = [];
    let fail = true;
    previewStates(plans, () => (fail ? throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { 'lines[0].rate': ['Too many decimals.'] } } })) : of(result(5)))).subscribe((s) => states.push(s));

    plans.next(plan(1));
    vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);
    fail = false;
    plans.next(plan(2));
    vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);
    plans.next(null);
    vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);

    expect(states.map((s) => s.kind)).toEqual(['error', 'ok', 'idle']);
    const err = states[0];
    expect(err.kind === 'error' && err.problem?.errors?.['lines[0].rate']).toEqual(['Too many decimals.']);
  });

  it('planKey is stable for equal plans and empty for null', () => {
    expect(planKey(plan(1))).toBe(planKey(plan(1)));
    expect(planKey(plan(1))).not.toBe(planKey(plan(2)));
    expect(planKey(null)).toBe('');
  });
});
