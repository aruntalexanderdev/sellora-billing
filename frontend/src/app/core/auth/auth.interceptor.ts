import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { apiPath, isAuthEndpoint, isPlatformEndpoint } from '../http/api-url';
import { endSession } from '../http/session';
import type { ApiProblem } from './auth.models';
import { AuthService } from './auth.service';

function codeOf(error: HttpErrorResponse): string | undefined {
  return (error.error as ApiProblem | null)?.code;
}

const withToken = <T>(req: HttpRequest<T>, token: string) => req.clone({ setHeaders: { Authorization: `Bearer ${token}` } });

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  // Only company API calls carry the company token (never auth endpoints or the platform console).
  const path = apiPath(req.url);
  if (path === null || isAuthEndpoint(path) || isPlatformEndpoint(path)) return next(req);

  const sentToken = auth.accessToken();
  const authedReq = sentToken ? withToken(req, sentToken) : req;

  return next(authedReq).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401 || !sentToken) return throwError(() => error);
      const code = codeOf(error);
      if (!auth.getRefreshToken()) {
        // Nothing to refresh with: the session cannot continue.
        endSession(auth, router, code === 'company_context_invalid' ? 'revoked' : 'expired');
        return throwError(() => error);
      }
      if (code === 'invalid_refresh_token') {
        // switch-company with a dead refresh token. This interceptor is the single owner of ending the session.
        endSession(auth, router, 'expired');
        return throwError(() => error);
      }

      // Someone (another request, or another tab via storage sync) already rotated the token: just retry with it.
      const current = auth.accessToken();
      const retryWith = (token: string) =>
        next(withToken(req, token)).pipe(
          catchError((retryError: unknown) => {
            if (retryError instanceof HttpErrorResponse && retryError.status === 401 && codeOf(retryError) === 'company_context_invalid') {
              endSession(auth, router, 'revoked');
            }
            return throwError(() => retryError);
          }),
        );

      if (current && current !== sentToken) return retryWith(current);

      // D4: single-flight refresh (shared by every queued 401), then retry once.
      return auth.refresh().pipe(
        catchError((refreshError: unknown) => {
          // 403 no_branch_access keeps the session: the error interceptor routes to the no-branch page.
          const noBranch = refreshError instanceof HttpErrorResponse && refreshError.status === 403 && codeOf(refreshError) === 'no_branch_access';
          if (!noBranch) endSession(auth, router, code === 'company_context_invalid' ? 'revoked' : 'expired');
          return throwError(() => refreshError);
        }),
        switchMap((res) => retryWith(res.accessToken)),
      );
    }),
  );
};
