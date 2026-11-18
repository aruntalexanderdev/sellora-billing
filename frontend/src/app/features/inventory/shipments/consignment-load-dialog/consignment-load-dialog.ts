import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { toProblem } from '../../../../core/http/api-errors';
import { IdempotentSubmission } from '../../../../core/idempotency';
import { quantityStep } from '../../../../shared/forms/quantity-input';
import { UiButton } from '../../../../shared/ui/button/button';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { ProductUnitsLookup } from '../../stock-transfers/product-units-lookup';
import { buildLoadRequest, hasLoadErrors, mapLoadProblem, validateLoad, type LoadErrors, type LoadRow } from '../shipment-rules';
import type { Consignment, ConsignmentLine, Shipment } from '../shipments.models';
import { ShipmentsService } from '../shipments.service';

interface Row extends LoadRow {
  line: ConsignmentLine;
}

/** Load dialog: loaded quantity per active line, 0 <= loaded <= allocated, in the line's own unit. Lowering a loaded line unloads it. */
@Component({
  selector: 'app-consignment-load-dialog',
  standalone: true,
  imports: [UiButton],
  templateUrl: './consignment-load-dialog.html',
  styleUrl: './consignment-load-dialog.scss',
})
export class ConsignmentLoadDialog implements OnInit {
  private readonly service = inject(ShipmentsService);
  private readonly lookup = inject(ProductUnitsLookup);
  private readonly ownSubmission = new IdempotentSubmission();

  readonly shipmentId = input.required<string>();
  readonly consignment = input.required<Consignment>();
  /** Owned by the host per (consignment, load) so the key survives closing and reopening after an ambiguous failure. */
  readonly submissionInput = input<IdempotentSubmission | null>(null, { alias: 'submission' });
  readonly done = output<Shipment>();
  readonly cancelled = output<void>();

  readonly rows = signal<Row[]>([]);
  readonly busy = signal(false);
  readonly touched = signal(false);
  readonly server = signal<LoadErrors>({ rows: {}, form: null });
  readonly step = (r: Row) => quantityStep(r.decimalPlaces);
  readonly live = computed(() => (this.touched() ? validateLoad(this.rows()) : { rows: {}, form: null }));
  readonly shown = computed(() => ({ rows: { ...this.server().rows, ...this.live().rows }, form: this.live().form ?? this.server().form }));
  readonly fmt = (n: number) => formatQuantity(n, 0);

  private get submission(): IdempotentSubmission {
    return this.submissionInput() ?? this.ownSubmission;
  }

  ngOnInit(): void {
    const lines = this.consignment().lines.filter((l) => l.status === 'Active');
    this.rows.set(
      lines.map((line) => ({
        lineId: line.id,
        line,
        allocatedQty: line.enteredQty,
        // A first load defaults to everything allocated; a correction starts from what is loaded.
        loadedQty: line.loadedBaseQty > 0 ? line.loadedQty : line.enteredQty,
        decimalPlaces: 3,
      })),
    );
    lines.forEach((line, i) =>
      this.lookup.units(line.productId).subscribe({
        next: (all) => {
          const unit = all.find((u) => u.id === line.productUnitId);
          if (unit) this.patch(i, { decimalPlaces: unit.decimalPlaces });
        },
      }),
    );
  }

  setLoaded(i: number, raw: string): void {
    this.patch(i, { loadedQty: raw === '' ? null : Number(raw) });
  }

  loadAll(): void {
    this.rows.update((rows) => rows.map((r) => ({ ...r, loadedQty: r.allocatedQty })));
    this.server.set({ rows: {}, form: null });
  }

  private patch(i: number, change: Partial<Row>): void {
    this.rows.update((rows) => rows.map((r, idx) => (idx === i ? { ...r, ...change } : r)));
    this.server.set({ rows: {}, form: null });
  }

  submit(): void {
    this.touched.set(true);
    if (hasLoadErrors(validateLoad(this.rows()))) return;
    this.busy.set(true);
    this.server.set({ rows: {}, form: null });
    const body = buildLoadRequest(this.rows());
    this.service.load(this.shipmentId(), this.consignment().id, body, this.submission.keyFor(body)).subscribe({
      next: (s) => {
        this.busy.set(false);
        this.submission.succeeded();
        this.done.emit(s);
      },
      error: (err) => {
        this.busy.set(false);
        this.submission.failed(err);
        this.server.set(mapLoadProblem(toProblem(err)));
      },
    });
  }
}
