import type { ApiProblem } from '../../../core/auth/auth.models';

/** ProblemDetails `errors` keyed by lower-camel field (`ratePercent`, `components[1].ratePercent`), first message each. */
export function problemFields(problem: ApiProblem | undefined): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [field, messages] of Object.entries(problem?.errors ?? {})) {
    if (!messages?.length) continue;
    out[field.charAt(0).toLowerCase() + field.slice(1)] = messages[0];
  }
  return out;
}

/** Message for an unexpected failure with no field errors. */
export function problemTitle(problem: ApiProblem | undefined, fallback = 'Something went wrong.'): string {
  return problem?.title ?? fallback;
}
