import { HttpClient } from '@angular/common/http';
import { DestroyRef, Injectable, Injector, computed, inject, signal } from '@angular/core';
import { Observable, finalize, from, map, shareReplay, switchMap, tap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { Router } from '@angular/router';
import { ContextService, HARD_NAVIGATE } from '../context/context.service';
import type { AuthResponse, AuthenticatedUser, LoginRequest } from './auth.models';

export const AUTH_STORAGE_KEY = 'sellora.auth';

interface StoredSession {
  accessToken: string;
  refreshToken: string;
  user: AuthenticatedUser;
}

function parseSession(raw: string | null): StoredSession | null {
  try {
    return raw ? (JSON.parse(raw) as StoredSession) : null;
  } catch {
    return null;
  }
}

function readStoredSession(): StoredSession | null {
  try {
    return parseSession(localStorage.getItem(AUTH_STORAGE_KEY));
  } catch {
    return null;
  }
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  // ContextService depends on AuthService (user id, tokens); resolved lazily to avoid a construction cycle.
  private readonly injector = inject(Injector);
  private readonly hardNavigate = inject(HARD_NAVIGATE);
  private readonly session = signal<StoredSession | null>(readStoredSession());
  private refreshInFlight: Observable<AuthResponse> | null = null;

  readonly currentUser = computed(() => this.session()?.user ?? null);
  readonly isAuthenticated = computed(() => this.session() !== null);
  readonly accessToken = computed(() => this.session()?.accessToken ?? null);

  constructor() {
    // D4: adopt tokens rotated (or cleared) by another tab, so a second tab never presents a revoked refresh token.
    if (typeof window !== 'undefined') {
      const onStorage = (e: StorageEvent) => {
        if (e.key !== null && e.key !== AUTH_STORAGE_KEY) return;
        const next = e.key === null ? null : parseSession(e.newValue);
        const prev = this.session();
        this.session.set(next);
        if (prev && !next) {
          // Another tab signed out (or cleared storage): end this tab's session too.
          this.context.reset();
          void this.router.navigate(['/login']);
        } else if (!prev && next) {
          void this.context.hydrate();
        } else if (prev && next && (prev.user.id !== next.user.id || prev.user.companyId !== next.user.companyId)) {
          // Another tab switched company / signed in as someone else: never pair the new token with this tab's old
          // company context (X-Branch-Id, nav, cached data). Drop it and reload from scratch.
          this.context.reset();
          this.hardNavigate('/dashboard');
        }
      };
      window.addEventListener('storage', onStorage);
      inject(DestroyRef).onDestroy(() => window.removeEventListener('storage', onStorage));
    }
  }

  private get router(): Router {
    return this.injector.get(Router);
  }

  private get context(): ContextService {
    return this.injector.get(ContextService);
  }

  /** Delegates to the ContextService (permissions at the working branch). */
  hasPermission(code: string): boolean {
    return this.context.hasPermission(code);
  }

  hasAnyPermission(codes: string[]): boolean {
    return codes.some((code) => this.hasPermission(code));
  }

  login(request: LoginRequest): Observable<AuthResponse> {
    return this.http.post<AuthResponse>(`${environment.apiBaseUrl}/auth/login`, request).pipe(
      tap((res) => this.adoptSession(res)),
      switchMap((res) => from(this.context.hydrate()).pipe(map(() => res))),
    );
  }

  /** Single-flight: concurrent callers share one in-flight refresh request. */
  refresh(): Observable<AuthResponse> {
    if (!this.refreshInFlight) {
      const refreshToken = this.session()?.refreshToken;
      this.refreshInFlight = this.http.post<AuthResponse>(`${environment.apiBaseUrl}/auth/refresh`, { refreshToken }).pipe(
        tap((res) => this.adoptSession(res)),
        finalize(() => {
          this.refreshInFlight = null;
        }),
        shareReplay({ bufferSize: 1, refCount: false }),
      );
    }
    return this.refreshInFlight;
  }

  logout(): void {
    const refreshToken = this.session()?.refreshToken;
    this.session.set(null);
    try {
      localStorage.removeItem(AUTH_STORAGE_KEY);
    } catch {
      // ignore storage failures
    }
    this.context.reset();
    if (refreshToken) {
      this.http.post(`${environment.apiBaseUrl}/auth/logout`, { refreshToken }).subscribe({ error: () => {} });
    }
  }

  getRefreshToken(): string | null {
    return this.session()?.refreshToken ?? null;
  }

  /** Patches the cached session's user fields (e.g. after a self-service username change) without a full re-login. */
  updateCurrentUser(patch: Partial<AuthenticatedUser>): void {
    const session = this.session();
    if (!session) return;
    this.store({ ...session, user: { ...session.user, ...patch } });
  }

  /** Persists a login / refresh / switch-company response as the current session. */
  adoptSession(res: AuthResponse): void {
    this.store({ accessToken: res.accessToken, refreshToken: res.refreshToken, user: res.user });
  }

  private store(stored: StoredSession): void {
    this.session.set(stored);
    try {
      localStorage.setItem(AUTH_STORAGE_KEY, JSON.stringify(stored));
    } catch {
      // ignore storage failures
    }
  }
}
