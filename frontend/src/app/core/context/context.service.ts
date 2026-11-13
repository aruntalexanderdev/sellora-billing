import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { ActivatedRouteSnapshot, Router } from '@angular/router';
import { Observable, firstValueFrom, tap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { ConfirmDialogService } from '../../shared/ui/confirm-dialog/confirm-dialog.service';
import type { ApiProblem, AuthResponse, AuthenticatedUser, MeResponse } from '../auth/auth.models';
import { AuthService } from '../auth/auth.service';
import { ToastService } from '../../shared/ui/toast/toast.service';
import { ALL_BRANCHES_LEGACY, BRANCH_KEY_PREFIX, ContextStatus, GuardRequirement } from './context.models';

/** Full-page navigation (company switch). Injectable so tests do not trigger a jsdom navigation. */
export const HARD_NAVIGATE = new InjectionToken<(url: string) => void>('HARD_NAVIGATE', {
  providedIn: 'root',
  factory: () => (url: string) => window.location.assign(url),
});

const RESYNC_MIN_INTERVAL_MS = 5000;
const EMPTY: ReadonlySet<string> = new Set<string>();

function problemOf(error: unknown): { status: number; code: string | undefined } {
  if (error instanceof HttpErrorResponse) {
    return { status: error.status, code: (error.error as ApiProblem | null)?.code };
  }
  return { status: 0, code: undefined };
}

function isBranchHeaderRejection(error: unknown): boolean {
  const { status, code } = problemOf(error);
  return (status === 403 && code === 'branch_access_denied') || (status === 400 && code === 'branch_header_invalid');
}

@Injectable({ providedIn: 'root' })
export class ContextService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly hardNavigate = inject(HARD_NAVIGATE);
  private readonly toast = inject(ToastService);

  private readonly me = signal<MeResponse | null>(null);
  private readonly _status = signal<ContextStatus>('loading');
  private readonly _workingBranchId = signal<string | null>(null);
  private readonly _companyView = signal(false);
  private readonly _contextVersion = signal(0);

  private seq = 0;
  private lastResyncAt = 0;
  private resyncInFlight: Promise<void> | null = null;
  private waiters: ((status: ContextStatus) => void)[] = [];
  private unsavedProbe: (() => boolean) | null = null;

  readonly status = this._status.asReadonly();
  readonly company = computed(() => {
    const m = this.me();
    return m ? { id: m.companyId, name: m.companyName } : null;
  });
  readonly companies = computed(() => this.me()?.companies ?? []);
  readonly accessScope = computed(() => this.me()?.accessScope ?? null);
  readonly allowedBranches = computed(() => this.me()?.allowedBranches ?? []);
  readonly primaryBranchId = computed(() => this.me()?.primaryBranchId ?? null);
  /** The branch sent as X-Branch-Id; null for a company-wide user with no selection (and no primary branch). */
  readonly workingBranchId = this._workingBranchId.asReadonly();
  /** Permissions for UI gating: always those of the active (working) branch. */
  readonly permissions = computed<ReadonlySet<string>>(() => {
    const m = this.me();
    return m ? new Set(m.permissions) : EMPTY;
  });
  /** FI-D11: codes the user holds company-level; only these may use the "Company view" (`scope=company`). */
  readonly companyPermissions = computed<ReadonlySet<string>>(() => {
    const m = this.me();
    return m?.companyPermissions ? new Set(m.companyPermissions) : EMPTY;
  });
  /** FI-D11: the explicit, per-session "Company view" toggle. Off by default; reset on every context change. */
  readonly companyView = this._companyView.asReadonly();
  readonly governance = computed(() => this.me()?.governance ?? null);
  /** Bumped after a branch switch; the shell re-creates the routed component (so it re-fetches) when it changes. */
  readonly contextVersion = this._contextVersion.asReadonly();

  /** The working branch, or the company default branch when a company-wide user has made no selection. */
  readonly workingBranch = computed(() => {
    const branches = this.allowedBranches();
    const id = this._workingBranchId();
    return (id ? branches.find((b) => b.id === id) : branches.find((b) => b.isDefault)) ?? null;
  });
  /** Selector is shown only when there is something to choose. */
  readonly canSwitchBranch = computed(() => this.allowedBranches().length > 1);
  readonly canSwitchCompany = computed(() => this.companies().length > 1);

  hasPermission(code: string): boolean {
    return this.permissions().has(code);
  }

  /** True when `code` is held company-level, i.e. the Company view may be offered on that page. */
  canCompanyView(code: string): boolean {
    return this.companyPermissions().has(code);
  }

  /** True when the Company view is on and `code` is held company-level: requests for that page add `scope=company`. */
  companyViewActive(code: string): boolean {
    return this._companyView() && this.canCompanyView(code);
  }

  setCompanyView(on: boolean): void {
    this._companyView.set(on);
  }

  /** True when the caller is an active governance member, optionally holding `code` (UI hint only). */
  hasGovernance(code?: string | null): boolean {
    const g = this.governance();
    return g !== null && (!code || g.permissions.includes(code));
  }

  /** Resolves once the context is no longer loading (guards wait on this). */
  whenSettled(): Promise<ContextStatus> {
    const s = this._status();
    if (s !== 'loading') return Promise.resolve(s);
    return new Promise((resolve) => this.waiters.push(resolve));
  }

  /** Loads GET /auth/me (at start, after login, branch switch). Never rejects; failures set status. */
  async hydrate(): Promise<void> {
    const seq = ++this.seq;
    const user = this.auth.currentUser();
    if (!user) {
      this.clear();
      return;
    }
    if (this._status() !== 'ready') this.setStatus('loading');

    let choice = this._workingBranchId() ?? this.readPreference(user);
    if (choice === ALL_BRANCHES_LEGACY) {
      // FI-D11: "All branches" no longer exists; a stored preference migrates to the primary/default branch.
      this.clearPreference(user);
      choice = null;
    }
    let candidate = choice;
    this._workingBranchId.set(candidate);

    let me: MeResponse;
    try {
      me = await this.fetchMe();
    } catch (error) {
      if (candidate !== null && isBranchHeaderRejection(error)) {
        // A stored value is only a preference: drop it and fall back to the server's default.
        this.clearPreference(user);
        candidate = null;
        this._workingBranchId.set(null);
        try {
          me = await this.fetchMe();
        } catch (retryError) {
          if (seq === this.seq) this.fail(retryError);
          return;
        }
      } else {
        if (seq === this.seq) this.fail(error);
        return;
      }
    }
    if (seq !== this.seq) return;

    const allowed = me.allowedBranches ?? [];
    if (allowed.length === 0) {
      this.me.set(me);
      this._workingBranchId.set(null);
      this.setStatus('noBranch');
      return;
    }

    const resolved = this.resolveWorking(me, candidate);
    const implicit = me.primaryBranchId ?? allowed.find((b) => b.isDefault)?.id ?? null;
    const permissionsMatch = resolved === candidate || (candidate === null && resolved === implicit);
    this._workingBranchId.set(resolved);
    if (!permissionsMatch) {
      // /me computed permissions for another branch than the one we settled on; ask again with the right header.
      try {
        me = await this.fetchMe();
      } catch (error) {
        if (seq === this.seq) this.fail(error);
        return;
      }
      if (seq !== this.seq) return;
    }
    this.me.set(me);
    this.setStatus('ready');
  }

  /**
   * D3: switch the working branch for this tab. Resolves false when
   * cancelled, not allowed, or when loading the new context failed (the previous working branch is restored).
   * `reroute: false` skips the "current route no longer accessible" redirect.
   */
  async switchBranch(target: string, options: { reroute?: boolean } = {}): Promise<boolean> {
    if (target === this._workingBranchId()) return true;
    if (!this.allowedBranches().some((b) => b.id === target)) return false;
    if (!(await this.confirmDiscardIfDirty())) return false;

    const user = this.auth.currentUser();
    const previousId = this._workingBranchId();
    const previousStatus = this._status();
    this._workingBranchId.set(target);
    if (user) this.writePreference(user, target);
    await this.hydrate();

    if (this._status() !== 'ready' && previousStatus === 'ready' && this.me() !== null) {
      // The new context could not be loaded: stay on the branch the user was working in (me is still the old, consistent one).
      this._workingBranchId.set(previousId);
      if (user) {
        if (previousId) this.writePreference(user, previousId);
        else this.clearPreference(user);
      }
      this.setStatus('ready');
      this.toast.show('Could not switch branch. You are still working in the previous one.');
      return false;
    }

    if (this._status() === 'ready' && options.reroute !== false && !this.routeAccessible()) {
      await this.router.navigateByUrl('/dashboard');
    }
    this.bumpContext();
    return true;
  }

  /** D3: company switch persists the new session, clears the branch preference and hard-navigates to the dashboard. */
  switchCompany(companyId: string): Observable<AuthResponse> {
    const previous = this.auth.currentUser();
    const refreshToken = this.auth.getRefreshToken();
    return this.http
      .post<AuthResponse>(`${environment.apiBaseUrl}/auth/switch-company`, { companyId, refreshToken })
      .pipe(
        tap((res) => {
          if (previous) this.clearPreference(previous);
          this.auth.adoptSession(res);
          this.reset();
          this.hardNavigate('/dashboard');
        }),
      );
  }

  /**
   * D5 `branch_access_denied`: re-validates the session with the current header. 'revoked' when the working
   * branch had to change (access removed; reset to primary), 'resource' when /me still accepts it (a hidden location).
   */
  async probeBranchAccess(): Promise<'revoked' | 'resource'> {
    const before = this._workingBranchId();
    await this.hydrate();
    if (this._workingBranchId() === before) return 'resource';
    this.bumpContext();
    return 'revoked';
  }

  /** D5: the user has no usable branch (403 branch_required / no_branch_access on an authenticated call). */
  markNoBranch(): void {
    this.setStatus('noBranch');
  }

  /** D5 403 without code: re-fetch /me once so the nav matches the server (debounced). */
  resyncPermissions(): Promise<void> {
    if (this.resyncInFlight) return this.resyncInFlight;
    if (Date.now() - this.lastResyncAt < RESYNC_MIN_INTERVAL_MS) return Promise.resolve();
    this.lastResyncAt = Date.now();
    const before = this.contextSignature();
    this.resyncInFlight = this.hydrate()
      .then(() => {
        // Only a real change re-creates the outlet; an unchanged re-sync must not discard the page's state.
        if (this.contextSignature() !== before) this.bumpContext();
      })
      .finally(() => {
        this.resyncInFlight = null;
      });
    return this.resyncInFlight;
  }

  /** D5 `branch_header_invalid`: clear the selection and re-hydrate. */
  async clearSelectionAndRehydrate(): Promise<void> {
    const user = this.auth.currentUser();
    if (user) this.clearPreference(user);
    this._workingBranchId.set(null);
    await this.hydrate();
    this.bumpContext();
  }

  /** The shell registers a probe reporting whether the routed component has unsaved changes. Returns an unregister function. */
  registerUnsavedProbe(probe: () => boolean): () => void {
    this.unsavedProbe = probe;
    return () => {
      if (this.unsavedProbe === probe) this.unsavedProbe = null;
    };
  }

  async confirmDiscardIfDirty(): Promise<boolean> {
    if (!this.unsavedProbe?.()) return true;
    return this.confirm.ask({
      title: 'Discard unsaved changes?',
      body: 'You have unsaved changes on this page. Switching now will discard them.',
      confirmLabel: 'Switch',
      cancelLabel: 'Stay',
      destructive: true,
    });
  }

  /** Re-evaluates the permission/governance requirements of the currently active route. */
  routeAccessible(): boolean {
    let snap: ActivatedRouteSnapshot | null = this.router.routerState.snapshot.root;
    while (snap) {
      for (const guard of snap.routeConfig?.canActivate ?? []) {
        const req = guard as unknown as GuardRequirement;
        if (req.permissionCode && !this.hasPermission(req.permissionCode)) return false;
        if (req.permissionAnyOf?.length && !req.permissionAnyOf.some((c) => this.hasPermission(c))) return false;
        if (req.requiresGovernance && !this.hasGovernance(req.governanceCode)) return false;
      }
      snap = snap.firstChild;
    }
    return true;
  }

  /** Clears everything (logout, company switch). */
  reset(): void {
    this.seq++;
    this.me.set(null);
    this._workingBranchId.set(null);
    this._companyView.set(false);
    this.setStatus('loading');
  }

  /** Re-creates the routed component (caches reset) and drops the Company view: the working branch or its permissions changed. */
  private bumpContext(): void {
    this._companyView.set(false);
    this._contextVersion.update((v) => v + 1);
  }

  private contextSignature(): string {
    const m = this.me();
    return JSON.stringify([this._workingBranchId(), [...(m?.permissions ?? [])].sort(), [...(m?.companyPermissions ?? [])].sort()]);
  }

  private clear(): void {
    this.reset();
  }

  private fetchMe(): Promise<MeResponse> {
    return firstValueFrom(this.http.get<MeResponse>(`${environment.apiBaseUrl}/auth/me`));
  }

  private fail(error: unknown): void {
    const { status, code } = problemOf(error);
    this.setStatus(status === 403 && (code === 'branch_required' || code === 'no_branch_access') ? 'noBranch' : 'error');
  }

  private setStatus(status: ContextStatus): void {
    this._status.set(status);
    if (status !== 'loading' && this.waiters.length) {
      const waiting = this.waiters;
      this.waiters = [];
      for (const resolve of waiting) resolve(status);
    }
  }

  private resolveWorking(me: MeResponse, candidate: string | null): string | null {
    const allowed = me.allowedBranches;
    const has = (id: string | null) => id !== null && allowed.some((b) => b.id === id);
    if (has(candidate)) return candidate;
    if (has(me.primaryBranchId)) return me.primaryBranchId;
    // FI-D11 (Q3a): header, else primary, else the company default branch; always a concrete branch so the header is always sent.
    return (allowed.find((b) => b.isDefault) ?? allowed[0]).id;
  }

  private key(user: AuthenticatedUser): string | null {
    const companyId = this.me()?.companyId ?? user.companyId;
    return companyId ? `${BRANCH_KEY_PREFIX}.${user.id}.${companyId}` : null;
  }

  private readPreference(user: AuthenticatedUser): string | null {
    const key = this.key(user);
    if (!key) return null;
    try {
      return sessionStorage.getItem(key) ?? localStorage.getItem(key);
    } catch {
      return null;
    }
  }

  private writePreference(user: AuthenticatedUser, branchId: string): void {
    const key = this.key(user);
    if (!key) return;
    try {
      sessionStorage.setItem(key, branchId);
      localStorage.setItem(key, branchId);
    } catch {
      // per-tab convenience only
    }
  }

  private clearPreference(user: AuthenticatedUser): void {
    const key = this.key(user);
    if (!key) return;
    try {
      sessionStorage.removeItem(key);
      localStorage.removeItem(key);
    } catch {
      // ignore
    }
  }
}
