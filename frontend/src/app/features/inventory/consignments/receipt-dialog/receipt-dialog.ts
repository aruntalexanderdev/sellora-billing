import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { toProblem } from '../../../../core/http/api-errors';
import { IdempotentSubmission } from '../../../../core/idempotency';
import { quantityStep } from '../../../../shared/forms/quantity-input';
import { UiButton } from '../../../../shared/ui/button/button';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { ProductUnitsLookup } from '../../stock-transfers/product-units-lookup';
import {
  RECEIPT_FIELDS, RECEIPT_SHORTFALL_NOTE, buildReceiptRequest, hasLoadErrors, mapReceiptProblem, remainingInUnit, remainingToAccountBase, validateReceipt,
  type LoadErrors, type ReceiptField, type ReceiptRow,
} from '../../shipments/shipment-rules';
import type { ConsignmentDetail, ConsignmentDetailLine, ConsignmentReceipt } from '../../shipments/shipments.models';
import { ConsignmentsService } from '../consignments.service';

interface Row extends ReceiptRow {
  line: ConsignmentDetailLine;
}

/**
 * Record-receipt dialog: per line accepted / damaged / rejected / missing in the line's own unit.
 * Their sum may not exceed what is left to account for (dispatched minus already accounted). Recording moves no stock.
 */
@Component({
  selector: 'app-receipt-dialog',
  standalone: true,
  imports: [UiButton],
  templateUrl: './receipt-dialog.html',
  styleUrl: './receipt-dialog.scss',
})
export class ReceiptDialog implements OnInit {
  private readonly service = inject(ConsignmentsService);
  private readonly lookup = inject(ProductUnitsLookup);
  private readonly ownSubmission = new IdempotentSubmission();

  readonly consignment = input.required<ConsignmentDetail>();
  /** Owned by the host so the key survives closing and reopening after an ambiguous failure. */
  readonly submissionInput = input<IdempotentSubmission | null>(null, { alias: 'submission' });
  readonly done = output<ConsignmentReceipt>();
  readonly cancelled = output<void>();

  readonly fields = RECEIPT_FIELDS;
  readonly shortfallNote = RECEIPT_SHORTFALL_NOTE;
  readonly rows = signal<Row[]>([]);
  readonly notes = signal('');
  readonly busy = signal(false);
  /** Unit decimals are known only once every line's units lookup returned; nothing is validated or submitted before. */
  readonly unitsReady = signal(false);
  readonly unitsError = signal<string | null>(null);
  readonly touched = signal(false);
  readonly server = signal<LoadErrors>({ rows: {}, form: null });
  readonly live = computed(() => (this.touched() ? validateReceipt(this.rows()) : { rows: {}, form: null }));
  readonly shown = computed(() => ({ rows: { ...this.server().rows, ...this.live().rows }, form: this.live().form ?? this.server().form }));
  readonly step = (r: Row) => quantityStep(r.decimalPlaces);
  readonly remaining = (r: Row) => formatQuantity(remainingInUnit(r), r.decimalPlaces);

  private get submission(): IdempotentSubmission {
    return this.submissionInput() ?? this.ownSubmission;
  }

  ngOnInit(): void {
    const lines = this.consignment().lines.filter((l) => remainingToAccountBase(l) > 0);
    this.rows.set(
      lines.map((line) => ({
        lineId: line.id,
        line,
        unitCode: line.unitCode,
        factor: line.conversionFactorToBase,
        decimalPlaces: 3,
        remainingBase: remainingToAccountBase(line),
        accepted: null,
        damaged: null,
        rejected: null,
        missing: null,
      })),
    );
    let pending = lines.length;
    if (pending === 0) this.unitsReady.set(true);
    lines.forEach((line, i) =>
      this.lookup.units(line.productId).subscribe({
        next: (all) => {
          const unit = all.find((u) => u.id === line.productUnitId);
          if (unit) this.patch(i, { decimalPlaces: unit.decimalPlaces });
          else this.unitsError.set('Could not read the unit precision of a line; reload and try again.');
          if (--pending === 0 && !this.unitsError()) this.unitsReady.set(true);
        },
        error: () => this.unitsError.set('Could not load unit precision; reload and try again.'),
      }),
    );
  }

  set(i: number, field: ReceiptField, raw: string): void {
    this.patch(i, { [field]: raw === '' ? null : Number(raw) } as Partial<Row>);
  }

  value(r: Row, f: ReceiptField): number | string {
    return r[f] ?? '';
  }

  /** Everything still outstanding is accepted (clears the other quantities). */
  acceptAll(): void {
    if (!this.unitsReady()) return;
    this.rows.update((rows) =>
      rows.map((r) => ({ ...r, accepted: remainingInUnit(r), damaged: null, rejected: null, missing: null })),
    );
    this.server.set({ rows: {}, form: null });
  }

  private patch(i: number, change: Partial<Row>): void {
    this.rows.update((rows) => rows.map((r, idx) => (idx === i ? { ...r, ...change } : r)));
    this.server.set({ rows: {}, form: null });
  }

  submit(): void {
    if (!this.unitsReady()) return;
    this.touched.set(true);
    if (hasLoadErrors(validateReceipt(this.rows()))) return;
    this.busy.set(true);
    this.server.set({ rows: {}, form: null });
    const body = buildReceiptRequest(this.rows(), this.notes());
    this.service.recordReceipt(this.consignment().id, body, this.submission.keyFor(body)).subscribe({
      next: (receipt) => {
        this.busy.set(false);
        this.submission.succeeded();
        this.done.emit(receipt);
      },
      error: (err) => {
        this.busy.set(false);
        this.submission.failed(err);
        this.server.set(mapReceiptProblem(toProblem(err), this.rows()));
      },
    });
  }
}
