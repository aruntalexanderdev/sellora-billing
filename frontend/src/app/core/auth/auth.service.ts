import { HttpClient } from '@angular/common/http';
import { Injectable, computed, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { environment } from '../../../environments/environment';
import type { AuthResponse, AuthenticatedUser, LoginRequest } from './auth.models';

const STORAGE_KEY = 'sellora.auth';

interface StoredSession {
  accessToken: string;
  refreshToken: string;
  user: AuthenticatedUser;
}

function readStoredSession(): StoredSession | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as StoredSession) : null;
  } catch {
    return null;
  }
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly session = signal<StoredSession | null>(readStoredSession());

  readonly currentUser = computed(() => this.session()?.user ?? null);
  readonly isAuthenticated = computed(() => this.session() !== null);
  readonly accessToken = computed(() => this.session()?.accessToken ?? null);

  constructor(private readonly http: HttpClient) {}

  hasPermission(code: string): boolean {
    return this.session()?.user.permissions.includes(code) ?? false;
  }

  hasAnyPermission(codes: string[]): boolean {
    return codes.some((code) => this.hasPermission(code));
  }

  login(request: LoginRequest): Observable<AuthResponse> {
    return this.http.post<AuthResponse>(`${environment.apiBaseUrl}/auth/login`, request).pipe(
      tap((res) => this.persist(res)),
    );
  }

  refresh(): Observable<AuthResponse> {
    const refreshToken = this.session()?.refreshToken;
    return this.http.post<AuthResponse>(`${environment.apiBaseUrl}/auth/refresh`, { refreshToken }).pipe(
      tap((res) => this.persist(res)),
    );
  }

  logout(): void {
    const refreshToken = this.session()?.refreshToken;
    this.session.set(null);
    localStorage.removeItem(STORAGE_KEY);
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
    const updated: StoredSession = { ...session, user: { ...session.user, ...patch } };
    this.session.set(updated);
    localStorage.setItem(STORAGE_KEY, JSON.stringify(updated));
  }

  private persist(res: AuthResponse): void {
    const stored: StoredSession = { accessToken: res.accessToken, refreshToken: res.refreshToken, user: res.user };
    this.session.set(stored);
    localStorage.setItem(STORAGE_KEY, JSON.stringify(stored));
  }
}
