import { Component, inject, signal } from '@angular/core';
import { UiButton } from '../button/button';
import { BranchPickerService } from './branch-picker.service';

@Component({
  selector: 'ui-branch-picker-host',
  standalone: true,
  imports: [UiButton],
  template: `
    @if (picker.active(); as req) {
      <div class="backdrop" (click)="picker.respond(null)">
        <div class="panel" role="dialog" aria-label="Choose a branch" (click)="$event.stopPropagation()">
          <div class="title">Choose a branch</div>
          <div class="body">You are viewing all branches. This action belongs to one branch; pick the branch to work in.</div>
          <select class="field" aria-label="Branch" [value]="selected() ?? ''" (change)="selected.set($any($event.target).value || null)">
            <option value="">Select a branch…</option>
            @for (b of req.branches; track b.id) {
              <option [value]="b.id">{{ b.name }}</option>
            }
          </select>
          <div class="actions">
            <ui-button variant="secondary" (onClick)="cancel()">Cancel</ui-button>
            <ui-button variant="primary" [disabled]="!selected()" (onClick)="confirm()">Continue</ui-button>
          </div>
        </div>
      </div>
    }
  `,
  styles: [`
    .backdrop { position: fixed; inset: 0; background: rgba(16, 25, 28, .45); z-index: 70; display: flex; align-items: center; justify-content: center; }
    .panel { background: var(--color-surface); border-radius: var(--radius-card); padding: 22px; width: 100%; max-width: 380px; box-shadow: 0 20px 50px rgba(12, 32, 36, .3); }
    .title { font-size: 15.5px; font-weight: 650; margin-bottom: 8px; }
    .body { font-size: 13.5px; color: var(--color-text-muted); line-height: 1.55; margin-bottom: 14px; }
    .field { width: 100%; margin-bottom: 20px; padding: 8px 10px; }
    .actions { display: flex; justify-content: flex-end; gap: 9px; }
  `],
})
export class UiBranchPickerHost {
  readonly picker = inject(BranchPickerService);
  readonly selected = signal<string | null>(null);

  confirm(): void {
    const id = this.selected();
    if (!id) return;
    this.selected.set(null);
    this.picker.respond(id);
  }

  cancel(): void {
    this.selected.set(null);
    this.picker.respond(null);
  }
}
