import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AccountsReportsService } from '../accounts-reports.service';
import { ReportDateFilter } from '../report-date-filter/report-date-filter';
import type { CashBankReport as CashBankReportModel } from '../accounts-reports.models';

const money = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

@Component({
  selector: 'app-cash-bank-report',
  standalone: true,
  imports: [RouterLink, ReportDateFilter],
  templateUrl: './cash-bank-report.html',
  styleUrl: '../accounts-report.scss',
})
export class CashBankReport implements OnInit {
  private readonly service = inject(AccountsReportsService);

  readonly dateFrom = signal('');
  readonly dateTo = signal('');
  readonly loading = signal(true);
  readonly failed = signal(false);
  readonly report = signal<CashBankReportModel | null>(null);

  readonly symbol = computed(() => this.report()?.baseCurrency.symbol ?? '₹');
  readonly fmt = (n: number) => this.symbol() + money(n);

  ngOnInit(): void {
    this.load();
  }

  onDates(from: string | null, to: string | null): void {
    if (from !== null) this.dateFrom.set(from);
    if (to !== null) this.dateTo.set(to);
    this.load();
  }

  print(): void {
    window.print();
  }

  load(): void {
    this.loading.set(true);
    this.failed.set(false);
    this.service.cashBank({ dateFrom: this.dateFrom(), dateTo: this.dateTo() }).subscribe({
      next: (r) => {
        this.report.set(r);
        this.loading.set(false);
      },
      error: () => {
        this.failed.set(true);
        this.loading.set(false);
      },
    });
  }
}
