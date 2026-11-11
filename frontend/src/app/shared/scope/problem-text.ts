import { problemMessage, toProblem } from '../../core/http/api-errors';

/**
 * One-line text for an inline (non-form) error panel: the first server field message when present, otherwise the
 * mapped problem message (code table -> title/detail -> `fallback`); the trace id is appended for support.
 */
export function problemText(error: unknown, fallback: string): string {
  const problem = toProblem(error);
  const first = Object.values(problem?.errors ?? {})[0]?.[0];
  if (problem && first) return problem.traceId ? `${first} (Ref: ${problem.traceId})` : first;
  return problemMessage(problem, fallback);
}

/** Message for a failed detail load; `null` for a 404 so the page shows its own "not found" text. */
export function loadErrorText(error: unknown, fallback: string): string | null {
  return toProblem(error)?.status === 404 ? null : problemText(error, fallback);
}
