import { Injectable, signal } from '@angular/core';
import type { AllowedBranch } from '../../../core/auth/auth.models';

interface ActivePicker {
  branches: AllowedBranch[];
  resolve: (branchId: string | null) => void;
}

/** D13: asks the user to pick a working branch (resolves the branch id, or null when cancelled). Rendered by the host mounted at the app root. */
@Injectable({ providedIn: 'root' })
export class BranchPickerService {
  private readonly _active = signal<ActivePicker | null>(null);
  readonly active = this._active.asReadonly();

  pick(branches: AllowedBranch[]): Promise<string | null> {
    this.respond(null); // a newer request supersedes an open one
    return new Promise((resolve) => this._active.set({ branches, resolve }));
  }

  respond(branchId: string | null): void {
    const active = this._active();
    if (!active) return;
    this._active.set(null);
    active.resolve(branchId);
  }
}
