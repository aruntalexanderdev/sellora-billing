import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, debounceTime, distinctUntilChanged, map, of, switchMap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { toProblem } from '../../core/http/api-errors';
import type { ApiProblem } from '../../core/auth/auth.models';
import type { TaxDocumentPreview, TaxDocumentPreviewRequest } from './tax-preview.models';

const URL = `${environment.apiBaseUrl}/tax/preview`;

/** Debounce of the live breakdown while the user types. */
export const PREVIEW_DEBOUNCE_MS = 300;

/** A preview request plus the form row of each request line (blank rows are not sent), so `index` can be mapped back. */
export interface PreviewPlan {
  request: TaxDocumentPreviewRequest;
  rows: number[];
}

/** Outcome of one preview call; a `null` plan = nothing to preview yet. */
export type PreviewState =
  | { kind: 'idle' }
  | { kind: 'ok'; plan: PreviewPlan; preview: TaxDocumentPreview }
  | { kind: 'error'; plan: PreviewPlan; problem: ApiProblem | null };

@Injectable({ providedIn: 'root' })
export class TaxPreviewService {
  private readonly http = inject(HttpClient);

  /** `POST /api/v1/tax/preview`: read-only; the branch is the working branch (X-Branch-Id), as on save. */
  preview(request: TaxDocumentPreviewRequest): Observable<TaxDocumentPreview> {
    return this.http.post<TaxDocumentPreview>(URL, request);
  }
}

/**
 * Turns a stream of preview plans into preview states: debounced, identical requests skipped, and a newer
 * plan cancels the call still in flight (`switchMap`), so a stale response can never overwrite a fresh one.
 * A `null` plan yields `idle` (after the debounce). Errors never terminate the stream.
 */
export function previewStates(
  plans: Observable<PreviewPlan | null>,
  call: (request: TaxDocumentPreviewRequest) => Observable<TaxDocumentPreview>,
  debounceMs: number = PREVIEW_DEBOUNCE_MS,
): Observable<PreviewState> {
  return plans.pipe(
    debounceTime(debounceMs),
    distinctUntilChanged((a, b) => JSON.stringify(a) === JSON.stringify(b)),
    switchMap((plan): Observable<PreviewState> =>
      plan === null
        ? of({ kind: 'idle' })
        : call(plan.request).pipe(
            map((preview): PreviewState => ({ kind: 'ok', plan, preview })),
            catchError((err: unknown) => of<PreviewState>({ kind: 'error', plan, problem: toProblem(err) })),
          ),
    ),
  );
}

/** Stable identity of a plan, to tell whether a state still matches the current form. */
export const planKey = (plan: PreviewPlan | null): string => (plan ? JSON.stringify(plan) : '');
