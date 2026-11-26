import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { downloadCsv, type CsvCell } from '../../../shared/export/csv';
import { injectBranchColumnVisible } from '../../../shared/scope/list-scope';
import type { VatCategory, VatReportSection, VatSummary, VatSummaryRow } from '../reports.models';
import { ReportsService } from '../reports.service';
import { LEGACY_HINT, LEGACY_LABEL, TaxReportFilters, TaxReportLimitations, currentMonthRange, money, periodError, ratePct, reportErrorMessage } from '../tax-report-common/tax-report-common';

export const VAT_SECTION_LABELS: Record<VatReportSection, string> = {
  Output: 'Output VAT (sales)',
  OutputReturn: 'Output returns (sales returns)',
  Input: 'Input VAT (purchases)',
  InputReturn: 'Input returns (purchase returns)',
};
export const VAT_CATEGORY_LABELS: Record<VatCategory, string> = {
  StandardRated: 'Standard rated',
  ZeroRated: 'Zero rated',
  Exempt: 'Exempt',
  OutOfScope: 'Out of scope',
  Legacy: LEGACY_LABEL,
};
const SECTIONS: VatReportSection[] = ['Output', 'OutputReturn', 'Input', 'InputReturn'];

/** VAT summary: sections by category, region (emirate) and rate, with output and input VAT net of returns. */
@Component({
  selector: 'app-vat-summary-report',
  standalone: true,
  imports: [TaxReportFilters, TaxReportLimitations],
  styleUrl: '../tax-report-common/tax-report.scss',
  template: `
    <div class="page-head no-print">
      <div>
        <h1>VAT summary</h1>
        <p>Output and input VAT by category, region and rate, net of returns. Amounts in {{ report()?.currencyCode ?? 'the base currency' }}.</p>
      </div>
      <div class="tr-actions">
        <button type="button" class="report-print-btn" data-testid="export-csv" [disabled]="!report()" (click)="exportCsv()">Export CSV</button>
        <button type="button" class="report-print-btn" (click)="print()">Print</button>
      </div>
    </div>

    <app-tax-report-filters [(dateFrom)]="dateFrom" [(dateTo)]="dateTo" [(branchId)]="branchId" [showBranch]="showBranch()" (changed)="load()" />

    @if (error(); as e) { <div class="tr-error" role="alert" data-testid="report-error">{{ e }}</div> }
    <app-tax-report-limitations [limitations]="report()?.limitations ?? []" />

    @if (loading()) {
      <div class="tr-empty">Loading…</div>
    } @else if (report(); as r) {
      <div class="tr-scroll">
        <table class="tr-table">
          <thead>
            <tr><th>Category</th><th>Region</th><th class="num">Rate</th><th class="num">Taxable</th><th class="num">VAT</th><th class="num">Docs</th></tr>
          </thead>
          <tbody>
            @for (s of sections(); track s.section) {
              <tr class="tr-section"><td colspan="6">{{ s.label }}</td></tr>
              @for (x of s.rows; track $index) {
                <tr data-testid="vat-row">
                  <td>
                    @if (x.category === 'Legacy') { <span class="tr-legacy" data-testid="legacy-label" [title]="legacyHint">{{ legacyLabel }}</span> } @else { {{ category(x) }} }
                  </td>
                  <td>{{ x.regionName ?? '—' }}</td>
                  <td class="num">{{ rate(x.ratePercent) }}</td>
                  <td class="num">{{ money(x.taxableValue) }}</td><td class="num">{{ money(x.vatAmount) }}</td><td class="num">{{ x.documentCount }}</td>
                </tr>
              } @empty {
                <tr><td colspan="6" class="tr-empty">None</td></tr>
              }
              @if (s.total; as t) {
                <tr class="tr-total"><td colspan="3">Total</td><td class="num">{{ money(t.taxableValue) }}</td><td class="num">{{ money(t.vatAmount) }}</td><td class="num">{{ t.documentCount }}</td></tr>
              }
            }
          </tbody>
          <tfoot>
            <tr data-testid="output-vat"><td colspan="3">Output VAT (sales less returns)</td><td class="num">{{ money(r.outputVat.taxableValue) }}</td><td class="num">{{ money(r.outputVat.vatAmount) }}</td><td></td></tr>
            <tr data-testid="input-vat"><td colspan="3">Input VAT (purchases less returns)</td><td class="num">{{ money(r.inputVat.taxableValue) }}</td><td class="num">{{ money(r.inputVat.vatAmount) }}</td><td></td></tr>
          </tfoot>
        </table>
      </div>
    }
  `,
})
export class VatSummaryReport implements OnInit {
  private readonly service = inject(ReportsService);

  readonly showBranch = injectBranchColumnVisible();
  readonly dateFrom = signal(currentMonthRange().from);
  readonly dateTo = signal(currentMonthRange().to);
  readonly branchId = signal<string | null>(null);
  readonly report = signal<VatSummary | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly money = money;
  readonly legacyLabel = LEGACY_LABEL;
  readonly legacyHint = LEGACY_HINT;

  readonly sections = computed(() => {
    const r = this.report();
    if (!r) return [];
    return SECTIONS.map((section) => ({
      section,
      label: VAT_SECTION_LABELS[section],
      rows: r.rows.filter((x) => x.section === section),
      total: r.sections.find((s) => s.section === section) ?? null,
    }));
  });

  ngOnInit(): void {
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
    this.service.vatSummary({ dateFrom: this.dateFrom(), dateTo: this.dateTo(), branchId: this.branchId() }).subscribe({
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

  category(x: VatSummaryRow): string {
    return VAT_CATEGORY_LABELS[x.category] ?? x.category;
  }
  rate(n: number): string {
    return ratePct(n);
  }

  csvRows(): CsvCell[][] {
    const r = this.report();
    if (!r) return [];
    const rows: CsvCell[][] = r.rows.map((x) => [VAT_SECTION_LABELS[x.section], this.category(x), x.regionName ?? '', x.ratePercent, x.taxableValue, x.vatAmount, x.documentCount]);
    rows.push(['Output VAT (net)', '', '', '', r.outputVat.taxableValue, r.outputVat.vatAmount, ''], ['Input VAT (net)', '', '', '', r.inputVat.taxableValue, r.inputVat.vatAmount, '']);
    return rows;
  }

  exportCsv(): void {
    const r = this.report();
    if (!r) return;
    downloadCsv(`vat-summary_${r.dateFrom}_${r.dateTo}.csv`, ['Section', 'Category', 'Region', 'Rate %', 'Taxable value', 'VAT', 'Documents'], this.csvRows());
  }

  print(): void {
    window.print();
  }
}
