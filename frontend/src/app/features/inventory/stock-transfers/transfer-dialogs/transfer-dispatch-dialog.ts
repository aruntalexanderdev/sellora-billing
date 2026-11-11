import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { toProblem } from '../../../../core/http/api-errors';
import { UiButton } from '../../../../shared/ui/button/button';
import { quantityStep } from '../../../../shared/forms/quantity-input';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { ProductUnitsLookup, toUnitRefs } from '../product-units-lookup';
import {
  IdempotentSubmission, baseToUnitQty, buildDispatchRequest, formatBase, hasErrors, mapRowProblem, validateDispatch,
  type DispatchRow, type RowErrors, type UnitRef,
} from '../stock-transfer-rules';
import { StockTransfersService } from '../stock-transfers.service';
import type { StockTransfer, StockTransferLine } from '../stock-transfers.models';

interface Row extends DispatchRow {
  line: StockTransferLine;
  units: UnitRef[];
}

/** Dispatch: every line listed (default = requested, 0 = not shipped), unit selectable per line. One dispatch per transfer. */
@Component({
  selector: 'app-transfer-dispatch-dialog',
  standalone: true,
  imports: [UiButton],
  templateUrl: './transfer-dispatch-dialog.html',
  styleUrl: './transfer-dialogs.scss',
})
export class TransferDispatchDialog implements OnInit {
  private readonly service = inject(StockTransfersService);
  private readonly lookup = inject(ProductUnitsLookup);
  private readonly ownSubmission = new IdempotentSubmission();

  readonly transfer = input.required<StockTransfer>();
  /** Owned by the host per (transfer, action) so it survives closing and reopening the dialog after an ambiguous failure. */
  readonly submissionInput = input<IdempotentSubmission | null>(null, { alias: 'submission' });
  private get submission(): IdempotentSubmission {
    return this.submissionInput() ?? this.ownSubmission;
  }
  readonly done = output<StockTransfer>();
  readonly cancelled = output<void>();

  readonly rows = signal<Row[]>([]);
  readonly busy = signal(false);
  readonly errors = signal<RowErrors>({ rows: {}, form: null });
  readonly touched = signal(false);
  readonly step = (r: Row) => quantityStep(r.unit?.decimalPlaces ?? 3);
  readonly fmt = (line: StockTransferLine, n: number) => formatBase(line, n);
  readonly qty = (n: number, r: Row) => formatQuantity(n, r.unit?.decimalPlaces ?? 3);
  readonly liveErrors = computed(() => (this.touched() ? validateDispatch(this.rows()) : { rows: {}, form: null }));
  readonly shown = computed(() => {
    const server = this.errors();
    const live = this.liveErrors();
    return { rows: { ...server.rows, ...live.rows }, form: live.form ?? server.form };
  });

  ngOnInit(): void {
    const lines = this.transfer().lines;
    this.rows.set(
      lines.map((line) => ({
        lineId: line.id,
        line,
        // Until the product's units load, the request unit (with 3 dp) stands in.
        units: [{ id: line.productUnitId, unitCode: line.unitCode, displayName: line.unitCode, decimalPlaces: 3, conversionFactorToBase: line.conversionFactorToBase }],
        unit: { id: line.productUnitId, unitCode: line.unitCode, displayName: line.unitCode, decimalPlaces: 3, conversionFactorToBase: line.conversionFactorToBase },
        quantity: line.requestedQty,
        requestedBaseQty: line.requestedBaseQty,
      })),
    );
    lines.forEach((line, i) =>
      this.lookup.units(line.productId).subscribe({
        next: (all) => {
          const units = toUnitRefs(all, [line.productUnitId]);
          this.patch(i, { units, unit: units.find((u) => u.id === line.productUnitId) ?? units[0] ?? null });
        },
      }),
    );
  }

  setUnit(i: number, unitId: string): void {
    const row = this.rows()[i];
    const unit = row.units.find((u) => u.id === unitId) ?? null;
    // Same requested amount expressed in the new unit; blank when it is not representable there.
    this.patch(i, { unit, quantity: unit ? baseToUnitQty(row.requestedBaseQty, unit) : null });
  }

  setQuantity(i: number, raw: string): void {
    this.patch(i, { quantity: raw === '' ? null : Number(raw) });
  }

  private patch(i: number, change: Partial<Row>): void {
    this.rows.update((rows) => rows.map((r, idx) => (idx === i ? { ...r, ...change } : r)));
    this.errors.set({ rows: {}, form: null });
  }

  submit(): void {
    this.touched.set(true);
    if (hasErrors(validateDispatch(this.rows()))) return;
    this.busy.set(true);
    this.errors.set({ rows: {}, form: null });
    const body = buildDispatchRequest(this.rows());
    this.service.dispatch(this.transfer().id, body, this.submission.keyFor(body)).subscribe({
      next: (t) => {
        this.busy.set(false);
        this.submission.succeeded();
        this.done.emit(t);
      },
      error: (err) => {
        this.busy.set(false);
        this.submission.failed(err);
        this.errors.set(mapRowProblem(toProblem(err)));
      },
    });
  }
}
