import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService } from '../../../../core/auth/auth.service';
import { toProblem } from '../../../../core/http/api-errors';
import { IdempotentSubmission } from '../../../../core/idempotency';
import { loadErrorText } from '../../../../shared/scope/problem-text';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { ReasonPrompt } from '../../../administration/access-ui/reason-prompt';
import { ProductUnitsLookup } from '../../stock-transfers/product-units-lookup';
import { endpointLabel } from '../../stock-transfers/stock-transfer-rules';
import { ConsignmentLoadDialog } from '../consignment-load-dialog/consignment-load-dialog';
import { ShipmentAllocatePanel } from '../shipment-allocate-panel/shipment-allocate-panel';
import { ShipmentForm } from '../shipment-form/shipment-form';
import { ShipmentVehiclesPanel } from '../shipment-vehicles-panel/shipment-vehicles-panel';
import {
  activeLines, consignmentStatusTone, liveConsignments, missingLegs, modeLabel, plannerProblemMessage, referenceLabel, shipmentCapabilities,
  shipmentStatusLabel, shipmentStatusTone, validateAllocationTotal, vehicleLabel, TRANSPORT_VIA_LABELS,
} from '../shipment-rules';
import type { Consignment, ConsignmentLine, Shipment } from '../shipments.models';
import { ShipmentsService } from '../shipments.service';

type Panel = { kind: 'header' } | { kind: 'cancelShipment' } | { kind: 'cancelConsignment'; id: string } | { kind: 'load'; id: string };

/** Shipment planner: header, vehicles, add requests, consignments per destination with allocations, vehicle legs, loading. */
@Component({
  selector: 'app-shipment-detail',
  standalone: true,
  imports: [RouterLink, DatePipe, UiBadge, UiButton, ReasonPrompt, ShipmentForm, ShipmentVehiclesPanel, ShipmentAllocatePanel, ConsignmentLoadDialog],
  templateUrl: './shipment-detail.html',
  styleUrl: './shipment-detail.scss',
})
export class ShipmentDetail implements OnInit {
  private readonly service = inject(ShipmentsService);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly toast = inject(ToastService);
  private readonly lookup = inject(ProductUnitsLookup);
  private readonly submissions = new Map<string, IdempotentSubmission>();

  readonly shipment = signal<Shipment | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly panel = signal<Panel | null>(null);
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);
  /** Allocation being edited inline: new TOTAL in the allocation's unit. */
  readonly editing = signal<{ lineId: string; total: number | null; error: string | null } | null>(null);
  /** decimalPlaces per product-unit id (for validating allocation totals). */
  readonly decimals = signal<Record<string, number>>({});

  readonly caps = computed(() => shipmentCapabilities(this.shipment()?.status ?? 'Cancelled', (c) => this.auth.hasPermission(c)));
  readonly live = computed(() => liveConsignments(this.shipment()?.consignments ?? []));
  readonly cancelledConsignments = computed(() => (this.shipment()?.consignments ?? []).filter((c) => c.status === 'Cancelled'));
  readonly legsMissing = computed(() => {
    const s = this.shipment();
    return s ? missingLegs(s.transportVia, s.transportMode, s.consignments) : [];
  });
  readonly referenceMissing = computed(() => {
    const s = this.shipment();
    return !!s && s.status !== 'Cancelled' && !s.transportReferenceValid;
  });

  readonly tone = shipmentStatusTone;
  readonly statusText = shipmentStatusLabel;
  readonly consignmentTone = consignmentStatusTone;
  readonly mode = modeLabel;
  readonly refLabel = referenceLabel;
  readonly viaLabels = TRANSPORT_VIA_LABELS;
  readonly place = endpointLabel;
  readonly lines = activeLines;
  readonly vehicleName = vehicleLabel;
  readonly qty = (n: number) => formatQuantity(n, 0);

  constructor() {
    effect(() => {
      const s = this.shipment();
      if (!s) return;
      const productIds = new Set(s.consignments.flatMap((c) => c.lines.map((l) => l.productId)));
      productIds.forEach((pid) =>
        this.lookup.units(pid).subscribe({
          next: (units) => this.decimals.update((d) => ({ ...d, ...Object.fromEntries(units.map((u) => [u.id, u.decimalPlaces])) })),
        }),
      );
    });
  }

  ngOnInit(): void {
    this.service.get(this.route.snapshot.paramMap.get('id')!).subscribe({
      next: (s) => {
        this.shipment.set(s);
        this.loading.set(false);
      },
      error: (err) => {
        this.loadError.set(loadErrorText(err, 'Could not load this shipment.'));
        this.loading.set(false);
      },
    });
  }

  // ---- state ----

  /** A write finished: show the server's new state. */
  onUpdated(s: Shipment, message?: string): void {
    this.shipment.set(s);
    this.panel.set(null);
    this.editing.set(null);
    this.actionError.set(null);
    if (message) this.toast.show(message);
  }

  open(panel: Panel): void {
    this.actionError.set(null);
    this.panel.set(panel);
  }

  closePanel(): void {
    const p = this.panel();
    this.panel.set(null);
    this.actionError.set(null);
    // An ambiguous failure may have been processed: re-read, keep the key until acknowledged.
    if (p) {
      const sub = this.submissionFor(this.panelKey(p));
      if (sub.hasAmbiguousFailure) {
        sub.acknowledged();
        this.refresh();
      }
    }
  }

  isPanel(kind: Panel['kind'], id?: string): boolean {
    const p = this.panel();
    return !!p && p.kind === kind && (id === undefined || ('id' in p && p.id === id));
  }

  submissionFor(name: string): IdempotentSubmission {
    const k = `${this.shipment()?.id ?? ''}:${name}`;
    let sub = this.submissions.get(k);
    if (!sub) this.submissions.set(k, (sub = new IdempotentSubmission()));
    return sub;
  }

  private panelKey(p: Panel): string {
    return p.kind === 'cancelShipment' ? 'cancel' : p.kind === 'load' ? `load:${p.id}` : p.kind;
  }

  private refresh(): void {
    this.service.get(this.shipment()!.id).subscribe({ next: (s) => this.shipment.set(s) });
  }

  // ---- actions ----

  cancelShipment(reason: string | null): void {
    const s = this.shipment();
    if (!s || !reason || this.busy()) return;
    const sub = this.submissionFor('cancel');
    this.busy.set(true);
    this.actionError.set(null);
    this.service.cancel(s.id, reason, sub.key).subscribe({
      next: (u) => {
        this.busy.set(false);
        sub.succeeded();
        this.onUpdated(u, 'Shipment cancelled; its reservations were released.');
      },
      error: (err) => this.fail(err, sub),
    });
  }

  cancelConsignment(consignmentId: string, reason: string | null): void {
    const s = this.shipment();
    if (!s || !reason || this.busy()) return;
    this.run(this.service.cancelConsignment(s.id, consignmentId, reason), 'Consignment cancelled.');
  }

  removeVehicleLeg(c: Consignment): void {
    this.assignVehicle(c, '');
  }

  assignVehicle(c: Consignment, shipmentVehicleId: string): void {
    const s = this.shipment();
    if (!s || this.busy()) return;
    this.run(this.service.assignVehicle(s.id, c.id, shipmentVehicleId || null), 'Vehicle leg updated.');
  }

  removeAllocation(l: ConsignmentLine): void {
    const s = this.shipment();
    if (!s || this.busy()) return;
    this.run(this.service.removeAllocation(s.id, l.id), 'Allocation removed; its reservation was released.');
  }

  // ---- change allocation (inline) ----

  startEdit(l: ConsignmentLine): void {
    this.actionError.set(null);
    this.editing.set({ lineId: l.id, total: l.enteredQty, error: null });
  }

  setEditTotal(raw: string): void {
    this.editing.update((e) => (e ? { ...e, total: raw === '' ? null : Number(raw), error: null } : e));
  }

  /** Extra base units the allocation may grow by: the request's remaining unallocated quantity (the server also checks stock). */
  private extraAllowed(l: ConsignmentLine): number {
    return l.request.remainingUnallocatedBaseQty;
  }

  decimalsOf(l: ConsignmentLine): number {
    return this.decimals()[l.productUnitId] ?? 3;
  }

  saveEdit(l: ConsignmentLine): void {
    const s = this.shipment();
    const e = this.editing();
    if (!s || !e || e.lineId !== l.id || this.busy()) return;
    const error = validateAllocationTotal(e.total, l, this.decimalsOf(l), this.extraAllowed(l));
    if (error) {
      this.editing.set({ ...e, error });
      return;
    }
    const sub = this.submissionFor(`change:${l.id}`);
    const total = e.total!;
    this.busy.set(true);
    this.service.changeAllocation(s.id, l.id, total, sub.keyFor({ total })).subscribe({
      next: (u) => {
        this.busy.set(false);
        sub.succeeded();
        this.onUpdated(u, total === 0 ? 'Allocation removed.' : 'Allocation updated.');
      },
      error: (err) => {
        this.busy.set(false);
        sub.failed(err);
        this.editing.set({ ...e, error: plannerProblemMessage(toProblem(err)) });
      },
    });
  }

  private run(call: ReturnType<ShipmentsService['removeAllocation']>, success: string): void {
    this.busy.set(true);
    this.actionError.set(null);
    call.subscribe({
      next: (u) => {
        this.busy.set(false);
        this.onUpdated(u, success);
      },
      error: (err) => {
        this.busy.set(false);
        this.actionError.set(plannerProblemMessage(toProblem(err)));
      },
    });
  }

  private fail(err: unknown, sub: IdempotentSubmission): void {
    this.busy.set(false);
    sub.failed(err);
    this.actionError.set(plannerProblemMessage(toProblem(err)));
  }

  consignmentOf(id: string): Consignment | null {
    return this.shipment()?.consignments.find((c) => c.id === id) ?? null;
  }

  loadedSummary(c: Consignment): string {
    const lines = activeLines(c);
    const loaded = lines.filter((l) => l.loadedBaseQty >= l.allocatedBaseQty && l.allocatedBaseQty > 0).length;
    return `${loaded} of ${lines.length} lines fully loaded`;
  }
}
