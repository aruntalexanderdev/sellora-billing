import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { ContextService } from '../context/context.service';
import { apiPath, isAuthEndpoint, isPlatformEndpoint } from './api-url';

export const BRANCH_HEADER = 'X-Branch-Id';

/** D2: sends the working branch on every API call except login/refresh/logout and /platform/*. */
export const contextInterceptor: HttpInterceptorFn = (req, next) => {
  const path = apiPath(req.url);
  if (path === null || isAuthEndpoint(path) || isPlatformEndpoint(path)) return next(req);

  const branchId = inject(ContextService).workingBranchId();
  return branchId ? next(req.clone({ setHeaders: { [BRANCH_HEADER]: branchId } })) : next(req);
};
