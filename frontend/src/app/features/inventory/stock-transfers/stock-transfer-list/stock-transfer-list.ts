import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import { UiButton } from '../../../../shared/ui/button/button';
import type { DataTableColumn, DataTableFilter, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { LocationSelect } from '../../../../shared/locations/location-select';
import { AuthService } from '../../../../core/auth/auth.service';
import { problemMessage, toProblem } from '../../../../core/http/api-errors';
import { LoadError } from '../../../../shared/scope/load-error';
import { endpointLabel, statusFromLabel, statusLabel, statusTone } from '../stock-transfer-rules';
import { StockTransfersService } from '../stock-transfers.service';
import { STOCK_TRANSFER_STATUSES } from '../stock-transfers.models';
import type { StockTransferDirection, StockTransferListItem, StockTransferStatus } from '../stock-transfers.models';

const COLS: DataTableColumn[] = [
  { label: 'No', align: 'left' },
  { label: 'Date', align: 'left' },
  { label: 'From', align: 'left' },
  { label: 'To', align: 'left' },
  { label: 'Branches', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: 'Lines', align: 'right' },
  { label: 'In transit', align: 'right' },
];

const ALL_STATUSES = 'All statuses';
export const STOCK_TRANSFER_PAGE_SIZE = 20;

const branchLabel = (b: { code: string | null; name: string | null }) => b.code || b.name || 'Company';

@Component({
  selector: 'app-stock-transfer-list',
  standalone: true,
  imports: [RouterLink, UiDataTable, UiButton, LoadError, LocationSelect],
  templateUrl: './stock-transfer-list.html',
  styleUrl: './stock-transfer-list.scss',
})
export class StockTransferList implements OnInit {
  private readonly service = inject(StockTransfersService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly errorMessage = signal<string | null>(null);
  readonly status = signal<StockTransferStatus | typeof ALL_STATUSES>(ALL_STATUSES);
  readonly direction = signal<StockTransferDirection | ''>('');
  readonly sourceLocationId = signal<string | null>(null);
  readonly destinationLocationId = signal<string | null>(null);
  readonly dateFrom = signal('');
  readonly dateTo = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<StockTransferListItem[]>([]);
  readonly total = signal(0);

  readonly canViewQueue = computed(() => this.auth.hasPermission('StockTransfers.Dispatch'));
  readonly canCreate = computed(() => this.auth.hasPermission('StockTransfers.Create'));
  readonly hasMore = computed(() => this.page() * STOCK_TRANSFER_PAGE_SIZE < this.total());
  readonly hasExtraFilter = computed(
    () => !!this.direction() || !!this.sourceLocationId() || !!this.destinationLocationId() || !!this.dateFrom() || !!this.dateTo(),
  );

  readonly filters = computed<DataTableFilter[]>(() => [
    {
      value: this.status() === ALL_STATUSES ? ALL_STATUSES : statusLabel(this.status() as StockTransferStatus),
      options: [ALL_STATUSES, ...STOCK_TRANSFER_STATUSES.map(statusLabel)],
      onChange: (v) => this.onStatusChange(v === ALL_STATUSES ? ALL_STATUSES : (statusFromLabel(v) ?? ALL_STATUSES)),
    },
  ]);

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((t) => ({
      cells: [
        { kind: 'link', text: t.transferNo, onClick: () => this.openDetail(t.id) },
        { kind: 'text', text: formatDate(t.createdAt), color: 'var(--color-text-muted)' },
        { kind: 'text', text: endpointLabel(t.from), sub: t.from.type ?? undefined },
        { kind: 'text', text: endpointLabel(t.to), sub: t.to.type ?? undefined },
        { kind: 'text', text: `${branchLabel(t.sourceBranch)} → ${branchLabel(t.destinationBranch)}`, color: 'var(--color-text-muted)' },
        { kind: 'badge', text: statusLabel(t.status), tone: statusTone(t.status) },
        { kind: 'text', text: String(t.lineCount), align: 'right' },
        // Never summed across products: a count of lines that still have goods in transit.
        { kind: 'text', text: t.outstandingLineCount > 0 ? `${t.outstandingLineCount} line${t.outstandingLineCount === 1 ? '' : 's'}` : '—', align: 'right', weight: t.outstandingLineCount > 0 ? '650' : undefined },
      ],
    } satisfies DataTableRow)),
  );

  ngOnInit(): void {
    this.load();
  }

  onStatusChange(value: StockTransferStatus | typeof ALL_STATUSES): void {
    this.status.set(value);
    this.reload();
  }

  onDirectionChange(value: string): void {
    this.direction.set(value === 'incoming' || value === 'outgoing' ? value : '');
    this.reload();
  }

  onSourceChange(value: string | null): void {
    this.sourceLocationId.set(value);
    this.reload();
  }

  onDestinationChange(value: string | null): void {
    this.destinationLocationId.set(value);
    this.reload();
  }

  onDateFromChange(value: string): void {
    this.dateFrom.set(value);
    this.reload();
  }

  onDateToChange(value: string): void {
    this.dateTo.set(value);
    this.reload();
  }

  clearFilters(): void {
    this.direction.set('');
    this.sourceLocationId.set(null);
    this.destinationLocationId.set(null);
    this.dateFrom.set('');
    this.dateTo.set('');
    this.reload();
  }

  onPageChange(page: number): void {
    this.page.set(page);
    this.load();
  }

  retry(): void {
    this.load();
  }

  goToCreate(): void {
    this.router.navigate(['/inventory/stock-transfers/new']);
  }

  openDetail(id: string): void {
    this.router.navigate(['/inventory/stock-transfers', id]);
  }

  private reload(): void {
    this.page.set(1);
    this.load();
  }

  private load(): void {
    this.state.set('loading');
    this.errorMessage.set(null);
    const status = this.status();
    this.service
      .list({
        status: status === ALL_STATUSES ? undefined : status,
        direction: this.direction() || undefined,
        sourceLocationId: this.sourceLocationId() || undefined,
        destinationLocationId: this.destinationLocationId() || undefined,
        dateFrom: this.dateFrom() || undefined,
        dateTo: this.dateTo() || undefined,
        page: this.page(),
        pageSize: STOCK_TRANSFER_PAGE_SIZE,
      })
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

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' });
}
