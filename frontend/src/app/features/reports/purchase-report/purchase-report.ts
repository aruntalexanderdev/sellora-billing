import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { UiDataTable } from '../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../shared/ui/data-table/data-table.types';
import { problemMessage, toProblem } from '../../../core/http/api-errors';
import { LoadError } from '../../../shared/scope/load-error';
import { ScopeFilters } from '../../../shared/scope/scope-filters';
import { injectBranchColumnVisible, placeLabel } from '../../../shared/scope/list-scope';
import { ReportsService } from '../reports.service';
import type { PurchaseReportRow } from '../reports.models';
import type { TaxComponentKind } from '../../inventory/tax-setup/tax-setup.models';
import { UiCompanyViewToggle } from '../../../shared/ui/company-view-toggle/company-view-toggle';

const HEAD_COLS: DataTableColumn[] = [
  { label: 'Purchase No', align: 'left' },
  { label: 'Date', align: 'left' },
  { label: 'Supplier', align: 'left' },
  { label: 'Subtotal', align: 'right' },
  { label: 'Taxable', align: 'right' },
  { label: 'Tax', align: 'right' },
];
const TAIL_COLS: DataTableColumn[] = [
  { label: 'Other', align: 'right' },
  { label: 'Total', align: 'right' },
  { label: 'Paid', align: 'right' },
  { label: 'Due', align: 'right' },
];

const KIND_ORDER: TaxComponentKind[] = ['Cgst', 'Sgst', 'Utgst', 'Igst', 'Cess', 'Vat', 'Other'];
const KIND_LABEL: Record<TaxComponentKind, string> = { Cgst: 'CGST', Sgst: 'SGST', Utgst: 'UTGST', Igst: 'IGST', Cess: 'Cess', Vat: 'VAT', Other: 'Other tax' };

/** Distinct tax component kinds present in the rows, in a fixed order (legacy tax is under `Other`). */
export function reportTaxKinds(rows: readonly PurchaseReportRow[]): TaxComponentKind[] {
  const seen = new Set<TaxComponentKind>();
  for (const r of rows) for (const t of r.taxByKind ?? []) seen.add(t.kind);
  return KIND_ORDER.filter((k) => seen.has(k));
}

export const reportTaxLabel = (kind: TaxComponentKind): string => KIND_LABEL[kind];

const INR = (n: number) => '₹' + Math.round(n).toLocaleString('en-IN');

// Report totals are always in the base currency; a foreign purchase also shows what it was invoiced in.
const originalAmount = (code: string, original: number, baseTotal: number) =>
  original === baseTotal ? undefined : `${code} ${original.toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
const PAGE_SIZE = 20;

@Component({
  selector: 'app-purchase-report',
  standalone: true,
  imports: [UiCompanyViewToggle, UiDataTable, ScopeFilters, LoadError],
  templateUrl: './purchase-report.html',
  styleUrl: '../report-list.scss',
})
export class PurchaseReport implements OnInit {
  private readonly reportsService = inject(ReportsService);

  readonly showBranch = injectBranchColumnVisible();
  readonly taxKinds = computed(() => reportTaxKinds(this.items()));
  readonly cols = computed<DataTableColumn[]>(() => [
    ...HEAD_COLS,
    ...this.taxKinds().map((k) => ({ label: reportTaxLabel(k), align: 'right' as const })),
    ...TAIL_COLS,
    ...(this.showBranch() ? [{ label: 'Branch', align: 'left' as const }] : []),
  ]);
  readonly branchId = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);
  readonly search = signal('');
  readonly dateFrom = signal('');
  readonly dateTo = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<PurchaseReportRow[]>([]);
  readonly total = signal(0);

  readonly hasDateFilter = computed(() => !!this.dateFrom() || !!this.dateTo());
  readonly hasMore = computed(() => this.page() * PAGE_SIZE < this.total());
  readonly isPartialTotals = computed(() => this.page() > 1 || this.hasMore());

  readonly totals = computed(() => {
    const rows = this.items();
    return {
      subtotal: rows.reduce((s, r) => s + r.subtotal, 0),
      tax: rows.reduce((s, r) => s + r.taxTotal, 0),
      grandTotal: rows.reduce((s, r) => s + r.grandTotal, 0),
      due: rows.reduce((s, r) => s + r.dueAmount, 0),
    };
  });

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((p) => ({
      cells: [
        { kind: 'text', text: p.purchaseNo, weight: '600', color: p.status === 'Cancelled' ? 'var(--color-text-fainter)' : 'var(--color-text)' },
        { kind: 'text', text: p.purchaseDate, color: 'var(--color-text-muted)' },
        { kind: 'text', text: p.supplierName },
        { kind: 'text', text: INR(p.subtotal), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(p.taxableTotal ?? p.subtotal + p.otherChargesTotal), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(p.taxTotal), align: 'right', color: 'var(--color-text-muted)' },
        ...this.taxKinds().map((k) => ({
          kind: 'text' as const,
          text: INR((p.taxByKind ?? []).filter((t) => t.kind === k).reduce((s, t) => s + t.taxAmount, 0)),
          align: 'right' as const,
          color: 'var(--color-text-muted)',
        })),
        { kind: 'text', text: INR(p.otherChargesTotal), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(p.grandTotal), sub: originalAmount(p.currencyCode, p.originalGrandTotal, p.grandTotal), align: 'right', weight: '650' },
        { kind: 'text', text: INR(p.paidAmount), align: 'right', color: 'var(--color-text-muted)' },
        { kind: 'text', text: INR(p.dueAmount), align: 'right', color: p.dueAmount > 0 ? 'var(--color-danger)' : 'var(--color-text-muted)' },
        ...(this.showBranch() ? [{ kind: 'text' as const, text: placeLabel(p.branchCode, p.branchName), color: 'var(--color-text-muted)' }] : []),
      ],
    })),
  );

  ngOnInit(): void {
    this.load();
  }

  onSearch(value: string): void {
    this.search.set(value);
    this.page.set(1);
    this.load();
  }

  onDateFromChange(value: string): void {
    this.dateFrom.set(value);
    this.page.set(1);
    this.load();
  }

  onDateToChange(value: string): void {
    this.dateTo.set(value);
    this.page.set(1);
    this.load();
  }

  clearDates(): void {
    this.dateFrom.set('');
    this.dateTo.set('');
    this.page.set(1);
    this.load();
  }

  onBranchChange(value: string | null): void {
    this.branchId.set(value);
    this.page.set(1);
    this.load();
  }

  onPageChange(page: number): void {
    this.page.set(page);
    this.load();
  }

  onCompanyViewChange(): void {
    this.page.set(1);
    this.load();
  }

  retry(): void {
    this.load();
  }

  print(): void {
    window.print();
  }

  private load(): void {
    this.state.set('loading');
    this.errorMessage.set(null);
    this.reportsService
      .purchases({ search: this.search(), branchId: this.branchId(), dateFrom: this.dateFrom() || undefined, dateTo: this.dateTo() || undefined, page: this.page(), pageSize: PAGE_SIZE })
      .subscribe({
        next: (res) => {
          this.items.set(res.items);
          this.total.set(res.total);
          this.state.set('ready');
        },
        error: (err) => {
          this.errorMessage.set(problemMessage(toProblem(err)));
          this.state.set('error');
        },
      });
  }
}
