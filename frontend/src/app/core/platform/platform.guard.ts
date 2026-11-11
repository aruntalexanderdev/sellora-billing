import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { PlatformAuthService } from './platform-auth.service';

/** D7: the platform console needs a live platform session; a company session never qualifies. */
export const platformGuard: CanActivateFn = () => {
  if (inject(PlatformAuthService).isAuthenticated()) return true;
  return inject(Router).createUrlTree(['/platform/login']);
};
