import { HttpInterceptorFn } from '@angular/common/http';
import { apiPath } from './api-url';

export const CORRELATION_HEADER = 'X-Correlation-Id';

function newId(): string {
  const c = globalThis.crypto;
  if (c?.randomUUID) return c.randomUUID();
  // Fallback (insecure contexts): RFC 4122 v4 from Math.random; only used for log correlation.
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (ch) => {
    const r = (Math.random() * 16) | 0;
    return (ch === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

/** D6: a client-generated UUID per API request; the backend adopts it as the trace id. */
export const correlationInterceptor: HttpInterceptorFn = (req, next) => {
  if (apiPath(req.url) === null || req.headers.has(CORRELATION_HEADER)) return next(req);
  return next(req.clone({ setHeaders: { [CORRELATION_HEADER]: newId() } }));
};
