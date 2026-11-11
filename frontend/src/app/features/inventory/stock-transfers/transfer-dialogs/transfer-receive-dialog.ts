import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { toProblem } from '../../../../core/http/api-errors';
import { UiButton } from '../../../../shared/ui/button/button';
import { quantityStep } from '../../../../shared/forms/quantity-input';
import { ProductUnitsLookup, toUnitRefs } from '../product-units-lookup';
import {
  IdempotentSubmission, baseToUnitQty, buildReceiveRequest, formatBase, hasErrors, lineUnitId, mapRowProblem, validateReceive,
  type ReceiveRow, type RowErrors, type UnitRef,
} from '../stock-transfer-rules';
import { StockTransfersService } from '../stock-transfers.service';
import type { StockTransfer, StockTransferLine } from '../stock-transfers.models';

interface Row extends ReceiveRow {
  line: StockTransferLine;
  units: UnitRef[];
}

const NO_ERRORS: RowErrors = { rows: {}, form: null };

/** Receipt: accepted + damaged per outstanding line (<= outstanding; partial allowed, several receipts per transfer). */
@Component({
  selector: 'app-transfer-receive-dialog',
  standalone: true,
  imports: [UiButton],
  templateUrl: './transfer-receive-dialog.html',
  styleUrl: './transfer-dialogs.scss',
})
export class TransferReceiveDialog implements OnInit {
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
  readonly notes = signal('');
  readonly busy = signal(false);
  readonly errors = signal<RowErrors>(NO_ERRORS);
  readonly touched = signal(false);
  readonly step = (r: Row) => quantityStep(r.unit?.decimalPlaces ?? 3);
  readonly fmt = (line: StockTransferLine, n: number) => formatBase(line, n);
  readonly liveErrors = computed(() => (this.touched() ? validateReceive(this.rows()) : NO_ERRORS));
  readonly shown = computed(() => {
    const server = this.errors();
    const live = this.liveErrors();
    return { rows: { ...server.rows, ...live.rows }, form: live.form ?? server.form };
  });

  ngOnInit(): void {
    const lines = this.transfer().lines.filter((l) => l.outstandingBaseQty > 0);
    this.rows.set(
      lines.map((line) => {
        const unit: UnitRef = {
          id: lineUnitId(line),
          unitCode: line.dispatchUnitCode ?? line.unitCode,
          displayName: line.dispatchUnitCode ?? line.unitCode,
          decimalPlaces: 3,
          conversionFactorToBase: line.dispatchFactor ?? line.conversionFactorToBase,
        };
        return { lineId: line.id, line, units: [unit], unit, accepted: baseToUnitQty(line.outstandingBaseQty, unit), damaged: 0, outstandingBaseQty: line.outstandingBaseQty };
      }),
    );
    lines.forEach((line, i) =>
      this.lookup.units(line.productId).subscribe({
        next: (all) => {
          const units = toUnitRefs(all, [lineUnitId(line)]);
          // Prefer the dispatch unit; fall back to the first unit that represents the outstanding quantity exactly.
          const preferred = units.find((u) => u.id === lineUnitId(line));
          const unit = preferred && baseToUnitQty(line.outstandingBaseQty, preferred) !== null
            ? preferred
            : (units.find((u) => baseToUnitQty(line.outstandingBaseQty, u) !== null) ?? preferred ?? units[0] ?? null);
          this.patch(i, { units, unit, accepted: unit ? baseToUnitQty(line.outstandingBaseQty, unit) : null, damaged: 0 });
        },
      }),
    );
  }

  setUnit(i: number, unitId: string): void {
    const row = this.rows()[i];
    const unit = row.units.find((u) => u.id === unitId) ?? null;
    this.patch(i, { unit, accepted: unit ? baseToUnitQty(row.outstandingBaseQty, unit) : null, damaged: 0 });
  }

  setAccepted(i: number, raw: string): void {
    this.patch(i, { accepted: raw === '' ? null : Number(raw) });
  }

  setDamaged(i: number, raw: string): void {
    this.patch(i, { damaged: raw === '' ? null : Number(raw) });
  }

  private patch(i: number, change: Partial<Row>): void {
    this.rows.update((rows) => rows.map((r, idx) => (idx === i ? { ...r, ...change } : r)));
    this.errors.set(NO_ERRORS);
  }

  submit(): void {
    this.touched.set(true);
    if (hasErrors(validateReceive(this.rows()))) return;
    this.busy.set(true);
    this.errors.set(NO_ERRORS);
    const body = buildReceiveRequest(this.rows(), this.notes());
    this.service.receive(this.transfer().id, body, this.submission.keyFor(body)).subscribe({
      next: (res) => {
        this.busy.set(false);
        this.submission.succeeded();
        this.done.emit(res.transfer);
      },
      error: (err) => {
        this.busy.set(false);
        this.submission.failed(err);
        this.errors.set(mapRowProblem(toProblem(err)));
      },
    });
  }
}
