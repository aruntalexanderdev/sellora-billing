import { Component, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { UiButton } from '../../../shared/ui/button/button';

export const REASON_MAX = 500;

/** Trimmed reason, or null when it is empty (or only whitespace). */
export function normalizeReason(raw: string | null | undefined): string | null {
  const text = (raw ?? '').trim();
  return text.length > 0 ? text : null;
}

/** Inline "state your reason" panel for audited actions (access, governance, platform). Emits the trimmed reason. */
@Component({
  selector: 'app-reason-prompt',
  standalone: true,
  imports: [FormsModule, UiButton],
  template: `
    <div class="panel" role="group" [attr.aria-label]="title()">
      <h3>{{ title() }}</h3>
      @if (error()) {
        <div class="server-error">{{ error() }}</div>
      }
      <label class="field-label" for="reason-input">Reason{{ required() ? '' : ' (optional)' }}</label>
      <textarea id="reason-input" class="field-input" rows="2" [maxLength]="max" [ngModel]="reason()" (ngModelChange)="reason.set($event)"></textarea>
      @if (touched() && invalid()) {
        <div class="field-error">A reason is required.</div>
      }
      <div class="actions">
        <ui-button [variant]="destructive() ? 'destructive' : 'primary'" [disabled]="busy()" (onClick)="submit()">{{ confirmLabel() }}</ui-button>
        <ui-button (onClick)="cancelled.emit()">Cancel</ui-button>
      </div>
    </div>
  `,
  styleUrl: './access-ui.scss',
})
export class ReasonPrompt {
  readonly title = input.required<string>();
  readonly confirmLabel = input('Confirm');
  readonly destructive = input(false);
  readonly required = input(true);
  readonly busy = input(false);
  readonly error = input<string | null>(null);
  readonly confirmed = output<string | null>();
  readonly cancelled = output<void>();

  readonly max = REASON_MAX;
  readonly reason = signal('');
  readonly touched = signal(false);

  invalid(): boolean {
    return this.required() && normalizeReason(this.reason()) === null;
  }

  submit(): void {
    this.touched.set(true);
    if (this.invalid()) return;
    this.confirmed.emit(normalizeReason(this.reason()));
  }
}
