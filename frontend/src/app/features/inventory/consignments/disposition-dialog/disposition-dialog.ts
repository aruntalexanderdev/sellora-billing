import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { toProblem } from '../../../../core/http/api-errors';
import { IdempotentSubmission } from '../../../../core/idempotency';
import { quantityStep } from '../../../../shared/forms/quantity-input';
import { UiButton } from '../../../../shared/ui/button/button';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { ProductUnitsLookup } from '../../stock-transfers/product-units-lookup';
import {
  DISPOSITION_ACTIONS, DISPOSITION_ACTION_LABELS, DISPOSITION_BUCKETS, DISPOSITION_NOTE, DISPOSITION_REASON_MAX, buildDispositionRequest, dispositionOpenInUnit,
  dispositionableBase, hasLoadErrors, mapDispositionProblem, validateDisposition, type DispositionRow, type LoadErrors,
} from '../../shipments/shipment-rules';
import type { ConsignmentDetail, ConsignmentDetailLine, ConsignmentDisposition, ConsignmentReceipt, DispositionAction } from '../../shipments/shipments.models';
import { ConsignmentsService } from '../consignments.service';

interface Row extends DispositionRow {
  line: ConsignmentDetailLine;
}

/**
 * Resolve-discrepancy dialog (company-level StockTransfers.Resolve): per line and bucket (damaged / rejected / missing) an action and a quantity in the
 * line's own unit, at most the posted quantity still open. One reason for the whole disposition.
 */
@Component({
  selector: 'app-disposition-dialog',
  standalone: true,
  imports: [UiButton],
  templateUrl: './disposition-dialog.html',
  styleUrl: './disposition-dialog.scss',
})
export class DispositionDialog implements OnInit {
  private readonly service = inject(ConsignmentsService);
  private readonly lookup = inject(ProductUnitsLookup);
  private readonly ownSubmission = new IdempotentSubmission();

  readonly consignment = input.required<ConsignmentDetail>();
  /** Posted receipts are the only source of dispositionable quantity. */
  readonly receipts = input.required<ConsignmentReceipt[]>();
  readonly submissionInput = input<IdempotentSubmission | null>(null, { alias: 'submission' });
  readonly done = output<ConsignmentDisposition>();
  readonly cancelled = output<void>();

  readonly note = DISPOSITION_NOTE;
  readonly reasonMax = DISPOSITION_REASON_MAX;
  readonly actions = DISPOSITION_ACTIONS;
  readonly actionLabel = (a: DispositionAction) => DISPOSITION_ACTION_LABELS[a];
  readonly rows = signal<Row[]>([]);
  readonly reason = signal('');
  readonly busy = signal(false);
  /** Unit decimals are known only once every line's units lookup returned; nothing is validated or submitted before. */
  readonly unitsReady = signal(false);
  readonly unitsError = signal<string | null>(null);
  readonly touched = signal(false);
  readonly server = signal<LoadErrors>({ rows: {}, form: null });
  readonly live = computed(() => (this.touched() ? validateDisposition(this.rows(), this.reason()) : { rows: {}, form: null }));
  readonly shown = computed(() => ({ rows: { ...this.server().rows, ...this.live().rows }, form: this.live().form ?? this.server().form }));
  readonly step = (r: Row) => quantityStep(r.decimalPlaces);
  readonly open = (r: Row) => formatQuantity(dispositionOpenInUnit(r), r.decimalPlaces);

  private get submission(): IdempotentSubmission {
    return this.submissionInput() ?? this.ownSubmission;
  }

  ngOnInit(): void {
    const rows: Row[] = [];
    for (const line of this.consignment().lines) {
      for (const bucket of DISPOSITION_BUCKETS) {
        const openBase = dispositionableBase(line, this.receipts(), bucket);
        if (openBase <= 0) continue;
        rows.push({ lineId: line.id, line, bucket, unitCode: line.unitCode, factor: line.conversionFactorToBase, decimalPlaces: 3, openBase, action: 'WriteOff', quantity: null });
      }
    }
    this.rows.set(rows);
    const products = [...new Set(rows.map((r) => r.line.productId))];
    let pending = products.length;
    if (pending === 0) this.unitsReady.set(true);
    products.forEach((productId) =>
      this.lookup.units(productId).subscribe({
        next: (all) => {
          let missing = false;
          this.rows.update((list) =>
            list.map((r) => {
              if (r.line.productId !== productId) return r;
              const unit = all.find((u) => u.id === r.line.productUnitId);
              if (!unit) missing = true;
              return unit ? { ...r, decimalPlaces: unit.decimalPlaces } : r;
            }),
          );
          if (missing) this.unitsError.set('Could not read the unit precision of a line; reload and try again.');
          if (--pending === 0 && !this.unitsError()) this.unitsReady.set(true);
        },
        error: () => this.unitsError.set('Could not load unit precision; reload and try again.'),
      }),
    );
  }

  setQuantity(i: number, raw: string): void {
    this.patch(i, { quantity: raw === '' ? null : Number(raw) });
  }

  setAction(i: number, action: string): void {
    this.patch(i, { action: action as DispositionAction });
  }

  /** Fill the whole open quantity of one row. */
  fillAll(i: number): void {
    if (!this.unitsReady()) return;
    const r = this.rows()[i];
    this.patch(i, { quantity: dispositionOpenInUnit(r) });
  }

  private patch(i: number, change: Partial<Row>): void {
    this.rows.update((rows) => rows.map((r, idx) => (idx === i ? { ...r, ...change } : r)));
    this.server.set({ rows: {}, form: null });
  }

  submit(): void {
    if (!this.unitsReady()) return;
    this.touched.set(true);
    if (hasLoadErrors(validateDisposition(this.rows(), this.reason()))) return;
    this.busy.set(true);
    this.server.set({ rows: {}, form: null });
    const body = buildDispositionRequest(this.rows(), this.reason());
    this.service.recordDisposition(this.consignment().id, body, this.submission.keyFor(body)).subscribe({
      next: (d) => {
        this.busy.set(false);
        this.submission.succeeded();
        this.done.emit(d);
      },
      error: (err) => {
        this.busy.set(false);
        this.submission.failed(err);
        this.server.set(mapDispositionProblem(toProblem(err), this.rows()));
      },
    });
  }
}
