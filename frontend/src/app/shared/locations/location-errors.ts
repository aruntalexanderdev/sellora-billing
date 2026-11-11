import type { ApiProblem } from '../../core/auth/auth.models';

export interface SplitLocationError {
  /** Message for the `stockLocationId` field, or null when the server sent none. */
  locationError: string | null;
  /** The problem without the location field error, ready for `applyProblem` / `problemMessage`. */
  rest: ApiProblem | null;
}

/** Pulls the `stockLocationId` field error (any casing) out of a ProblemDetails so forms can show it by the picker. */
export function splitLocationError(problem: ApiProblem | null | undefined): SplitLocationError {
  if (!problem?.errors) return { locationError: null, rest: problem ?? null };
  const rest: Record<string, string[]> = {};
  const found: string[] = [];
  for (const [field, messages] of Object.entries(problem.errors)) {
    if (field.toLowerCase() === 'stocklocationid') found.push(...messages);
    else rest[field] = messages;
  }
  if (found.length === 0) return { locationError: null, rest: problem };
  return { locationError: found.join(' '), rest: { ...problem, errors: rest } };
}
