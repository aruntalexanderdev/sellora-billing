import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { ContextService } from '../context/context.service';
import { ToastService } from '../../shared/ui/toast/toast.service';
import { CONFLICT_MESSAGE, FORBIDDEN_MESSAGE, toProblem } from './api-errors';
import { apiPath, isPlatformEndpoint } from './api-url';
import { PRIMARY_LOAD } from './request-context';

/**
 * D5 side effects for API errors. The error always continues to the caller (features show `problemMessage`).
 * 401 refresh/retry (incl. `company_context_invalid`) lives in the auth interceptor; this one handles the rest.
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const path = apiPath(req.url);
  if (path === null) return next(req);

  const context = inject(ContextService);
  const router = inject(Router);
  const toast = inject(ToastService);

  return next(req).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse) {
        const code = toProblem(error)?.code;
        const status = error.status;
        const isMe = path === '/auth/me'; // the context service handles its own /me failures

        if (isPlatformEndpoint(path)) {
          // D7: the platform console has its own session; an ended session goes to the platform login, never the company one.
          if (status === 401 && path !== '/auth/platform/login') void router.navigate(['/platform/login']);
        } else if (status === 401) {
          // Session end (expired / revoked / invalid_refresh_token) is owned by the auth interceptor.
        } else if (status === 403 && (code === 'branch_required' || code === 'no_branch_access')) {
          // At login / company switch the form shows the message; otherwise the user has no usable branch.
          if (path !== '/auth/login' && path !== '/auth/switch-company') {
            context.markNoBranch();
            void router.navigate(['/no-branch-access']);
          }
        } else if (status === 400 && code === 'branch_header_invalid') {
          if (!isMe) void context.clearSelectionAndRehydrate();
        } else if (status === 403 && code === 'branch_access_denied') {
          if (!isMe) {
            void context.probeBranchAccess().then((outcome) => {
              if (outcome === 'revoked') {
                const name = context.workingBranch()?.name;
                toast.show(`Your access to that branch was removed${name ? `; switched to ${name}` : ''}.`);
              }
              // 'resource': a hidden location; the caller shows the readable branch_access_denied message.
            });
          }
        } else if (status === 403 && code === 'company_scope_denied') {
          // FI-D11: the Company view was refused; fall back to the active branch. The page shows the mapped message.
          context.setCompanyView(false);
        } else if (status === 403 && !code) {
          if (!path.startsWith('/auth/')) {
            void context.resyncPermissions();
            toast.show(FORBIDDEN_MESSAGE);
            // Only the screen's primary load leaves the page; background GETs just toast (see PRIMARY_LOAD).
            if (req.method === 'GET' && req.context.get(PRIMARY_LOAD)) void router.navigate(['/permission-denied']);
          }
        } else if (status === 409) {
          toast.show(CONFLICT_MESSAGE);
        }
      }
      return throwError(() => error);
    }),
  );
};
