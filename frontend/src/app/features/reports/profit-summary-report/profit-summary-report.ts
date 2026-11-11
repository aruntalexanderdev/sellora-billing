import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { UiCard } from '../../../shared/ui/card/card';
import { problemMessage, toProblem } from '../../../core/http/api-errors';
import { ScopeFilters } from '../../../shared/scope/scope-filters';
import { injectBranchColumnVisible } from '../../../shared/scope/list-scope';
import { ReportsService } from '../reports.service';
import type { ProfitSummary } from '../reports.models';

const INR = (n: number) => '₹' + n.toLocaleString('en-IN', { maximumFractionDigits: 0 });

@Component({
  selector: 'app-profit-summary-report',
  standalone: true,
  imports: [UiCard, ScopeFilters],
  templateUrl: './profit-summary-report.html',
  styleUrl: './profit-summary-report.scss',
})
export class ProfitSummaryReport implements OnInit {
  private readonly reportsService = inject(ReportsService);

  readonly INR = INR;
  readonly dateFrom = signal('');
  readonly dateTo = signal('');
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly showBranch = injectBranchColumnVisible();
  readonly branchId = signal<string | null>(null);
  readonly summary = signal<ProfitSummary | null>(null);

  readonly hasDateFilter = computed(() => !!this.dateFrom() || !!this.dateTo());

  ngOnInit(): void {
    this.load();
  }

  onDateFromChange(value: string): void {
    this.dateFrom.set(value);
    this.load();
  }

  onDateToChange(value: string): void {
    this.dateTo.set(value);
    this.load();
  }

  onBranchChange(value: string | null): void {
    this.branchId.set(value);
    this.load();
  }

  clearDates(): void {
    this.dateFrom.set('');
    this.dateTo.set('');
    this.load();
  }

  retry(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.reportsService.profitSummary({ branchId: this.branchId(), dateFrom: this.dateFrom() || undefined, dateTo: this.dateTo() || undefined }).subscribe({
      next: (res) => {
        this.summary.set(res);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(problemMessage(toProblem(err), 'Could not load this report.'));
        this.loading.set(false);
      },
    });
  }
}
