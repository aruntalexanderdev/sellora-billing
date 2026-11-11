import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { environment } from '../../../environments/environment';

export const PLATFORM_STORAGE_KEY = 'sellora.platform';

/** Mirrors Api/Contracts/PlatformContracts.cs PlatformUserDto. */
export interface PlatformUser {
  id: string;
  name: string;
}

/** Mirrors PlatformLoginRequest. */
export interface PlatformLoginRequest {
  usernameOrEmail: string;
  password: string;
}

/** Mirrors PlatformLoginResponse: an access token only (30 minutes, no refresh token). */
export interface PlatformLoginResponse {
  accessToken: string;
  expiresAt: string;
  user: PlatformUser;
}

function read(): PlatformLoginResponse | null {
  try {
    const raw = sessionStorage.getItem(PLATFORM_STORAGE_KEY);
    return raw ? (JSON.parse(raw) as PlatformLoginResponse) : null;
  } catch {
    return null;
  }
}

function expired(s: PlatformLoginResponse | null): boolean {
  return !s || Number.isNaN(Date.parse(s.expiresAt)) || Date.parse(s.expiresAt) <= Date.now();
}

/**
 * D7: the platform console session. Separate from the company session (AuthService): kept in sessionStorage (dies with the tab),
 * a single short-lived access token, never refreshed. An expired or rejected token means signing in again.
 */
@Injectable({ providedIn: 'root' })
export class PlatformAuthService {
  private readonly http = inject(HttpClient);
  private readonly session = signal<PlatformLoginResponse | null>(read());

  readonly user = computed(() => this.session()?.user ?? null);

  /** The token to send, or null when there is no session or it has expired (the expired session is dropped). */
  accessToken(): string | null {
    const s = this.session();
    if (!s) return null;
    if (expired(s)) {
      this.clear();
      return null;
    }
    return s.accessToken;
  }

  isAuthenticated(): boolean {
    return this.accessToken() !== null;
  }

  login(request: PlatformLoginRequest): Observable<PlatformLoginResponse> {
    return this.http.post<PlatformLoginResponse>(`${environment.apiBaseUrl}/auth/platform/login`, request).pipe(
      tap((res) => {
        try {
          sessionStorage.setItem(PLATFORM_STORAGE_KEY, JSON.stringify(res));
        } catch {
          // keep the in-memory session only
        }
        this.session.set(res);
      }),
    );
  }

  /** Ends the platform session (sign-out, 401, expiry). */
  clear(): void {
    try {
      sessionStorage.removeItem(PLATFORM_STORAGE_KEY);
    } catch {
      // ignore
    }
    this.session.set(null);
  }
}
