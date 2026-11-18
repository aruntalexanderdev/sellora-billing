import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../../../../core/auth/auth.service';
import { problemMessage, toProblem } from '../../../../core/http/api-errors';
import { LoadError } from '../../../../shared/scope/load-error';
import { UiCompanyViewToggle } from '../../../../shared/ui/company-view-toggle/company-view-toggle';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { endpointLabel } from '../../stock-transfers/stock-transfer-rules';
import { consignmentStatusTone, modeLabel, shipmentStatusLabel } from '../../shipments/shipment-rules';
import type { ConsignmentDirection, ConsignmentListItem } from '../../shipments/shipments.models';
import { ConsignmentsService } from '../consignments.service';

export const CONSIGNMENT_PAGE_SIZE = 20;

const OUTGOING_COLS: DataTableColumn[] = [
  { label: 'Consignment', align: 'left' },
  { label: 'Shipment', align: 'left' },
  { label: 'From', align: 'left' },
  { label: 'To', align: 'left' },
  { label: 'Mode', align: 'left' },
  { label: 'Planned', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: 'Lines', align: 'right' },
];

// Incoming readers see only their own consignment: no shipment link, no other destinations.
const INCOMING_COLS: DataTableColumn[] = [
  { label: 'Consignment', align: 'left' },
  { label: 'From', align: 'left' },
  { label: 'To', align: 'left' },
  { label: 'Mode', align: 'left' },
  { label: 'Planned', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: 'Lines', align: 'right' },
];

/** Consignments with Incoming / Outgoing tabs. Incoming rows never expose the parent shipment. */
@Component({
  selector: 'app-consignment-list',
  standalone: true,
  imports: [UiDataTable, LoadError, UiCompanyViewToggle],
  templateUrl: './consignment-list.html',
  styleUrl: './consignment-list.scss',
})
export class ConsignmentList implements OnInit {
  private readonly service = inject(ConsignmentsService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly canOutgoing = computed(() => this.auth.hasPermission('Shipments.View'));
  readonly direction = signal<ConsignmentDirection>(this.auth.hasPermission('Shipments.View') ? 'outgoing' : 'incoming');
  readonly search = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly errorMessage = signal<string | null>(null);
  readonly items = signal<ConsignmentListItem[]>([]);
  readonly total = signal(0);
  readonly hasMore = computed(() => this.page() * CONSIGNMENT_PAGE_SIZE < this.total());
  readonly cols = computed(() => (this.direction() === 'outgoing' ? OUTGOING_COLS : INCOMING_COLS));

  readonly rows = computed<DataTableRow[]>(() => {
    const outgoing = this.direction() === 'outgoing';
    return this.items().map((c) => {
      const lead: DataTableRow['cells'] = [{ kind: 'link', text: c.consignmentNo, onClick: () => this.openDetail(c.id) }];
      if (outgoing) {
        lead.push(c.shipmentId
          ? { kind: 'link', text: c.shipmentNo, onClick: () => this.router.navigate(['/inventory/shipments', c.shipmentId]) }
          : { kind: 'text', text: c.shipmentNo });
      }
      return {
        cells: [
          ...lead,
          { kind: 'text', text: endpointLabel(c.source) },
          { kind: 'text', text: endpointLabel(c.destination), sub: c.destinationBranch.code || c.destinationBranch.name || undefined },
          { kind: 'text', text: modeLabel(c.transportMode), sub: outgoing ? shipmentStatusLabel(c.shipmentStatus) : undefined },
          { kind: 'text', text: c.plannedDispatchDate ?? '—', color: 'var(--color-text-muted)' },
          { kind: 'badge', text: c.status, tone: consignmentStatusTone(c.status) },
          { kind: 'text', text: String(c.lineCount), align: 'right' },
        ],
      } satisfies DataTableRow;
    });
  });

  ngOnInit(): void {
    this.load();
  }

  setDirection(d: ConsignmentDirection): void {
    if (d === this.direction()) return;
    this.direction.set(d);
    this.items.set([]);
    this.reload();
  }

  onSearch(v: string): void {
    this.search.set(v);
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

  openDetail(id: string): void {
    this.router.navigate(['/inventory/consignments', id]);
  }

  private reload(): void {
    this.page.set(1);
    this.load();
  }

  private load(): void {
    this.state.set('loading');
    this.errorMessage.set(null);
    this.service.list({ direction: this.direction(), search: this.search() || undefined, page: this.page(), pageSize: CONSIGNMENT_PAGE_SIZE }).subscribe({
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
