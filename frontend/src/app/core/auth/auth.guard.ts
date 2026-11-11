import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { ContextService } from '../context/context.service';
import type { GuardRequirement } from '../context/context.models';
import { BranchPickerService } from '../../shared/ui/branch-picker/branch-picker.service';
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

/**
 * D13: routes that create or change documents carry `data.requiresBranch`. In All-branches mode (no X-Branch-Id, so a
 * write would silently land on the primary branch) the user must pick a branch first; picking one switches the working
 * branch and re-runs the navigation, cancelling the picker cancels the navigation (on the initial navigation it lands on /dashboard instead of a blank page).
 */
export const requiresBranchGuard: CanActivateFn = async (route, state) => {
  if (route.data?.['requiresBranch'] !== true) return true;
  const auth = inject(AuthService);
  const router = inject(Router);
  const context = inject(ContextService);
  const picker = inject(BranchPickerService);

  if (!auth.isAuthenticated()) return true; // the auth guard owns this
  if ((await context.whenSettled()) !== 'ready' || !context.isAllBranches()) return true;
  // Let the permission guard deny first (it runs alongside this one): do not prompt for a screen the user cannot open.
  for (const guard of route.routeConfig?.canActivate ?? []) {
    const req = guard as unknown as GuardRequirement;
    if (req.permissionCode && !context.hasPermission(req.permissionCode)) return true;
    if (req.permissionAnyOf?.length && !req.permissionAnyOf.some((c) => context.hasPermission(c))) return true;
  }

  const branchId = await picker.pick([...context.allowedBranches()]);
  // Initial navigation (deep link / reload): cancelling must not leave a blank page, so land on the dashboard.
  const cancelled = () => (router.navigated ? false : router.createUrlTree(['/dashboard']));
  if (!branchId) return cancelled();
  if (!(await context.switchBranch(branchId, { reroute: false }))) return cancelled();
  return router.parseUrl(state.url); // re-run every guard with the branch-scoped permissions
};
