import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { AuthService } from '../../../core/auth/auth.service';
import { LOGIN_REASON_MESSAGES } from '../../../core/http/api-errors';
import { Login } from './login';

function mount(query: Record<string, string> = {}) {
  TestBed.configureTestingModule({
    imports: [Login],
    providers: [
      provideRouter([]),
      { provide: AuthService, useValue: { login: vi.fn() } },
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
    ],
  });
  const fixture = TestBed.createComponent(Login);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('Login ?reason= banner', () => {
  it('shows no banner without a reason', () => {
    expect(mount().querySelector('.error-banner')).toBeNull();
  });

  it.each([
    ['expired', 'Your session has expired. Please sign in again.'],
    ['revoked', 'Your access to this company has changed. Please sign in again.'],
  ])('reason=%s shows its message', (reason, text) => {
    const banner = mount({ reason }).querySelector('.error-banner');
    expect(banner?.textContent).toContain(text);
    expect(LOGIN_REASON_MESSAGES[reason]).toBe(text);
  });

  it('ignores an unknown reason', () => {
    expect(mount({ reason: 'whatever' }).querySelector('.error-banner')).toBeNull();
  });
});
