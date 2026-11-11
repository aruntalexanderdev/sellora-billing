import type { ApiProblem } from '../../core/auth/auth.models';
import { problemMessage, toProblem } from '../../core/http/api-errors';

/**
 * Message for a failed status/reassignment call. Domain refusals (e.g. the InUse check: "still holds stock or has open
 * transfers") arrive as field errors; show the server text verbatim rather than a generic title.
 */
export function refusalMessage(error: unknown, fallback = 'Could not complete the action.'): string {
  const problem: ApiProblem | null = toProblem(error);
  const first = Object.values(problem?.errors ?? {}).find((m) => m?.length)?.[0];
  if (first) return problem?.traceId ? `${first} (Ref: ${problem.traceId})` : first;
  return problemMessage(problem, fallback);
}

/** D14: the server's message for a rejected status `reason` (400 field `reason`), or null when the failure is something else. */
export function reasonFieldError(error: unknown): string | null {
  return toProblem(error)?.errors?.['reason']?.[0] ?? null;
}

/** Trimmed text, or null when blank (optional API fields are nullable, never empty strings). */
export function blankToNull(value: string | null | undefined): string | null {
  const t = (value ?? '').trim();
  return t.length === 0 ? null : t;
}
