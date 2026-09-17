import { Component, inject } from '@angular/core';
import { ConfirmDialogService } from './confirm-dialog.service';
import { UiButton } from '../button/button';

@Component({
  selector: 'ui-confirm-dialog-host',
  standalone: true,
  imports: [UiButton],
  template: `
    @if (dialog.active(); as req) {
      <div class="backdrop" (click)="dialog.respond(false)">
        <div class="panel" (click)="$event.stopPropagation()">
          <div class="title">{{ req.title }}</div>
          <div class="body">{{ req.body }}</div>
          <div class="actions">
            <ui-button variant="secondary" (onClick)="dialog.respond(false)">{{ req.cancelLabel ?? 'Cancel' }}</ui-button>
            <ui-button [variant]="req.destructive ? 'destructive' : 'primary'" (onClick)="dialog.respond(true)">
              {{ req.confirmLabel ?? 'Confirm' }}
            </ui-button>
          </div>
        </div>
      </div>
    }
  `,
  styles: [`
    .backdrop {
      position: fixed;
      inset: 0;
      background: rgba(16, 25, 28, .45);
      z-index: 70;
      display: flex;
      align-items: center;
      justify-content: center;
    }
    .panel {
      background: var(--color-surface);
      border-radius: var(--radius-card);
      padding: 22px;
      width: 100%;
      max-width: 380px;
      box-shadow: 0 20px 50px rgba(12, 32, 36, .3);
    }
    .title {
      font-size: 15.5px;
      font-weight: 650;
      margin-bottom: 8px;
    }
    .body {
      font-size: 13.5px;
      color: var(--color-text-muted);
      line-height: 1.55;
      margin-bottom: 20px;
    }
    .actions {
      display: flex;
      justify-content: flex-end;
      gap: 9px;
    }
  `],
})
export class UiConfirmDialogHost {
  readonly dialog = inject(ConfirmDialogService);
}
