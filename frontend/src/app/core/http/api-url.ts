import { environment } from '../../../environments/environment';

/** Path of `url` relative to the API base, or null when the request does not target the API (works for '/api/v1' and 'https://host/api/v1'). */
export function apiPath(url: string): string | null {
  const base = environment.apiBaseUrl;
  if (!url.startsWith(base)) return null;
  const rest = url.slice(base.length);
  if (rest !== '' && !rest.startsWith('/') && !rest.startsWith('?')) return null;
  return rest.split('?')[0] || '/';
}

export function isAuthEndpoint(path: string): boolean {
  return /^\/auth\/(login|refresh|logout)(\/|$)/i.test(path);
}

export function isPlatformEndpoint(path: string): boolean {
  // Platform console endpoints (platform token only): /platform/* and the platform login at /auth/platform/login.
  return /^\/(platform|auth\/platform)(\/|$)/i.test(path);
}
