import type { HttpErrorResponse } from '@angular/common/http';
import { Component, input, model, output } from '@angular/core';
import { ScopeFilters } from '../../../shared/scope/scope-filters';
import { UiCompanyViewToggle } from '../../../shared/ui/company-view-toggle/company-view-toggle';
import type { TaxReportLimitation } from '../reports.models';
import { problemMessage, toProblem } from '../../../core/http/api-errors';

export const LEGACY_LABEL = 'Legacy (unsplit)';
export const LEGACY_HINT = 'Tax recorded before CGST/SGST/IGST were stored separately. It is shown as one amount and never split.';
export const HSN_NOT_RECORDED_LABEL = 'HSN not recorded';

export const TREATMENT_LABELS: Record<string, string> = {
  Taxable: 'Taxable',
  NilRated: 'Nil rated',
  Exempt: 'Exempt',
  ZeroRated: 'Zero rated',
  NonTaxable: 'Non-taxable',
};
export const SUPPLY_LABELS: Record<string, string> = { IntraState: 'Intra-state', InterState: 'Inter-state', NotApplicable: 'Not applicable' };

const pad = (n: number) => String(n).padStart(2, '0');
const iso = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;

/** First day of the current month to today (local time), as yyyy-MM-dd. */
export function currentMonthRange(now: Date = new Date()): { from: string; to: string } {
  return { from: iso(new Date(now.getFullYear(), now.getMonth(), 1)), to: iso(now) };
}

/** Whole days between two yyyy-MM-dd dates, inclusive; NaN when either is empty/invalid. */
export function periodDays(from: string, to: string): number {
  const a = Date.parse(from);
  const b = Date.parse(to);
  return Number.isNaN(a) || Number.isNaN(b) ? NaN : Math.round((b - a) / 86_400_000) + 1;
}

/** Client-side period check mirroring the server (both dates, from <= to, at most 366 days); null when fine. */
export function periodError(from: string, to: string): string | null {
  if (!from || !to) return 'Choose both dates.';
  if (from > to) return 'The start date must not be after the end date.';
  if (periodDays(from, to) > 366) return 'Choose a period of at most 366 days.';
  return null;
}

/** Amount for display and tables (2 decimals, Indian grouping). Not for quantities. */
export function money(n: number | null | undefined): string {
  return (n ?? 0).toLocaleString('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

export function ratePct(n: number): string {
  return `${Number(n.toFixed(4))}%`;
}

/** Message of a failed report call; 400 `dateFrom` / `dateTo` field errors come first. */
export function reportErrorMessage(err: unknown): string {
  const problem = toProblem(err as HttpErrorResponse);
  const errors = problem?.errors ?? {};
  const own = Object.entries(errors).find(([k]) => /^date(from|to)$/i.test(k));
  return own ? own[1].join(' ') : problemMessage(problem);
}

/** Filter bar of the tax reports: period, branch (company-wide users) and the company view toggle. */
@Component({
  selector: 'app-tax-report-filters',
  standalone: true,
  imports: [ScopeFilters, UiCompanyViewToggle],
  template: `
    <ui-company-view-toggle permission="Reports.Tax" (changed)="changed.emit()" />
    <div class="report-filters no-print">
      <div class="report-filter-field">
        <label class="report-filter-label" for="tr-from">From</label>
        <input id="tr-from" type="date" class="report-filter-input" data-testid="date-from" [value]="dateFrom()" (change)="dateFrom.set($any($event.target).value); changed.emit()" />
      </div>
      <div class="report-filter-field">
        <label class="report-filter-label" for="tr-to">To</label>
        <input id="tr-to" type="date" class="report-filter-input" data-testid="date-to" [value]="dateTo()" (change)="dateTo.set($any($event.target).value); changed.emit()" />
      </div>
      <app-scope-filters [showBranch]="showBranch()" [showLocation]="false" [branchId]="branchId()" (branchIdChange)="branchId.set($event); changed.emit()" />
      <ng-content />
    </div>
  `,
})
export class TaxReportFilters {
  readonly dateFrom = model('');
  readonly dateTo = model('');
  readonly branchId = model<string | null>(null);
  readonly showBranch = input(true);
  readonly changed = output<void>();
}

/** Plain-language banners for the limitations the server reports. */
@Component({
  selector: 'app-tax-report-limitations',
  standalone: true,
  template: `
    @for (l of limitations(); track l.code) {
      <div class="tr-banner" role="note" [attr.data-testid]="'limitation-' + l.code">
        <strong>Please note.</strong> {{ l.message }}
      </div>
    }
  `,
  styleUrl: './tax-report.scss',
})
export class TaxReportLimitations {
  readonly limitations = input<readonly TaxReportLimitation[]>([]);
}
