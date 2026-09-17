import { Component } from '@angular/core';

@Component({
  selector: 'ui-card',
  standalone: true,
  template: `<ng-content />`,
  styles: [`
    :host {
      display: block;
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius-card);
    }
  `],
})
export class UiCard {}
