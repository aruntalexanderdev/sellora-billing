import { Injectable, signal } from '@angular/core';

export interface ConfirmRequest {
  title: string;
  body: string;
  confirmLabel?: string;
  cancelLabel?: string;
  destructive?: boolean;
}

interface ActiveConfirm extends ConfirmRequest {
  resolve: (confirmed: boolean) => void;
}

@Injectable({ providedIn: 'root' })
export class ConfirmDialogService {
  private readonly _active = signal<ActiveConfirm | null>(null);
  readonly active = this._active.asReadonly();

  ask(request: ConfirmRequest): Promise<boolean> {
    return new Promise((resolve) => {
      this._active.set({ ...request, resolve });
    });
  }

  respond(confirmed: boolean): void {
    const active = this._active();
    if (!active) return;
    this._active.set(null);
    active.resolve(confirmed);
  }
}
