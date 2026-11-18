import { Component, computed, inject, input, output, signal } from '@angular/core';
import { toProblem } from '../../../../core/http/api-errors';
import { IdempotentSubmission } from '../../../../core/idempotency';
import { UiButton } from '../../../../shared/ui/button/button';
import { RECEIPT_REJECT_REASON_MAX, plannerProblemMessage, validateRejectReason } from '../../shipments/shipment-rules';
import type { ConsignmentReceipt } from '../../shipments/shipments.models';
import { ConsignmentsService } from '../consignments.service';

/** Rejects an unposted receipt for correction. A reason is required; the receipt's quantities become recordable again. */
@Component({
  selector: 'app-receipt-reject-dialog',
  standalone: true,
  imports: [UiButton],
  templateUrl: './receipt-reject-dialog.html',
  styleUrl: './receipt-reject-dialog.scss',
})
export class ReceiptRejectDialog {
  private readonly service = inject(ConsignmentsService);
  private readonly ownSubmission = new IdempotentSubmission();

  readonly receipt = input.required<ConsignmentReceipt>();
  /** Owned by the host so the key survives closing and reopening after an ambiguous failure. */
  readonly submissionInput = input<IdempotentSubmission | null>(null, { alias: 'submission' });
  readonly done = output<ConsignmentReceipt>();
  readonly cancelled = output<void>();

  readonly reasonMax = RECEIPT_REJECT_REASON_MAX;
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly touched = signal(false);
  readonly serverError = signal<string | null>(null);
  readonly error = computed(() => (this.touched() ? validateRejectReason(this.reason()) : null));

  private get submission(): IdempotentSubmission {
    return this.submissionInput() ?? this.ownSubmission;
  }

  submit(): void {
    this.touched.set(true);
    if (validateRejectReason(this.reason())) return;
    const r = this.receipt();
    const reason = this.reason().trim();
    this.busy.set(true);
    this.serverError.set(null);
    this.service.rejectReceipt(r.consignmentId, r.id, reason, this.submission.keyFor({ id: r.id, reason })).subscribe({
      next: (updated) => {
        this.busy.set(false);
        this.submission.succeeded();
        this.done.emit(updated);
      },
      error: (err) => {
        this.busy.set(false);
        this.submission.failed(err);
        this.serverError.set(plannerProblemMessage(toProblem(err)));
      },
    });
  }
}
