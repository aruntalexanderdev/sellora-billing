import { Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';

/** Placeholder for a screen that a later Phase 11 work package replaces (reads the route's `data.page`). */
@Component({
  selector: 'ui-coming-soon',
  standalone: true,
  template: `
    <div class="cs-card">
      <div class="cs-title">{{ page }}</div>
      <div class="cs-body">This screen is being built and will be available in an upcoming release.</div>
    </div>
  `,
  styles: [`
    .cs-card {
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius-card);
      padding: 48px 28px;
      text-align: center;
      max-width: 520px;
      margin: 40px auto;
    }
    .cs-title { font-size: 16px; font-weight: 650; margin-bottom: 6px; }
    .cs-body { font-size: 13.5px; color: var(--color-text-muted); }
  `],
})
export class ComingSoon {
  readonly page = (inject(ActivatedRoute).snapshot.data['page'] as string | undefined) ?? 'Coming soon';
}
