import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { downloadCsv, type CsvCell } from '../../../shared/export/csv';
import { injectBranchColumnVisible } from '../../../shared/scope/list-scope';
import { formatQuantity } from '../../../shared/units/unit-conversion';
import type { HsnDirection, HsnSummary, HsnSummaryRow } from '../reports.models';
import { ReportsService } from '../reports.service';
import {
  HSN_NOT_RECORDED_LABEL, LEGACY_HINT, TaxReportFilters, TaxReportLimitations, currentMonthRange, money, periodError, ratePct, reportErrorMessage,
} from '../tax-report-common/tax-report-common';

/** HSN summary (GSTR-1 table 12) of sales or purchases; quantities are base quantities net of returns, summed only within one HSN and base unit. */
@Component({
  selector: 'app-hsn-summary-report',
  standalone: true,
  imports: [TaxReportFilters, TaxReportLimitations],
  styleUrl: '../tax-report-common/tax-report.scss',
  template: `
    <div class="page-head no-print">
      <div>
        <h1>HSN summary</h1>
        <p>{{ direction() === 'Sales' ? 'Sales' : 'Purchases' }} by HSN/SAC code, unit and rate, net of returns.</p>
      </div>
      <div class="tr-actions">
        <button type="button" class="report-print-btn" data-testid="export-csv" [disabled]="!report()" (click)="exportCsv()">Export CSV</button>
        <button type="button" class="report-print-btn" (click)="print()">Print</button>
      </div>
    </div>

    <app-tax-report-filters [(dateFrom)]="dateFrom" [(dateTo)]="dateTo" [(branchId)]="branchId" [showBranch]="showBranch()" (changed)="load()">
      <div class="report-filter-field">
        <span class="report-filter-label">Direction</span>
        <div class="tr-toggle" role="group" aria-label="Direction">
          <button type="button" data-testid="dir-Sales" [class.on]="direction() === 'Sales'" [attr.aria-pressed]="direction() === 'Sales'" (click)="setDirection('Sales')">Sales</button>
          <button type="button" data-testid="dir-Purchases" [class.on]="direction() === 'Purchases'" [attr.aria-pressed]="direction() === 'Purchases'" (click)="setDirection('Purchases')">Purchases</button>
        </div>
      </div>
    </app-tax-report-filters>

    @if (error(); as e) { <div class="tr-error" role="alert" data-testid="report-error">{{ e }}</div> }
    <app-tax-report-limitations [limitations]="report()?.limitations ?? []" />

    @if (loading()) {
      <div class="tr-empty">Loading…</div>
    } @else if (report()) {
      <div class="tr-scroll">
        <table class="tr-table">
          <thead>
            <tr>
              <th>HSN/SAC</th><th>UQC</th><th class="num">Quantity</th><th class="num">Rate</th>
              <th class="num">Taxable</th><th class="num">CGST</th><th class="num">SGST/UTGST</th><th class="num">IGST</th><th class="num">Cess</th><th class="num">Legacy tax</th><th class="num">Total value</th>
            </tr>
          </thead>
          <tbody>
            @for (r of report()!.rows; track $index) {
              <tr data-testid="hsn-row">
                <td>
                  @if (r.hsnCode) { {{ r.hsnCode }} } @else { <span class="tr-legacy" data-testid="hsn-missing" [title]="hint">{{ missingLabel }}</span> }
                </td>
                <td>{{ r.uqc }}</td>
                <td class="num">{{ qty(r) }}</td>
                <td class="num">{{ rate(r.ratePercent) }}</td>
                <td class="num">{{ money(r.taxableValue) }}</td><td class="num">{{ money(r.cgst) }}</td><td class="num">{{ money(r.sgstUtgst) }}</td>
                <td class="num">{{ money(r.igst) }}</td><td class="num">{{ money(r.cess) }}</td><td class="num">{{ money(r.legacyTax) }}</td><td class="num">{{ money(r.totalValue) }}</td>
              </tr>
            } @empty {
              <tr><td colspan="11" class="tr-empty" data-testid="empty">No lines in this period.</td></tr>
            }
          </tbody>
          @if (report()!.rows.length > 0) {
            <tfoot>
              <tr data-testid="hsn-totals">
                <td colspan="4">Totals</td>
                <td class="num">{{ money(report()!.totals.taxableValue) }}</td>
                <td colspan="4"></td>
                <td class="num">{{ money(report()!.totals.totalTax) }}</td>
                <td class="num">{{ money(report()!.totals.totalValue) }}</td>
              </tr>
            </tfoot>
          }
        </table>
      </div>
    }
  `,
})
export class HsnSummaryReport implements OnInit {
  private readonly service = inject(ReportsService);

  readonly showBranch = injectBranchColumnVisible();
  readonly dateFrom = signal(currentMonthRange().from);
  readonly dateTo = signal(currentMonthRange().to);
  readonly branchId = signal<string | null>(null);
  readonly direction = signal<HsnDirection>('Sales');
  readonly report = signal<HsnSummary | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly money = money;
  readonly missingLabel = HSN_NOT_RECORDED_LABEL;
  readonly hint = 'These lines predate HSN snapshots, or the product had no HSN code when they were recorded. ' + LEGACY_HINT.split('.')[0] + '.';
  readonly hasMissing = computed(() => !!this.report()?.rows.some((r) => !r.hsnCode));

  ngOnInit(): void {
    this.load();
  }

  setDirection(d: HsnDirection): void {
    if (this.direction() === d) return;
    this.direction.set(d);
    this.load();
  }

  load(): void {
    const bad = periodError(this.dateFrom(), this.dateTo());
    if (bad) {
      this.error.set(bad);
      this.report.set(null);
      return;
    }
    this.error.set(null);
    this.loading.set(true);
    this.service.hsnSummary({ dateFrom: this.dateFrom(), dateTo: this.dateTo(), branchId: this.branchId(), direction: this.direction() }).subscribe({
      next: (r) => {
        this.report.set(r);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.report.set(null);
        this.error.set(reportErrorMessage(err));
        this.loading.set(false);
      },
    });
  }

  qty(r: HsnSummaryRow): string {
    return formatQuantity(r.baseQty, r.decimalPlaces);
  }
  rate(n: number): string {
    return ratePct(n);
  }

  csvRows(): CsvCell[][] {
    return (this.report()?.rows ?? []).map((r) => [
      r.hsnCode ?? HSN_NOT_RECORDED_LABEL, r.uqc, formatQuantity(r.baseQty, r.decimalPlaces), r.ratePercent, r.taxableValue, r.cgst, r.sgstUtgst, r.igst, r.cess, r.legacyTax, r.totalValue,
    ]);
  }

  exportCsv(): void {
    const r = this.report();
    if (!r) return;
    downloadCsv(
      `hsn-summary-${r.direction.toLowerCase()}_${r.dateFrom}_${r.dateTo}.csv`,
      ['HSN/SAC', 'UQC', 'Quantity', 'Rate %', 'Taxable value', 'CGST', 'SGST/UTGST', 'IGST', 'Cess', 'Legacy tax', 'Total value'],
      this.csvRows(),
    );
  }

  print(): void {
    window.print();
  }
}
