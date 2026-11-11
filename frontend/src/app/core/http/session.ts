import { Router } from '@angular/router';
import { AuthService } from '../auth/auth.service';

export type LogoutReason = 'expired' | 'revoked';

/** Ends the local session (idempotent; the auth interceptor is the single caller) and sends the user to the login page with a reason (D4/D5). */
export function endSession(auth: AuthService, router: Router, reason: LogoutReason): void {
  if (!auth.isAuthenticated()) return; // already ended (e.g. several queued requests failing on the same refresh)
  auth.logout();
  void router.navigate(['/login'], { queryParams: { reason } });
}
