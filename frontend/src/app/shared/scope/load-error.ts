import { Component, input } from '@angular/core';

/** Inline banner for a failed list/detail load; the message comes from `problemMessage(toProblem(err))`. */
@Component({
  selector: 'app-load-error',
  standalone: true,
  template: `
    @if (message(); as m) {
      <div class="load-error" role="alert" data-testid="load-error">{{ m }}</div>
    }
  `,
  styles: `
    .load-error {
      margin-bottom: 14px;
      padding: 10px 14px;
      border-radius: var(--radius-control);
      background: var(--tone-bad-bg, #fdecea);
      color: var(--tone-bad-fg, var(--color-danger));
      font-size: 13px;
    }
  `,
})
export class LoadError {
  readonly message = input<string | null>(null);
}
