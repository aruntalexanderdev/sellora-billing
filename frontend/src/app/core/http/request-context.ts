import { HttpContext, HttpContextToken } from '@angular/common/http';

/**
 * Marks a GET as the primary load of the current screen (the data the page cannot render without).
 * Rule (error interceptor): a bare 403 on a GET sends the user to /permission-denied only when the request carries
 * this flag; every other 403 (background refreshes, lookups, secondary panels) is toast-only so the user keeps the page.
 * Usage: `http.get(url, { context: primaryLoad() })`.
 */
export const PRIMARY_LOAD = new HttpContextToken<boolean>(() => false);

export function primaryLoad(context: HttpContext = new HttpContext()): HttpContext {
  return context.set(PRIMARY_LOAD, true);
}
