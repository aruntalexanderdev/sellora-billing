import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { problemMessage, toProblem } from '../../../../core/http/api-errors';
import { LoadError } from '../../../../shared/scope/load-error';
import { LocationSelect } from '../../../../shared/locations/location-select';
import { UiCompanyViewToggle } from '../../../../shared/ui/company-view-toggle/company-view-toggle';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { statusLabel, statusTone } from '../stock-transfer-rules';
import { StockTransfersService } from '../stock-transfers.service';
import type { ReadyForShipmentItem } from '../stock-transfers.models';

const COLS: DataTableColumn[] = [
  { label: 'No', align: 'left' },
  { label: 'Source', align: 'left' },
  { label: 'Destination', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: 'Approved', align: 'left' },
  { label: 'Lines to ship', align: 'right' },
];

export const READY_PAGE_SIZE = 20;

/** Approved / partially fulfilled requests waiting for shipment (FI-D28). Gated by StockTransfers.Dispatch. */
@Component({
  selector: 'app-ready-for-shipment',
  standalone: true,
  imports: [RouterLink, UiDataTable, LoadError, LocationSelect, UiCompanyViewToggle],
  templateUrl: './ready-for-shipment.html',
  styleUrl: '../stock-transfer-list/stock-transfer-list.scss',
})
export class ReadyForShipment implements OnInit {
  private readonly service = inject(StockTransfersService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly errorMessage = signal<string | null>(null);
  readonly sourceLocationId = signal<string | null>(null);
  readonly search = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<ReadyForShipmentItem[]>([]);
  readonly total = signal(0);
  readonly hasMore = computed(() => this.page() * READY_PAGE_SIZE < this.total());

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((t) => ({
      cells: [
        { kind: 'link', text: t.transferNo, onClick: () => this.router.navigate(['/inventory/stock-transfers', t.id]) },
        { kind: 'text', text: t.sourceLocationName ?? '—' },
        { kind: 'text', text: t.destinationLocationName ?? '—', sub: t.destinationBranchName ?? undefined },
        { kind: 'badge', text: statusLabel(t.status), tone: statusTone(t.status) },
        { kind: 'text', text: t.approvedAt ? new Date(t.approvedAt).toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' }) : '—', color: 'var(--color-text-muted)' },
        { kind: 'text', text: `${t.remainingLineCount} of ${t.lineCount}`, align: 'right' },
      ],
    } satisfies DataTableRow)),
  );

  ngOnInit(): void {
    this.load();
  }

  onSourceChange(value: string | null): void {
    this.sourceLocationId.set(value);
    this.reload();
  }

  onSearch(value: string): void {
    this.search.set(value);
    this.reload();
  }

  onCompanyViewChange(): void {
    this.reload();
  }

  onPageChange(page: number): void {
    this.page.set(page);
    this.load();
  }

  retry(): void {
    this.load();
  }

  private reload(): void {
    this.page.set(1);
    this.load();
  }

  private load(): void {
    this.state.set('loading');
    this.errorMessage.set(null);
    this.service
      .readyForShipment({ sourceLocationId: this.sourceLocationId(), search: this.search() || undefined, page: this.page(), pageSize: READY_PAGE_SIZE })
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
