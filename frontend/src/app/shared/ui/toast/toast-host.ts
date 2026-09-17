import { Component, inject } from '@angular/core';
import { ToastService } from './toast.service';

@Component({
  selector: 'ui-toast-host',
  standalone: true,
  template: `
    @for (msg of toast.messages(); track msg.id) {
      <div class="toast">
        <span class="toast-dot">✓</span>
        <div class="toast-text">{{ msg.text }}</div>
      </div>
    }
  `,
  styles: [`
    :host {
      position: fixed;
      right: 24px;
      bottom: 24px;
      z-index: 60;
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .toast {
      display: flex;
      align-items: flex-start;
      gap: 10px;
      max-width: 360px;
      padding: 12px 14px;
      border-radius: 9px;
      background: var(--color-sidebar-bg);
      color: #e8eef0;
      box-shadow: 0 12px 30px rgba(12, 32, 36, .26);
      animation: sl-rise .2s ease-out;
    }
    .toast-dot {
      width: 16px;
      height: 16px;
      border-radius: 50%;
      background: var(--tone-good-fg);
      color: #fff;
      font-size: 10px;
      font-weight: 800;
      display: flex;
      align-items: center;
      justify-content: center;
      flex: none;
      margin-top: 1px;
    }
    .toast-text {
      font-size: 13px;
      line-height: 1.45;
    }
  `],
})
export class UiToastHost {
  readonly toast = inject(ToastService);
}
