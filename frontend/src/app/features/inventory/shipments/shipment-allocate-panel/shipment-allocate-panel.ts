import { Component, OnInit, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { toProblem } from '../../../../core/http/api-errors';
import { IdempotentSubmission } from '../../../../core/idempotency';
import { quantityStep } from '../../../../shared/forms/quantity-input';
import { problemText } from '../../../../shared/scope/problem-text';
import { UiButton } from '../../../../shared/ui/button/button';
import { StockService } from '../../stock/stock.service';
import { ProductUnitsLookup, toUnitRefs } from '../../stock-transfers/product-units-lookup';
import { endpointLabel } from '../../stock-transfers/stock-transfer-rules';
import type { ReadyForShipmentItem, StockTransfer, StockTransferLine } from '../../stock-transfers/stock-transfers.models';
import { StockTransfersService } from '../../stock-transfers/stock-transfers.service';
import {
  baseOf, floorUnitQty, limitsView, maxAllocatableBase, plannerProblemMessage, requestRemainingBase, validateAllocation,
  type AllocUnit, type AllocationLimits, type LimitsView,
} from '../shipment-rules';
import type { Shipment } from '../shipments.models';
import { ShipmentsService } from '../shipments.service';

export interface AllocLine {
  line: StockTransferLine;
  units: AllocUnit[];
  unitId: string;
  /** Unit forced by an existing allocation of this line on the shipment (the allocation keeps its unit). */
  lockedUnitId: string | null;
  qty: number | null;
  /** The user typed a quantity: stop re-applying the computed default. */
  edited: boolean;
  availableBase: number | null;
  error: string | null;
  busy: boolean;
  submission: IdempotentSubmission;
}

export interface AllocRequest {
  transfer: StockTransfer;
  lines: AllocLine[];
}

/** "Add requests": the Ready-for-shipment queue filtered to the shipment's source; per request line quantity entry in a unit. */
@Component({
  selector: 'app-shipment-allocate-panel',
  standalone: true,
  imports: [UiButton],
  templateUrl: './shipment-allocate-panel.html',
  styleUrl: './shipment-allocate-panel.scss',
})
export class ShipmentAllocatePanel implements OnInit {
  private readonly transfers = inject(StockTransfersService);
  private readonly stock = inject(StockService);
  private readonly lookup = inject(ProductUnitsLookup);
  private readonly shipments = inject(ShipmentsService);

  readonly shipment = input.required<Shipment>();
  readonly updated = output<Shipment>();

  readonly items = signal<ReadyForShipmentItem[]>([]);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly search = signal('');
  readonly open = signal<Record<string, AllocRequest | 'loading' | undefined>>({});
  readonly label = endpointLabel;
  readonly step = (l: AllocLine) => quantityStep(this.unitOf(l)?.decimalPlaces ?? 3);

  readonly requests = computed(() => this.items());

  constructor() {
    // A fresh shipment state (after an add / change) refreshes the locks and remaining quantities of open requests.
    effect(() => {
      this.shipment();
      untracked(() => this.refreshOpen());
    });
  }

  ngOnInit(): void {
    this.loadQueue();
  }

  loadQueue(): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.transfers.readyForShipment({ sourceLocationId: this.shipment().source.id, search: this.search() || undefined, pageSize: 50 }).subscribe({
      next: (r) => {
        this.items.set(r.items);
        this.loading.set(false);
      },
      error: (err) => {
        this.loadError.set(problemText(err, 'Could not load the ready-for-shipment queue.'));
        this.loading.set(false);
      },
    });
  }

  onSearch(value: string): void {
    this.search.set(value);
    this.loadQueue();
  }

  toggle(item: ReadyForShipmentItem): void {
    const cur = this.open();
    if (cur[item.id]) {
      const { [item.id]: _drop, ...rest } = cur;
      this.open.set(rest);
      return;
    }
    this.open.update((o) => ({ ...o, [item.id]: 'loading' }));
    this.loadRequest(item.id);
  }

  request(id: string): AllocRequest | null {
    const v = this.open()[id];
    return v && v !== 'loading' ? v : null;
  }

  isLoading(id: string): boolean {
    return this.open()[id] === 'loading';
  }

  unitOf(l: AllocLine): AllocUnit | null {
    return l.units.find((u) => u.id === l.unitId) ?? null;
  }

  limits(l: AllocLine): AllocationLimits {
    return { remainingBase: requestRemainingBase(l.line), availableBase: l.availableBase };
  }

  view(l: AllocLine): LimitsView {
    return limitsView(l.line.requestedBaseQty, this.limits(l), this.unitOf(l), l.line.baseUnitCode, l.line.baseDecimalPlaces ?? 3);
  }

  canAdd(l: AllocLine): boolean {
    return requestRemainingBase(l.line) > 0;
  }

  setUnit(req: AllocRequest, l: AllocLine, unitId: string): void {
    this.patch(req, l, { unitId, qty: this.defaultQty(l, unitId), edited: false, error: null });
  }

  setQty(req: AllocRequest, l: AllocLine, raw: string): void {
    this.patch(req, l, { qty: raw === '' ? null : Number(raw), edited: true, error: null });
  }

  add(req: AllocRequest, l: AllocLine): void {
    const unit = this.unitOf(l);
    const error = validateAllocation(l.qty, unit, this.limits(l));
    if (error) {
      this.patch(req, l, { error });
      return;
    }
    const body = { stockTransferLineId: l.line.id, productUnitId: unit!.id, quantity: l.qty! };
    this.patch(req, l, { busy: true, error: null });
    this.shipments.allocate(this.shipment().id, body, l.submission.keyFor(body)).subscribe({
      next: (s) => {
        l.submission.succeeded();
        // The parent re-binds the shipment; the effect above refreshes this request's remaining quantities and unit lock.
        this.updated.emit(s);
      },
      error: (err) => {
        l.submission.failed(err);
        this.patch(req, l, { busy: false, error: plannerProblemMessage(toProblem(err)) });
      },
    });
  }

  private refreshOpen(): void {
    for (const [id, v] of Object.entries(untracked(() => this.open()))) {
      if (v && v !== 'loading') this.loadRequest(id);
    }
  }

  private lockFor(lineId: string): string | null {
    for (const c of this.shipment().consignments) {
      if (c.status === 'Cancelled') continue;
      const hit = c.lines.find((x) => x.status === 'Active' && x.stockTransferLineId === lineId);
      if (hit) return hit.productUnitId;
    }
    return null;
  }

  private loadRequest(id: string): void {
    this.transfers.get(id).subscribe({
      next: (t) => {
        const previous = this.request(id);
        const lines: AllocLine[] = t.lines.map((line) => {
          const prev = previous?.lines.find((p) => p.line.id === line.id);
          const lockedUnitId = this.lockFor(line.id);
          return {
            line,
            units: prev?.units ?? [{ id: line.productUnitId, unitCode: line.unitCode, displayName: line.unitCode, decimalPlaces: 3, conversionFactorToBase: line.conversionFactorToBase }],
            unitId: prev?.unitId ?? lockedUnitId ?? line.productUnitId,
            lockedUnitId,
            qty: prev?.qty ?? null,
            edited: prev?.edited ?? false,
            availableBase: prev?.availableBase ?? null,
            error: null,
            busy: false,
            submission: prev?.submission ?? new IdempotentSubmission(),
          };
        });
        this.open.update((o) => ({ ...o, [id]: { transfer: t, lines } }));
        lines.forEach((l) => {
          this.lookup.units(l.line.productId).subscribe({
            next: (all) => {
              const units = toUnitRefs(all, [l.line.productUnitId, l.lockedUnitId]);
              this.patchById(id, l.line.id, { units });
              this.applyDefault(id, l.line.id);
            },
          });
          this.stock.detail(l.line.productId, this.shipment().source.id).subscribe({
            next: (d) => {
              const bal = d.locations?.find((b) => b.locationId === this.shipment().source.id);
              // Review fix: net of other shipments' reservations (on hand - reserved), the same figure the server checks.
              this.patchById(id, l.line.id, { availableBase: bal ? (bal.availableQuantity ?? bal.qty - (bal.reservedQuantity ?? 0)) : 0 });
              this.applyDefault(id, l.line.id);
            },
            error: () => undefined,
          });
        });
      },
      error: (err) => {
        this.open.update((o) => ({ ...o, [id]: undefined }));
        this.loadError.set(problemText(err, 'Could not load this request.'));
      },
    });
  }

  /** Re-derives the suggested quantity (units / availability arrive asynchronously) unless the user already typed one. */
  private applyDefault(requestId: string, lineId: string): void {
    const current = this.findLine(requestId, lineId);
    if (current && !current.edited) this.patchById(requestId, lineId, { qty: this.defaultQty(current, current.unitId) });
  }

  /** The most that can be added in the unit: min(remaining, available) floored to the unit's precision; null when nothing fits. */
  private defaultQty(l: AllocLine, unitId: string): number | null {
    const unit = l.units.find((u) => u.id === unitId);
    if (!unit) return null;
    const max = floorUnitQty(maxAllocatableBase(this.limits(l)), unit);
    return max > 0 && baseOf(max, unit) > 0 ? max : null;
  }

  private findLine(requestId: string, lineId: string): AllocLine | null {
    return this.request(requestId)?.lines.find((x) => x.line.id === lineId) ?? null;
  }

  private patch(req: AllocRequest, l: AllocLine, change: Partial<AllocLine>): void {
    this.patchById(req.transfer.id, l.line.id, change);
  }

  private patchById(requestId: string, lineId: string, change: Partial<AllocLine>): void {
    this.open.update((o) => {
      const cur = o[requestId];
      if (!cur || cur === 'loading') return o;
      return { ...o, [requestId]: { ...cur, lines: cur.lines.map((x) => (x.line.id === lineId ? { ...x, ...change } : x)) } };
    });
  }
}
