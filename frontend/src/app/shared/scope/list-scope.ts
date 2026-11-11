import { Signal, computed, inject } from '@angular/core';
import { ContextService } from '../../core/context/context.service';
import type { DataTableCell, DataTableColumn } from '../ui/data-table/data-table.types';

/** The slice of ContextService the visibility rule needs (kept narrow so specs can stub it). */
export interface BranchScopeSource {
  accessScope(): string | null;
  allowedBranches(): readonly unknown[];
  isAllBranches(): boolean;
}

/**
 * D13 / Phase 11 WP-E: lists and reports show a Branch column when the caller can see more than one branch's data:
 * a company-wide user, a user with more than one allowed branch, or anyone in All-branches mode.
 */
export function branchColumnVisible(ctx: BranchScopeSource): boolean {
  return ctx.accessScope() === 'CompanyWide' || ctx.allowedBranches().length > 1 || ctx.isAllBranches();
}

/** Signal form of {@link branchColumnVisible}; call inside an injection context. */
export function injectBranchColumnVisible(): Signal<boolean> {
  const ctx = inject(ContextService);
  return computed(() => branchColumnVisible(ctx));
}

/** "Name (CODE)", falling back to whichever part exists; an em dash when neither does. */
export function placeLabel(code: string | null | undefined, name: string | null | undefined): string {
  if (name && code) return `${name} (${code})`;
  return name || code || '—';
}

/** Query-param helper: only non-empty values are sent. */
export function putIfSet(params: Record<string, string>, key: string, value: string | null | undefined): void {
  if (value) params[key] = value;
}

/** Columns appended by lists: Branch (when {@link branchColumnVisible}) and Location (when the caller can see locations). */
export function scopeColumns(showBranch: boolean, showLocation: boolean): DataTableColumn[] {
  const cols: DataTableColumn[] = [];
  if (showBranch) cols.push({ label: 'Branch', align: 'left' });
  if (showLocation) cols.push({ label: 'Location', align: 'left' });
  return cols;
}

export interface ScopedRowFields {
  branchCode?: string | null;
  branchName?: string | null;
  stockLocationCode?: string | null;
  stockLocationName?: string | null;
}

/** Cells matching {@link scopeColumns}, in the same order. */
export function scopeCells(item: ScopedRowFields, showBranch: boolean, showLocation: boolean): DataTableCell[] {
  const cells: DataTableCell[] = [];
  if (showBranch) cells.push({ kind: 'text', text: placeLabel(item.branchCode, item.branchName), color: 'var(--color-text-muted)' });
  if (showLocation) cells.push({ kind: 'text', text: placeLabel(item.stockLocationCode, item.stockLocationName), color: 'var(--color-text-muted)' });
  return cells;
}
