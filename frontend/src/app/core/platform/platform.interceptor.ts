import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { apiPath, isPlatformEndpoint } from '../http/api-url';
import { PlatformAuthService } from './platform-auth.service';

const BRANCH_HEADER = 'X-Branch-Id';
const PLATFORM_LOGIN = '/auth/platform/login';

/**
 * D7: the only place a platform token is attached, and only to /platform/* calls. Never sends X-Branch-Id.
 * The platform login itself is anonymous. A 401 on a platform call ends the platform session and goes to /platform/login
 * (the company session is untouched). Registered right after the correlation interceptor.
 */
export const platformInterceptor: HttpInterceptorFn = (req, next) => {
  const path = apiPath(req.url);
  if (path === null || !isPlatformEndpoint(path)) return next(req);

  const platform = inject(PlatformAuthService);
  const router = inject(Router);

  let outgoing = req.headers.has(BRANCH_HEADER) ? req.clone({ headers: req.headers.delete(BRANCH_HEADER) }) : req;
  if (path !== PLATFORM_LOGIN) {
    const token = platform.accessToken();
    if (token) outgoing = outgoing.clone({ setHeaders: { Authorization: `Bearer ${token}` } });
  }

  return next(outgoing).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401 && path !== PLATFORM_LOGIN) {
        platform.clear();
        void router.navigate(['/platform/login'], { queryParams: { reason: 'expired' } });
      }
      return throwError(() => error);
    }),
  );
};
