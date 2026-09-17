import { Component, input, output } from '@angular/core';

export type ButtonVariant = 'primary' | 'secondary' | 'destructive';

@Component({
  selector: 'ui-button',
  standalone: true,
  template: `
    <button
      type="button"
      class="ui-button"
      [class]="variant()"
      [disabled]="disabled()"
      [title]="disabled() && disabledReason() ? disabledReason() : null"
      (click)="onClick.emit($event)"
    >
      <ng-content />
    </button>
  `,
  styles: [`
    .ui-button {
      padding: 9px 16px;
      border-radius: var(--radius-control);
      font-size: 13.5px;
      font-weight: 600;
      cursor: pointer;
      border: 1px solid transparent;
      transition: background-color .12s ease;
    }
    .ui-button:disabled {
      cursor: not-allowed;
      opacity: .55;
    }
    .primary {
      background: var(--color-accent);
      border-color: var(--color-accent-border);
      color: #fff;
    }
    .primary:not(:disabled):hover {
      background: var(--color-accent-hover);
    }
    .secondary {
      background: var(--color-surface);
      border-color: var(--color-border-strong);
      color: var(--color-text);
    }
    .secondary:not(:disabled):hover {
      background: #f4f6f7;
    }
    .destructive {
      background: #fff;
      border-color: var(--color-danger-border);
      color: var(--color-danger);
    }
    .destructive:not(:disabled):hover {
      background: var(--color-danger-bg);
    }
  `],
})
export class UiButton {
  readonly variant = input<ButtonVariant>('secondary');
  readonly disabled = input(false);
  readonly disabledReason = input<string | null>(null);
  readonly onClick = output<MouseEvent>();
}
