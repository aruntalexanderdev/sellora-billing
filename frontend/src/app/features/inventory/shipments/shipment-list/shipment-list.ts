import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../../../../core/auth/auth.service';
import { problemMessage, toProblem } from '../../../../core/http/api-errors';
import { LocationSelect } from '../../../../shared/locations/location-select';
import { LoadError } from '../../../../shared/scope/load-error';
import { UiButton } from '../../../../shared/ui/button/button';
import { UiCompanyViewToggle } from '../../../../shared/ui/company-view-toggle/company-view-toggle';
import { UiDataTable } from '../../../../shared/ui/data-table/data-table';
import type { DataTableColumn, DataTableFilter, DataTableRow, DataTableState } from '../../../../shared/ui/data-table/data-table.types';
import { endpointLabel } from '../../stock-transfers/stock-transfer-rules';
import { modeLabel, referenceLabel, shipmentStatusLabel, shipmentStatusTone, TRANSPORT_MODE_LABELS, SHIPMENT_STATUS_LABELS } from '../shipment-rules';
import { SHIPMENT_STATUSES, TRANSPORT_MODES } from '../shipments.models';
import type { ShipmentListItem, ShipmentStatus, TransportMode } from '../shipments.models';
import { ShipmentsService } from '../shipments.service';

const COLS: DataTableColumn[] = [
  { label: 'Shipment', align: 'left' },
  { label: 'Source', align: 'left' },
  { label: 'Mode', align: 'left' },
  { label: 'Reference', align: 'left' },
  { label: 'Planned', align: 'left' },
  { label: 'Status', align: 'left' },
  { label: 'Consignments', align: 'right' },
  { label: 'Lines', align: 'right' },
];

const ALL_STATUSES = 'All statuses';
const ALL_MODES = 'All modes';
export const SHIPMENT_PAGE_SIZE = 20;

/** Shipments list (Shipments.View): status / mode / source / search filters and the Company view. */
@Component({
  selector: 'app-shipment-list',
  standalone: true,
  imports: [UiDataTable, UiButton, LoadError, LocationSelect, UiCompanyViewToggle],
  templateUrl: './shipment-list.html',
  styleUrl: '../../stock-transfers/stock-transfer-list/stock-transfer-list.scss',
})
export class ShipmentList implements OnInit {
  private readonly service = inject(ShipmentsService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly cols = COLS;
  readonly errorMessage = signal<string | null>(null);
  readonly status = signal<ShipmentStatus | null>(null);
  readonly mode = signal<TransportMode | null>(null);
  readonly sourceLocationId = signal<string | null>(null);
  readonly search = signal('');
  readonly page = signal(1);
  readonly state = signal<DataTableState>('loading');
  readonly items = signal<ShipmentListItem[]>([]);
  readonly total = signal(0);

  readonly canPlan = computed(() => this.auth.hasPermission('Shipments.Plan'));
  readonly hasMore = computed(() => this.page() * SHIPMENT_PAGE_SIZE < this.total());

  readonly filters = computed<DataTableFilter[]>(() => [
    {
      value: this.status() ? shipmentStatusLabel(this.status()!) : ALL_STATUSES,
      options: [ALL_STATUSES, ...SHIPMENT_STATUSES.map(shipmentStatusLabel)],
      onChange: (v) => this.onStatusChange(SHIPMENT_STATUSES.find((s) => SHIPMENT_STATUS_LABELS[s] === v) ?? null),
    },
    {
      value: this.mode() ? modeLabel(this.mode()!) : ALL_MODES,
      options: [ALL_MODES, ...TRANSPORT_MODES.map(modeLabel)],
      onChange: (v) => this.onModeChange(TRANSPORT_MODES.find((m) => TRANSPORT_MODE_LABELS[m] === v) ?? null),
    },
  ]);

  readonly rows = computed<DataTableRow[]>(() =>
    this.items().map((s) => ({
      cells: [
        { kind: 'link', text: s.shipmentNo, onClick: () => this.open(s.id) },
        { kind: 'text', text: endpointLabel(s.source), sub: s.branch.code || s.branch.name || 'Company' },
        { kind: 'text', text: modeLabel(s.transportMode), sub: s.providerName ?? undefined },
        { kind: 'text', text: s.transportReference ?? '—', sub: s.transportReference ? referenceLabel(s.transportMode) : undefined },
        { kind: 'text', text: s.plannedDispatchDate ?? '—', color: 'var(--color-text-muted)' },
        { kind: 'badge', text: shipmentStatusLabel(s.status), tone: shipmentStatusTone(s.status) },
        { kind: 'text', text: String(s.consignmentCount), align: 'right' },
        { kind: 'text', text: String(s.lineCount), align: 'right' },
      ],
    } satisfies DataTableRow)),
  );

  ngOnInit(): void {
    this.load();
  }

  onStatusChange(v: ShipmentStatus | null): void {
    this.status.set(v);
    this.reload();
  }

  onModeChange(v: TransportMode | null): void {
    this.mode.set(v);
    this.reload();
  }

  onSourceChange(v: string | null): void {
    this.sourceLocationId.set(v);
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

  goToCreate(): void {
    this.router.navigate(['/inventory/shipments/new']);
  }

  open(id: string): void {
    this.router.navigate(['/inventory/shipments', id]);
  }

  private reload(): void {
    this.page.set(1);
    this.load();
  }

  private load(): void {
    this.state.set('loading');
    this.errorMessage.set(null);
    this.service
      .list({
        status: this.status() ?? undefined,
        mode: this.mode() ?? undefined,
        sourceLocationId: this.sourceLocationId(),
        search: this.search() || undefined,
        page: this.page(),
        pageSize: SHIPMENT_PAGE_SIZE,
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
