import { HttpErrorResponse } from '@angular/common/http';
import { AbstractControl, FormGroup } from '@angular/forms';
import type { ApiProblem } from '../auth/auth.models';

/** Readable messages for server problem `code`s (arch D5 / Phase 11 design §2). */
export const PROBLEM_MESSAGES: Record<string, string> = {
  // auth / context
  invalid_credentials: 'Invalid username/email or password.',
  account_disabled: 'Your account has been disabled. Contact an administrator.',
  no_company_access: 'Your account is not a member of any active company. Contact an administrator.',
  company_access_denied: 'You do not have access to that company.',
  no_branch_access: 'You have not been assigned to any branch. Contact an administrator.',
  branch_required: 'You have not been assigned to any branch. Contact an administrator.',
  branch_header_invalid: 'The selected branch is not valid. Your branch selection was reset.',
  branch_access_denied: 'You do not have access to that branch, or to the location you selected.',
  company_context_invalid: 'Your access to this company has changed. Please sign in again.',
  invalid_refresh_token: 'Your session has expired. Please sign in again.',
  company_scope_denied: 'Company view is not available to you for this page. Switch back to your active branch.',
  // roles
  role_management_forbidden: 'You are not allowed to manage this role.',
  role_assignment_forbidden: 'You are not allowed to assign this role.',
  role_escalation_forbidden: 'You cannot grant permissions you do not hold yourself.',
  self_role_change_forbidden: 'You cannot change your own roles.',
  administrator_assignment_forbidden: 'You are not allowed to grant or remove the Administrator role.',
  system_role_protected: 'This is a system role and cannot be changed.',
  last_administrator_protected: 'The last active administrator cannot be removed.',
  reference_data_platform_managed: 'This reference data is managed by the platform administrator and cannot be changed here.',
  // governance
  self_governance_change_forbidden: 'You cannot change your own governance membership.',
  governance_delegation_exceeded: 'You can only grant governance permissions that you hold yourself.',
  governance_member_protected: 'This governance member is protected and cannot be changed.',
  platform_account_protected: 'Platform accounts cannot be changed from the company.',
  governance_permission_required: 'You need a governance permission to do that.',
  last_owner_protected: 'The last owner cannot be removed or changed.',
  shared_account_protected: 'This account is shared with other companies and cannot be changed from here.',
  // access
  access_management_forbidden: 'You are not allowed to manage this user\'s access.',
  self_access_change_forbidden: 'You cannot change your own access.',
  // platform
  platform_admin_required: 'This account is not a platform administrator.',
  platform_session_required: 'Your platform session has ended. Please sign in to the platform console again.',
  company_creation_disabled: 'Company creation is disabled on this platform.',
};

export const CONFLICT_MESSAGE = 'This record was changed elsewhere. Reload and try again.';
export const GENERIC_MESSAGE = 'Something went wrong. Please try again.';
export const NETWORK_MESSAGE = 'Could not reach the server. Check your connection and try again.';
export const FORBIDDEN_MESSAGE = 'You do not have permission to do that.';

export const LOGIN_REASON_MESSAGES: Record<string, string> = {
  expired: 'Your session has expired. Please sign in again.',
  revoked: 'Your access to this company has changed. Please sign in again.',
};

/** The ProblemDetails body of an HTTP error (or the problem itself), or null. */
export function toProblem(error: unknown): ApiProblem | null {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as ApiProblem | string | null;
    const problem = body && typeof body === 'object' ? { ...body } : {};
    problem.status ??= error.status;
    return error.status === 0 ? null : problem;
  }
  if (error && typeof error === 'object') return error as ApiProblem;
  return null;
}

/** Human-readable message for a problem, with the server trace id appended for support. `null` = network failure. */
export function problemMessage(problem: ApiProblem | null | undefined, fallback = GENERIC_MESSAGE): string {
  if (!problem) return NETWORK_MESSAGE;
  let text = (problem.code && PROBLEM_MESSAGES[problem.code]) || '';
  if (!text) {
    if (problem.status === 409) text = CONFLICT_MESSAGE;
    else if (problem.status === 403 && !problem.title) text = FORBIDDEN_MESSAGE;
    else text = problem.title || problem.detail || fallback;
  }
  return problem.traceId ? `${text} (Ref: ${problem.traceId})` : text;
}

function lowerFirst(s: string): string {
  return s ? s[0].toLowerCase() + s.slice(1) : s;
}

function findControl(form: FormGroup, field: string, fieldMap?: Record<string, string>): AbstractControl | null {
  const mapped = fieldMap?.[field];
  if (mapped) return form.get(mapped);
  // 'Lines[0].Quantity' -> 'lines.0.quantity'
  const path = field
    .replace(/\[(\d+)\]/g, '.$1')
    .split('.')
    .map(lowerFirst)
    .join('.');
  return form.get(path) ?? form.get(field);
}

/**
 * Maps `problem.errors[field]` onto the matching form controls (`server` error + touched).
 * Returns a banner message when something could not be mapped (or the problem has no field errors), else null.
 */
export function applyProblem(form: FormGroup, problem: ApiProblem | null | undefined, fieldMap?: Record<string, string>): string | null {
  const entries = Object.entries(problem?.errors ?? {});
  if (entries.length === 0) return problemMessage(problem);

  const unmatched: string[] = [];
  for (const [field, messages] of entries) {
    const control = findControl(form, field, fieldMap);
    const text = messages.join(' ');
    if (control) {
      control.setErrors({ ...(control.errors ?? {}), server: text });
      control.markAsTouched();
    } else {
      unmatched.push(text);
    }
  }
  if (unmatched.length === 0) return null;
  const text = unmatched.join(' ');
  return problem?.traceId ? `${text} (Ref: ${problem.traceId})` : text;
}
