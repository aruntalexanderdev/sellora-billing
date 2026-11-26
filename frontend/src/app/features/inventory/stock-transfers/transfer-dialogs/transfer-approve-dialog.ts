import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { toProblem } from '../../../../core/http/api-errors';
import { UiButton } from '../../../../shared/ui/button/button';
import {
  APPROVE_REASON_MAX, IdempotentSubmission, buildApproveRequest, hasErrors, isReduced, mapRowProblem, validateApprove,
  type ApproveRow, type RowErrors,
} from '../stock-transfer-rules';
import { StockTransfersService } from '../stock-transfers.service';
import type { StockTransfer, StockTransferLine } from '../stock-transfers.models';

interface Row extends ApproveRow {
  line: StockTransferLine;
}

const NO_ERRORS: RowErrors = { rows: {}, form: null };

/** Approve a requested transfer, optionally at less than requested per line (down to 0, in the requested unit) with a reason. */
@Component({
  selector: 'app-transfer-approve-dialog',
  standalone: true,
  imports: [UiButton],
  templateUrl: './transfer-approve-dialog.html',
  styleUrl: './transfer-dialogs.scss',
})
export class TransferApproveDialog implements OnInit {
  private readonly service = inject(StockTransfersService);
  private readonly ownSubmission = new IdempotentSubmission();

  readonly transfer = input.required<StockTransfer>();
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
  readonly reasonMax = APPROVE_REASON_MAX;
  readonly reduced = computed(() => isReduced(this.rows()));
  readonly liveErrors = computed(() => (this.touched() ? validateApprove(this.rows(), this.reason()) : NO_ERRORS));
  readonly shown = computed(() => {
    const server = this.errors();
    const live = this.liveErrors();
    return { rows: { ...server.rows, ...live.rows }, form: live.form ?? server.form };
  });

  ngOnInit(): void {
    this.rows.set(this.transfer().lines.map((line) => ({ lineId: line.id, line, requestedQty: line.requestedQty, quantity: String(line.requestedQty) })));
  }

  setQuantity(i: number, value: string): void {
    this.rows.update((rows) => rows.map((r, idx) => (idx === i ? { ...r, quantity: value } : r)));
    this.errors.set(NO_ERRORS);
  }

  setReason(value: string): void {
    this.reason.set(value);
    this.errors.set(NO_ERRORS);
  }

  submit(): void {
    this.touched.set(true);
    if (hasErrors(validateApprove(this.rows(), this.reason()))) return;
    this.busy.set(true);
    this.errors.set(NO_ERRORS);
    const body = buildApproveRequest(this.rows(), this.reason());
    this.service.approve(this.transfer().id, this.submission.keyFor(body), body).subscribe({
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
