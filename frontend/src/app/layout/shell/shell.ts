import { Component, DestroyRef, computed, inject, signal, viewChild } from '@angular/core';
import { Router, RouterOutlet } from '@angular/router';
import { Sidebar } from '../sidebar/sidebar';
import { Header } from '../header/header';
import { UiToastHost } from '../../shared/ui/toast/toast-host';
import { UiBranchPickerHost } from '../../shared/ui/branch-picker/branch-picker-host';
import { UiConfirmDialogHost } from '../../shared/ui/confirm-dialog/confirm-dialog-host';
import { AuthService } from '../../core/auth/auth.service';
import { ALL_BRANCHES } from '../../core/context/context.models';
import { ContextService } from '../../core/context/context.service';
import { ToastService } from '../../shared/ui/toast/toast.service';
import { problemMessage, toProblem } from '../../core/http/api-errors';
import { NAV_DEF, filterNav } from './nav-def';
import type { SidebarNavGroup } from '../sidebar/sidebar.types';

const SIDEBAR_STORAGE_KEY = 'sellora.sidebarOpen';
const DESKTOP_BREAKPOINT = 900;

function isDesktopWidth(): boolean {
  return window.innerWidth >= DESKTOP_BREAKPOINT;
}

// On desktop the sidebar defaults to whatever the user last chose (persisted, "stick" vs
// collapsed). On mobile it always starts closed — the drawer is an overlay, not a fixed pane.
function readInitialSidebarOpen(): boolean {
  if (!isDesktopWidth()) return false;
  try {
    const stored = localStorage.getItem(SIDEBAR_STORAGE_KEY);
    return stored === null ? true : stored === 'true';
  } catch {
    return true;
  }
}

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, Sidebar, Header, UiToastHost, UiConfirmDialogHost, UiBranchPickerHost],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly context = inject(ContextService);
  private readonly toast = inject(ToastService);

  private readonly outlet = viewChild(RouterOutlet);

  readonly currentUser = this.auth.currentUser;
  readonly userInitials = computed(() => {
    const u = this.currentUser();
    return u ? `${u.firstName[0] ?? ''}${u.lastName[0] ?? ''}` : '';
  });

  readonly navGroups = computed<SidebarNavGroup[]>(() =>
    filterNav(NAV_DEF, {
      hasPermission: (code) => this.auth.hasPermission(code),
      hasGovernance: (code) => this.context.hasGovernance(code),
    }),
  );

  readonly companyName = computed(() => this.context.company()?.name ?? this.currentUser()?.companyName ?? '');
  readonly companyId = computed(() => this.context.company()?.id ?? '');
  readonly branchName = computed(() => (this.context.isAllBranches() ? 'All branches' : (this.context.workingBranch()?.name ?? '')));
  readonly canChooseAll = this.context.canChooseAll;
  readonly companies = this.context.companies;
  readonly allowedBranches = this.context.allowedBranches;
  readonly workingBranchId = computed(() => (this.context.isAllBranches() ? ALL_BRANCHES : (this.context.workingBranch()?.id ?? '')));
  /** Changes on branch switch: the routed component is re-created, so every screen re-fetches for the new branch. */
  readonly contextKey = computed(() => [this.context.contextVersion()]);

  readonly sidebarOpen = signal(readInitialSidebarOpen());

  constructor() {
    // D3: let the context service ask the routed component whether it has unsaved changes before switching.
    const unregister = this.context.registerUnsavedProbe(() => {
      const outlet = this.outlet();
      const component = outlet?.isActivated ? (outlet.component as { hasUnsavedChanges?: () => boolean }) : null;
      return component?.hasUnsavedChanges?.() ?? false;
    });
    inject(DestroyRef).onDestroy(unregister);
  }

  switchBranch(branchId: string): void {
    void this.context.switchBranch(branchId);
  }

  async switchCompany(companyId: string): Promise<void> {
    if (companyId === this.context.company()?.id) return;
    if (!(await this.context.confirmDiscardIfDirty())) return;
    this.context.switchCompany(companyId).subscribe({
      error: (err: unknown) => this.toast.show(problemMessage(toProblem(err))),
    });
  }

  toggleSidebar(): void {
    const desktop = isDesktopWidth();
    this.sidebarOpen.update((v) => {
      const next = !v;
      if (desktop) this.persistSidebarPreference(next);
      return next;
    });
  }

  closeSidebar(): void {
    this.sidebarOpen.set(false);
    if (isDesktopWidth()) this.persistSidebarPreference(false);
  }

  private persistSidebarPreference(open: boolean): void {
    try {
      localStorage.setItem(SIDEBAR_STORAGE_KEY, String(open));
    } catch {
      // per-viewer convenience only — safe to ignore storage failures
    }
  }

  signOut(): void {
    this.auth.logout();
    this.router.navigateByUrl('/login');
  }
}
