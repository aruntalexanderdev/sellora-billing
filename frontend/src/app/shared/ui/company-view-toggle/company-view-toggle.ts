import { Component, OnDestroy, computed, inject, input, output } from '@angular/core';
import { ContextService } from '../../../core/context/context.service';

/**
 * FI-D11 "Company view" toggle. Rendered only when `permission` is held company-level (`companyPermissions`);
 * while on it shows a "Company view – all branches" indicator. The state lives in ContextService (off by default,
 * reset on every branch/permission change and when the page is left), and the data services add `scope=company`.
 */
@Component({
  selector: 'ui-company-view-toggle',
  standalone: true,
  template: `
    @if (visible()) {
      <div class="cv no-print">
        <label class="cv-switch" title="Show data for all branches of the company instead of the active branch">
          <input type="checkbox" role="switch" [checked]="on()" (change)="toggle($any($event.target).checked)" />
          <span>Company view</span>
        </label>
        @if (on()) {
          <span class="cv-badge" role="status">Company view – all branches</span>
        }
      </div>
    }
  `,
  styles: [`
    .cv { display: flex; align-items: center; gap: 12px; margin: 0 0 12px; }
    .cv-switch { display: inline-flex; align-items: center; gap: 6px; font-size: 13px; font-weight: 600; cursor: pointer; }
    .cv-badge {
      padding: 3px 9px; border-radius: var(--radius-pill); font-size: 11.5px; font-weight: 650;
      background: var(--color-accent-soft-bg); color: var(--color-accent-soft-fg);
    }
  `],
})
export class UiCompanyViewToggle implements OnDestroy {
  private readonly context = inject(ContextService);

  /** The permission code of the page; the toggle is offered only when it is held company-level. */
  readonly permission = input.required<string>();
  /** Emitted after the state changed, so the page reloads with/without `scope=company`. */
  readonly changed = output<boolean>();

  readonly visible = computed(() => this.context.companyPermissions().has(this.permission()));
  readonly on = computed(() => this.context.companyViewActive(this.permission()));

  toggle(value: boolean): void {
    this.context.setCompanyView(value);
    this.changed.emit(value);
  }

  ngOnDestroy(): void {
    // Never carry a company-wide view over to another page.
    this.context.setCompanyView(false);
  }
}
