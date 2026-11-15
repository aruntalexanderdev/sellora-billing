import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { ContextService } from '../context/context.service';
import type { GuardRequirement } from '../context/context.models';
import { AuthService } from './auth.service';

export const authGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const context = inject(ContextService);

  if (!auth.isAuthenticated()) {
    return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  }
  // Users without any branch allocation only ever see the no-branch page.
  if ((await context.whenSettled()) === 'noBranch') return router.createUrlTree(['/no-branch-access']);
  return true;
};

/** Accepts one code, or a list meaning any-of. */
export function permissionGuard(permission: string | readonly string[]): CanActivateFn {
  const codes = typeof permission === 'string' ? [permission] : permission;
  const guard: CanActivateFn = async () => {
    const auth = inject(AuthService);
    const router = inject(Router);
    const context = inject(ContextService);

    if (!auth.isAuthenticated()) {
      return router.createUrlTree(['/login']);
    }
    const status = await context.whenSettled();
    if (status === 'noBranch') return router.createUrlTree(['/no-branch-access']);
    if (codes.some((c) => context.hasPermission(c))) {
      return true;
    }
    return router.createUrlTree(['/permission-denied'], { queryParams: { permission: codes.join(',') } });
  };
  const req = guard as unknown as GuardRequirement;
  if (typeof permission === 'string') req.permissionCode = permission;
  else req.permissionAnyOf = permission;
  return guard;
}

/** Governance screens: requires an active governance membership (optionally holding `governanceCode`). */
export function governanceGuard(governanceCode?: string): CanActivateFn {
  const guard: CanActivateFn = async () => {
    const auth = inject(AuthService);
    const router = inject(Router);
    const context = inject(ContextService);

    if (!auth.isAuthenticated()) {
      return router.createUrlTree(['/login']);
    }
    const status = await context.whenSettled();
    if (status === 'noBranch') return router.createUrlTree(['/no-branch-access']);
    if (context.hasGovernance(governanceCode)) return true;
    return router.createUrlTree(['/permission-denied'], { queryParams: { permission: governanceCode ?? 'governance' } });
  };
  const req = guard as unknown as GuardRequirement;
  req.requiresGovernance = true;
  req.governanceCode = governanceCode ?? null;
  return guard;
}
