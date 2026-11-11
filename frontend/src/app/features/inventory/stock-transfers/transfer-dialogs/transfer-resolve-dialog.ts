import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { toProblem } from '../../../../core/http/api-errors';
import { UiButton } from '../../../../shared/ui/button/button';
import {
  IdempotentSubmission, RESOLVE_REASON_MAX, buildResolveRequest, formatBase, hasErrors, mapRowProblem, validateResolve,
  type ResolveRow, type RowErrors,
} from '../stock-transfer-rules';
import { StockTransfersService } from '../stock-transfers.service';
import type { StockTransfer, StockTransferLine, StockTransferResolution } from '../stock-transfers.models';

interface Row extends ResolveRow {
  line: StockTransferLine;
}

const NO_ERRORS: RowErrors = { rows: {}, form: null };

/** Resolve (StockTransfers.Resolve, company-level): close every outstanding line as WriteOff (loss) or ReturnToSource. */
@Component({
  selector: 'app-transfer-resolve-dialog',
  standalone: true,
  imports: [UiButton],
  templateUrl: './transfer-resolve-dialog.html',
  styleUrl: './transfer-dialogs.scss',
})
export class TransferResolveDialog implements OnInit {
  private readonly service = inject(StockTransfersService);
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
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly errors = signal<RowErrors>(NO_ERRORS);
  readonly touched = signal(false);
  readonly reasonMax = RESOLVE_REASON_MAX;
  readonly fmt = (line: StockTransferLine, n: number) => formatBase(line, n);
  readonly liveErrors = computed(() => (this.touched() ? validateResolve(this.rows(), this.reason()) : NO_ERRORS));
  readonly shown = computed(() => {
    const server = this.errors();
    const live = this.liveErrors();
    return { rows: { ...server.rows, ...live.rows }, form: live.form ?? server.form };
  });

  ngOnInit(): void {
    this.rows.set(
      this.transfer()
        .lines.filter((l) => l.outstandingBaseQty > 0)
        .map((line) => ({ lineId: line.id, line, action: null })),
    );
  }

  setAction(i: number, value: string): void {
    const action: StockTransferResolution | null = value === 'WriteOff' || value === 'ReturnToSource' ? value : null;
    this.rows.update((rows) => rows.map((r, idx) => (idx === i ? { ...r, action } : r)));
    this.errors.set(NO_ERRORS);
  }

  setAll(value: StockTransferResolution): void {
    this.rows.update((rows) => rows.map((r) => ({ ...r, action: value })));
    this.errors.set(NO_ERRORS);
  }

  setReason(value: string): void {
    this.reason.set(value);
    this.errors.set(NO_ERRORS);
  }

  submit(): void {
    this.touched.set(true);
    if (hasErrors(validateResolve(this.rows(), this.reason()))) return;
    this.busy.set(true);
    this.errors.set(NO_ERRORS);
    const body = buildResolveRequest(this.rows(), this.reason());
    this.service.resolve(this.transfer().id, body, this.submission.keyFor(body)).subscribe({
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
