import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { injectBranchColumnVisible } from '../../../shared/scope/list-scope';
import { downloadCsv, type CsvCell } from '../../../shared/export/csv';
import type { GstGstinBlock, GstReportSection, GstSummary, GstSummaryRow, TaxReportAmounts } from '../reports.models';
import { ReportsService } from '../reports.service';
import {
  LEGACY_HINT, LEGACY_LABEL, SUPPLY_LABELS, TREATMENT_LABELS, TaxReportFilters, TaxReportLimitations, currentMonthRange, money, periodError, ratePct, reportErrorMessage,
} from '../tax-report-common/tax-report-common';

export const GST_SECTION_LABELS: Record<GstReportSection, string> = {
  Outward: 'Outward supplies (sales)',
  OutwardReturn: 'Outward returns (sales returns)',
  Inward: 'Inward supplies (purchases)',
  InwardReturn: 'Inward returns (purchase returns)',
};
const SECTIONS: GstReportSection[] = ['Outward', 'OutwardReturn', 'Inward', 'InwardReturn'];

export interface GstSectionView {
  section: GstReportSection;
  label: string;
  rows: GstSummaryRow[];
  total: TaxReportAmounts | null;
  documentCount: number;
}
export interface GstBlockView {
  gstin: string | null;
  title: string;
  sections: GstSectionView[];
  outwardNet: TaxReportAmounts;
  inwardNet: TaxReportAmounts;
}

const ZERO: TaxReportAmounts = { taxableValue: 0, cgst: 0, sgstUtgst: 0, igst: 0, cess: 0, legacyTax: 0, totalTax: 0 };

/** GST summary (GSTR-1 / GSTR-3B shaped): rows by section inside a block per seller GSTIN, with the net of returns. */
@Component({
  selector: 'app-gst-summary-report',
  standalone: true,
  imports: [TaxReportFilters, TaxReportLimitations],
  styleUrl: '../tax-report-common/tax-report.scss',
  template: `
    <div class="page-head no-print">
      <div>
        <h1>GST summary</h1>
        <p>Outward and inward supplies by seller GSTIN, place of supply and rate, net of returns. Amounts in {{ report()?.currencyCode ?? 'the base currency' }}.</p>
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
    } @else if (report() && blocks().length === 0) {
      <div class="tr-empty" data-testid="empty">No taxable documents in this period.</div>
    }

    @for (b of blocks(); track b.gstin ?? '-') {
      <section class="tr-block" data-testid="gstin-block">
        <h2>{{ b.title }}</h2>
        <div class="tr-scroll">
          <table class="tr-table">
            <thead>
              <tr>
                <th>Place of supply</th><th>Supply</th><th>Treatment</th><th class="num">Rate</th>
                <th class="num">Taxable</th><th class="num">CGST</th><th class="num">SGST/UTGST</th><th class="num">IGST</th><th class="num">Cess</th><th class="num">Legacy tax</th><th class="num">Docs</th>
              </tr>
            </thead>
            <tbody>
              @for (s of b.sections; track s.section) {
                <tr class="tr-section"><td colspan="11">{{ s.label }}</td></tr>
                @for (r of s.rows; track $index) {
                  <tr data-testid="gst-row">
                    <td>{{ stateText(r) }}</td>
                    <td>{{ supply(r) }}</td>
                    <td>
                      @if (r.isLegacy) { <span class="tr-legacy" data-testid="legacy-label" [title]="legacyHint">{{ legacyLabel }}</span> } @else { {{ treatment(r) }} }
                    </td>
                    <td class="num">{{ rate(r.ratePercent) }}</td>
                    <td class="num">{{ money(r.taxableValue) }}</td><td class="num">{{ money(r.cgst) }}</td><td class="num">{{ money(r.sgstUtgst) }}</td>
                    <td class="num">{{ money(r.igst) }}</td><td class="num">{{ money(r.cess) }}</td><td class="num">{{ money(r.legacyTax) }}</td><td class="num">{{ r.documentCount }}</td>
                  </tr>
                }
                @if (s.rows.length === 0) { <tr><td colspan="11" class="tr-empty">None</td></tr> }
                @if (s.total; as t) {
                  <tr class="tr-total">
                    <td colspan="4">Total</td>
                    <td class="num">{{ money(t.taxableValue) }}</td><td class="num">{{ money(t.cgst) }}</td><td class="num">{{ money(t.sgstUtgst) }}</td>
                    <td class="num">{{ money(t.igst) }}</td><td class="num">{{ money(t.cess) }}</td><td class="num">{{ money(t.legacyTax) }}</td><td class="num">{{ s.documentCount }}</td>
                  </tr>
                }
              }
            </tbody>
            <tfoot>
              <tr data-testid="outward-net">
                <td colspan="4">Net outward (sales less returns)</td>
                <td class="num">{{ money(b.outwardNet.taxableValue) }}</td><td class="num">{{ money(b.outwardNet.cgst) }}</td><td class="num">{{ money(b.outwardNet.sgstUtgst) }}</td>
                <td class="num">{{ money(b.outwardNet.igst) }}</td><td class="num">{{ money(b.outwardNet.cess) }}</td><td class="num">{{ money(b.outwardNet.legacyTax) }}</td><td></td>
              </tr>
              <tr data-testid="inward-net">
                <td colspan="4">Net inward (purchases less returns)</td>
                <td class="num">{{ money(b.inwardNet.taxableValue) }}</td><td class="num">{{ money(b.inwardNet.cgst) }}</td><td class="num">{{ money(b.inwardNet.sgstUtgst) }}</td>
                <td class="num">{{ money(b.inwardNet.igst) }}</td><td class="num">{{ money(b.inwardNet.cess) }}</td><td class="num">{{ money(b.inwardNet.legacyTax) }}</td><td></td>
              </tr>
            </tfoot>
          </table>
        </div>
      </section>
    }
  `,
})
export class GstSummaryReport implements OnInit {
  private readonly service = inject(ReportsService);

  readonly showBranch = injectBranchColumnVisible();
  readonly dateFrom = signal(currentMonthRange().from);
  readonly dateTo = signal(currentMonthRange().to);
  readonly branchId = signal<string | null>(null);
  readonly report = signal<GstSummary | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly money = money;
  readonly legacyLabel = LEGACY_LABEL;
  readonly legacyHint = LEGACY_HINT;

  readonly blocks = computed<GstBlockView[]>(() => {
    const r = this.report();
    if (!r) return [];
    const byGstin = new Map<string, GstGstinBlock>(r.blocks.map((b) => [b.gstin ?? '', b]));
    for (const row of r.rows) if (!byGstin.has(row.gstin ?? '')) byGstin.set(row.gstin ?? '', { gstin: row.gstin, sections: [], outwardNet: ZERO, inwardNet: ZERO });
    return [...byGstin.values()].map((b) => ({
      gstin: b.gstin,
      title: b.gstin ? `Seller GSTIN ${b.gstin}` : 'No GSTIN recorded',
      outwardNet: b.outwardNet,
      inwardNet: b.inwardNet,
      sections: SECTIONS.map((section) => {
        const total = b.sections.find((s) => s.section === section);
        return {
          section,
          label: GST_SECTION_LABELS[section],
          rows: r.rows.filter((x) => x.section === section && (x.gstin ?? '') === (b.gstin ?? '')),
          total: total?.amounts ?? null,
          documentCount: total?.documentCount ?? 0,
        };
      }),
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
    this.service.gstSummary({ dateFrom: this.dateFrom(), dateTo: this.dateTo(), branchId: this.branchId() }).subscribe({
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

  stateText(r: GstSummaryRow): string {
    if (!r.placeOfSupplyStateName) return '—';
    return r.placeOfSupplyStateCode ? `${r.placeOfSupplyStateName} (${r.placeOfSupplyStateCode})` : r.placeOfSupplyStateName;
  }
  supply(r: GstSummaryRow): string {
    return r.supplyType ? (SUPPLY_LABELS[r.supplyType] ?? r.supplyType) : '—';
  }
  treatment(r: GstSummaryRow): string {
    return r.treatment ? (TREATMENT_LABELS[r.treatment] ?? r.treatment) : '—';
  }
  rate(n: number): string {
    return ratePct(n);
  }

  /** CSV rows (header first row excluded): one per summary row, then the net rows per GSTIN. Numbers are unformatted. */
  csvRows(): CsvCell[][] {
    const out: CsvCell[][] = [];
    for (const b of this.blocks()) {
      for (const s of b.sections) {
        for (const r of s.rows) {
          out.push([
            s.label, b.gstin ?? '', r.placeOfSupplyStateName ?? '', r.placeOfSupplyStateCode ?? '', r.supplyType ? this.supply(r) : '',
            r.isLegacy ? LEGACY_LABEL : this.treatment(r), r.ratePercent, r.taxableValue, r.cgst, r.sgstUtgst, r.igst, r.cess, r.legacyTax, r.documentCount,
          ]);
        }
      }
      const net = (label: string, a: TaxReportAmounts): CsvCell[] => [label, b.gstin ?? '', '', '', '', '', '', a.taxableValue, a.cgst, a.sgstUtgst, a.igst, a.cess, a.legacyTax, ''];
      out.push(net('Net outward', b.outwardNet), net('Net inward', b.inwardNet));
    }
    return out;
  }

  exportCsv(): void {
    const r = this.report();
    if (!r) return;
    downloadCsv(
      `gst-summary_${r.dateFrom}_${r.dateTo}.csv`,
      ['Section', 'GSTIN', 'Place of supply', 'State code', 'Supply type', 'Treatment', 'Rate %', 'Taxable value', 'CGST', 'SGST/UTGST', 'IGST', 'Cess', 'Legacy tax', 'Documents'],
      this.csvRows(),
    );
  }

  print(): void {
    window.print();
  }
}
