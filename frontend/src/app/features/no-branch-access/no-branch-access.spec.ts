import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { signal } from '@angular/core';
import { AuthService } from '../../core/auth/auth.service';
import { user } from '../../core/testing/auth-fixtures';
import { NoBranchAccess } from './no-branch-access';

function mount(current: ReturnType<typeof user> | null) {
  const logout = vi.fn();
  TestBed.configureTestingModule({
    imports: [NoBranchAccess],
    providers: [provideRouter([]), { provide: AuthService, useValue: { currentUser: signal(current), logout } }],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(NoBranchAccess);
  fixture.detectChanges();
  return { el: fixture.nativeElement as HTMLElement, logout, navigate };
}

describe('NoBranchAccess page', () => {
  it('explains the missing allocation with the user and company name', () => {
    const { el } = mount(user());
    expect(el.querySelector('.nb-title')?.textContent).toContain('not been assigned to a branch');
    expect(el.querySelector('.nb-body')?.textContent).toContain('Asha Rao');
    expect(el.querySelector('.nb-body')?.textContent).toContain('Sellora Mart');
    expect(el.querySelector('.nb-body')?.textContent).toContain('company administrator');
  });

  it('falls back to a generic message without a session', () => {
    const { el } = mount(null);
    expect(el.querySelector('.nb-body')?.textContent).toContain('Your account has no branch allocation yet.');
  });

  it('sign out clears the session and goes to /login', () => {
    const { el, logout, navigate } = mount(user());
    el.querySelector<HTMLButtonElement>('button')!.click();
    expect(logout).toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith('/login');
  });
});
