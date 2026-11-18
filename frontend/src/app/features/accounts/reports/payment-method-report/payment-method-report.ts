import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { AccountsReportsService } from '../accounts-reports.service';
import { ReportDateFilter } from '../report-date-filter/report-date-filter';
import type { PaymentMethodSummary } from '../accounts-reports.models';
import { UiCompanyViewToggle } from '../../../../shared/ui/company-view-toggle/company-view-toggle';

const money = (n: number) => n.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const LABEL: Record<string, string> = { Cash: 'Cash', Upi: 'UPI', Card: 'Card', Credit: 'Credit', BankTransfer: 'Bank transfer' };

@Component({
  selector: 'app-payment-method-report',
  standalone: true,
  imports: [UiCompanyViewToggle, ReportDateFilter],
  templateUrl: './payment-method-report.html',
  styleUrl: '../accounts-report.scss',
})
export class PaymentMethodReport implements OnInit {
  private readonly service = inject(AccountsReportsService);

  readonly dateFrom = signal('');
  readonly dateTo = signal('');
  readonly loading = signal(true);
  readonly failed = signal(false);
  readonly report = signal<PaymentMethodSummary | null>(null);

  readonly symbol = computed(() => this.report()?.baseCurrency.symbol ?? '₹');
  readonly fmt = (n: number) => this.symbol() + money(n);
  readonly totalTransactions = computed(() => (this.report()?.items ?? []).reduce((n, m) => n + m.transactions, 0));
  readonly label = (m: string) => LABEL[m] ?? m;

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

  onCompanyViewChange(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.failed.set(false);
    this.service.paymentMethods({ dateFrom: this.dateFrom(), dateTo: this.dateTo() }).subscribe({
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
