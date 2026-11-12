import { HttpErrorResponse } from '@angular/common/http';
import { FormBuilder } from '@angular/forms';
import { CONFLICT_MESSAGE, FORBIDDEN_MESSAGE, GENERIC_MESSAGE, NETWORK_MESSAGE, PROBLEM_MESSAGES, applyProblem, problemMessage, toProblem } from './api-errors';

const ROLE_CODES = [
  'role_management_forbidden', 'role_assignment_forbidden', 'role_escalation_forbidden', 'self_role_change_forbidden',
  'administrator_assignment_forbidden', 'system_role_protected', 'last_administrator_protected', 'reference_data_platform_managed',
];
const GOVERNANCE_CODES = [
  'self_governance_change_forbidden', 'governance_delegation_exceeded', 'governance_member_protected', 'platform_account_protected',
  'governance_permission_required', 'last_owner_protected', 'shared_account_protected',
];
const ACCESS_CODES = ['access_management_forbidden', 'self_access_change_forbidden'];
const PLATFORM_CODES = ['platform_admin_required', 'company_creation_disabled', 'platform_session_required'];
const AUTH_CODES = [
  'account_disabled', 'no_branch_access', 'no_company_access', 'company_access_denied', 'branch_required', 'branch_header_invalid',
  'branch_access_denied', 'company_scope_denied', 'company_context_invalid', 'invalid_refresh_token', 'invalid_credentials',
];

describe('problemMessage', () => {
  it.each([...ROLE_CODES, ...GOVERNANCE_CODES, ...ACCESS_CODES, ...PLATFORM_CODES, ...AUTH_CODES])(
    'has a readable message for %s (instead of the server title)',
    (code) => {
      const text = problemMessage({ code, title: 'raw server title', status: 403 });
      expect(text).toBe(PROBLEM_MESSAGES[code]);
      expect(text).not.toContain('raw server title');
      expect(text.length).toBeGreaterThan(10);
    },
  );

  it('appends the trace id for support', () => {
    expect(problemMessage({ code: 'last_owner_protected', traceId: 'abc-123' })).toBe(`${PROBLEM_MESSAGES['last_owner_protected']} (Ref: abc-123)`);
    expect(problemMessage({ title: 'Boom', traceId: 't1' })).toBe('Boom (Ref: t1)');
  });

  it('uses the title for unknown codes, a conflict hint for 409, a permission hint for a bare 403', () => {
    expect(problemMessage({ code: 'something_new', title: 'Server said so' })).toBe('Server said so');
    expect(problemMessage({ status: 409, title: 'Data conflict' })).toBe(CONFLICT_MESSAGE);
    expect(problemMessage({ status: 403 })).toBe(FORBIDDEN_MESSAGE);
    expect(problemMessage({})).toBe(GENERIC_MESSAGE);
  });

  it('reports a network failure when there is no problem body', () => {
    expect(problemMessage(null)).toBe(NETWORK_MESSAGE);
    expect(toProblem(new HttpErrorResponse({ status: 0 }))).toBeNull();
  });

  it('toProblem reads the ProblemDetails body and the status', () => {
    const p = toProblem(new HttpErrorResponse({ status: 403, error: { code: 'x', traceId: 't' } }));
    expect(p).toEqual({ code: 'x', traceId: 't', status: 403 });
  });
});

describe('applyProblem', () => {
  const fb = new FormBuilder();
  const build = () =>
    fb.nonNullable.group({
      name: [''],
      code: [''],
      lines: fb.array([fb.nonNullable.group({ quantity: [''] })]),
    });

  it('maps PascalCase and indexed field names onto controls and returns null when all matched', () => {
    const form = build();
    const banner = applyProblem(form, { errors: { Name: ['Required.'], 'Lines[0].Quantity': ['Too many decimals.', 'Must be positive.'] } });
    expect(banner).toBeNull();
    expect(form.controls.name.errors).toEqual({ server: 'Required.' });
    expect(form.controls.name.touched).toBe(true);
    expect(form.controls.lines.at(0).controls.quantity.errors).toEqual({ server: 'Too many decimals. Must be positive.' });
  });

  it('honours an explicit fieldMap', () => {
    const form = build();
    applyProblem(form, { errors: { ShortCode: ['Duplicate.'] } }, { ShortCode: 'code' });
    expect(form.controls.code.errors).toEqual({ server: 'Duplicate.' });
  });

  it('returns a banner for unmapped fields (with trace id) and for problems without field errors', () => {
    const form = build();
    expect(applyProblem(form, { errors: { Nope: ['Bad thing.'] }, traceId: 't9' })).toBe('Bad thing. (Ref: t9)');
    expect(applyProblem(form, { code: 'system_role_protected' })).toBe(PROBLEM_MESSAGES['system_role_protected']);
    expect(applyProblem(form, null)).toBe(NETWORK_MESSAGE);
  });

  it('server errors clear when the user edits the control', () => {
    const form = build();
    applyProblem(form, { errors: { name: ['Taken.'] } });
    form.controls.name.setValue('other');
    expect(form.controls.name.errors).toBeNull();
  });
});
