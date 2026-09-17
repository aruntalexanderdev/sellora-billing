import { Injectable, signal } from '@angular/core';

let nextId = 1;

export interface ToastMessage {
  id: number;
  text: string;
}

@Injectable({ providedIn: 'root' })
export class ToastService {
  private readonly _messages = signal<ToastMessage[]>([]);
  readonly messages = this._messages.asReadonly();

  show(text: string, durationMs = 3200): void {
    const id = nextId++;
    this._messages.update((msgs) => [...msgs, { id, text }]);
    setTimeout(() => this.dismiss(id), durationMs);
  }

  dismiss(id: number): void {
    this._messages.update((msgs) => msgs.filter((m) => m.id !== id));
  }
}
