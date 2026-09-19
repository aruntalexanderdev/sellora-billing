import { Component, computed, input, output } from '@angular/core';

/** The From / To pair shared by the Accounts reports. It only reports what the user picked; the page owns the state. */
@Component({
  selector: 'app-report-date-filter',
  standalone: true,
  template: `
    <div class="report-filter-field">
      <label class="report-filter-label">From</label>
      <input type="date" class="report-filter-input" [value]="from()" (change)="fromChange.emit($any($event.target).value)" />
    </div>
    <div class="report-filter-field">
      <label class="report-filter-label">To</label>
      <input type="date" class="report-filter-input" [value]="to()" (change)="toChange.emit($any($event.target).value)" />
    </div>
    @if (active()) {
      <button type="button" class="report-filter-clear" (click)="cleared.emit()">Clear dates</button>
    }
  `,
  styleUrl: './report-date-filter.scss',
})
export class ReportDateFilter {
  readonly from = input('');
  readonly to = input('');
  readonly fromChange = output<string>();
  readonly toChange = output<string>();
  readonly cleared = output<void>();

  readonly active = computed(() => !!this.from() || !!this.to());
}
