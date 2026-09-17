import { Component, computed, input } from '@angular/core';

export type BadgeTone = 'good' | 'warn' | 'bad' | 'info' | 'mute';

@Component({
  selector: 'ui-badge',
  standalone: true,
  template: `<span class="ui-badge" [style.background]="bg()" [style.color]="fg()">{{ text() }}</span>`,
  styles: [`
    .ui-badge {
      display: inline-block;
      padding: 3px 9px;
      border-radius: var(--radius-pill);
      font-size: 11.5px;
      font-weight: 650;
      letter-spacing: .01em;
      white-space: nowrap;
    }
  `],
})
export class UiBadge {
  readonly text = input.required<string>();
  readonly tone = input<BadgeTone>('mute');

  private readonly toneMap: Record<BadgeTone, [string, string]> = {
    good: ['var(--tone-good-bg)', 'var(--tone-good-fg)'],
    warn: ['var(--tone-warn-bg)', 'var(--tone-warn-fg)'],
    bad: ['var(--tone-bad-bg)', 'var(--tone-bad-fg)'],
    info: ['var(--tone-info-bg)', 'var(--tone-info-fg)'],
    mute: ['var(--tone-mute-bg)', 'var(--tone-mute-fg)'],
  };

  readonly bg = computed(() => this.toneMap[this.tone()][0]);
  readonly fg = computed(() => this.toneMap[this.tone()][1]);
}
