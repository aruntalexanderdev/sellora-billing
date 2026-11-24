import { AfterViewInit, Component, ElementRef, OnDestroy, inject, input, output, viewChild } from '@angular/core';
import { ConfirmDialogService } from '../confirm-dialog/confirm-dialog.service';

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

let nextId = 0;

/**
 * Generic modal dialog. Render it with `@if` and close it on `(closed)`: it moves focus inside on open,
 * traps Tab, closes on Esc / backdrop click and restores focus to the previously focused element on destroy.
 *
 * ```html
 * @if (open()) {
 *   <ui-modal title="New category" (closed)="open.set(false)">
 *     …body…
 *     <div modal-footer>…buttons…</div>
 *   </ui-modal>
 * }
 * ```
 */
@Component({
  selector: 'ui-modal',
  standalone: true,
  host: { '(document:keydown)': 'onDocumentKeydown($event)' },
  template: `
    <div class="backdrop" (mousedown)="onBackdropDown($event)" (click)="onBackdrop($event)">
      <div
        #panel
        class="panel"
        [class.wide]="size() === 'wide'"
        role="dialog"
        aria-modal="true"
        [attr.aria-labelledby]="titleId"
        tabindex="-1"
      >
        <div class="head">
          <h2 class="title" [id]="titleId">{{ title() }}</h2>
          <button type="button" class="close" aria-label="Close" (click)="closed.emit()">×</button>
        </div>
        <div class="body"><ng-content /></div>
        <div class="footer"><ng-content select="[modal-footer]" /></div>
      </div>
    </div>
  `,
  styles: [`
    .backdrop {
      position: fixed;
      inset: 0;
      background: rgba(16, 25, 28, .45);
      z-index: 60;
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 16px;
    }
    .panel {
      background: var(--color-surface);
      border-radius: var(--radius-card);
      width: 100%;
      max-width: 460px;
      max-height: calc(100vh - 32px);
      display: flex;
      flex-direction: column;
      box-shadow: 0 20px 50px rgba(12, 32, 36, .3);
      outline: none;
    }
    .panel.wide { max-width: 640px; }
    .head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      padding: 18px 22px 0;
    }
    .title {
      font-size: 15.5px;
      font-weight: 650;
      margin: 0;
    }
    .close {
      border: 0;
      background: transparent;
      font-size: 22px;
      line-height: 1;
      color: var(--color-text-muted);
      cursor: pointer;
      padding: 2px 6px;
      border-radius: var(--radius-sm);
    }
    .close:hover { background: var(--color-border-subtle); }
    .body {
      padding: 14px 22px;
      overflow-y: auto;
    }
    .footer {
      padding: 0 22px 18px;
    }
    .footer:empty { display: none; }
    .footer ::ng-deep [modal-footer] {
      display: flex;
      justify-content: flex-end;
      flex-wrap: wrap;
      gap: 9px;
    }
  `],
})
export class UiModal implements AfterViewInit, OnDestroy {
  readonly title = input.required<string>();
  readonly size = input<'default' | 'wide'>('default');
  /** Set false to ignore backdrop clicks (Esc and the close button still close). */
  readonly closeOnBackdrop = input(true);
  /** Esc, backdrop click or the close button. The parent decides whether to actually remove the modal. */
  readonly closed = output<void>();

  readonly titleId = `ui-modal-title-${++nextId}`;
  private readonly confirm = inject(ConfirmDialogService);
  /** A click only closes when the press also started on the backdrop (not a text-selection drag out of a field). */
  private pressStartedOnBackdrop = false;
  private readonly panel = viewChild.required<ElementRef<HTMLElement>>('panel');
  private readonly previousFocus = (typeof document !== 'undefined' ? document.activeElement : null) as HTMLElement | null;

  ngAfterViewInit(): void {
    const panel = this.panel().nativeElement;
    const body = panel.querySelector<HTMLElement>('.body');
    // Prefer the first field of the content over the header close button.
    const target = body?.querySelector<HTMLElement>(FOCUSABLE) ?? panel;
    target.focus();
  }

  ngOnDestroy(): void {
    if (this.previousFocus?.isConnected) this.previousFocus.focus();
  }

  onBackdropDown(event: MouseEvent): void {
    this.pressStartedOnBackdrop = event.target === event.currentTarget;
  }

  onBackdrop(event: MouseEvent): void {
    const startedHere = this.pressStartedOnBackdrop;
    this.pressStartedOnBackdrop = false;
    if (startedHere && event.target === event.currentTarget && this.closeOnBackdrop()) this.closed.emit();
  }

  onDocumentKeydown(event: KeyboardEvent): void {
    // Handled by an inner control (e.g. an open combobox) or by a confirm dialog stacked on top.
    if (event.defaultPrevented || this.confirm.active()) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      this.closed.emit();
      return;
    }
    if (event.key === 'Tab') this.trapTab(event);
  }

  private trapTab(event: KeyboardEvent): void {
    const panel = this.panel().nativeElement;
    const items = Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE));
    if (items.length === 0) {
      event.preventDefault();
      panel.focus();
      return;
    }
    const first = items[0];
    const last = items[items.length - 1];
    const active = document.activeElement;
    if (event.shiftKey && (active === first || !panel.contains(active))) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && (active === last || !panel.contains(active))) {
      event.preventDefault();
      first.focus();
    }
  }
}
