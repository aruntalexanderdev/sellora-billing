import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService } from '../../../../core/auth/auth.service';
import { toProblem } from '../../../../core/http/api-errors';
import { IdempotentSubmission } from '../../../../core/idempotency';
import { loadErrorText } from '../../../../shared/scope/problem-text';
import { UiBadge } from '../../../../shared/ui/badge/badge';
import { UiButton } from '../../../../shared/ui/button/button';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { endpointLabel } from '../../stock-transfers/stock-transfer-rules';
import {
  RECEIPT_SHORTFALL_NOTE, canReceive, consignmentStatusTone, inTransitBase, modeLabel, plannerProblemMessage, referenceLabel, remainingToAccountBase,
  shipmentStatusLabel, TRANSPORT_VIA_LABELS,
} from '../../shipments/shipment-rules';
import type { ConsignmentDetail, ConsignmentReceipt } from '../../shipments/shipments.models';
import { ConsignmentsService } from '../consignments.service';
import { ReceiptDialog } from '../receipt-dialog/receipt-dialog';

/** One consignment as its reader sees it. Incoming readers get no shipment link, other destinations or other vehicles. Receiving: verify (record), then post. */
@Component({
  selector: 'app-consignment-detail',
  standalone: true,
  imports: [RouterLink, DatePipe, UiBadge, UiButton, ReceiptDialog],
  templateUrl: './consignment-detail.html',
  styleUrl: './consignment-detail.scss',
})
export class ConsignmentDetailPage implements OnInit {
  private readonly service = inject(ConsignmentsService);
  private readonly route = inject(ActivatedRoute);
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);
  private readonly receiptSubmission = new IdempotentSubmission();
  private readonly postSubmissions = new Map<string, IdempotentSubmission>();

  readonly consignment = signal<ConsignmentDetail | null>(null);
  readonly receipts = signal<ConsignmentReceipt[]>([]);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly receiptOpen = signal(false);
  readonly busyReceiptId = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);

  /** Outgoing readers may open the planner; incoming never get a shipment id. */
  readonly shipmentLink = computed(() => {
    const c = this.consignment();
    return c && c.direction === 'outgoing' && c.shipmentId ? ['/inventory/shipments', c.shipmentId] : null;
  });
  /** Record receipt / Post: StockTransfers.Receive while the consignment is InTransit or PartiallyReceived. */
  readonly receivable = computed(() => {
    const c = this.consignment();
    return !!c && canReceive(c.status, (code) => this.auth.hasPermission(code));
  });
  readonly hasRemaining = computed(() => (this.consignment()?.lines ?? []).some((l) => remainingToAccountBase(l) > 0));
  readonly dispatched = computed(() => !!this.consignment()?.dispatchedAt);

  readonly place = endpointLabel;
  readonly mode = modeLabel;
  readonly refLabel = referenceLabel;
  readonly shipmentStatus = shipmentStatusLabel;
  readonly tone = consignmentStatusTone;
  readonly viaLabels = TRANSPORT_VIA_LABELS;
  readonly shortfallNote = RECEIPT_SHORTFALL_NOTE;
  readonly qty = (n: number) => formatQuantity(n, 0);
  /** Base quantities: the DTOs carry no base decimals, so use the screen convention (widens to the stored precision, never hides it). */
  readonly base = (n: number) => formatQuantity(n, 0);
  readonly remainingBase = remainingToAccountBase;
  readonly inTransit = inTransitBase;

  ngOnInit(): void {
    this.service.get(this.route.snapshot.paramMap.get('id')!).subscribe({
      next: (c) => {
        this.consignment.set(c);
        this.loading.set(false);
        if (c.receiptCount > 0) this.loadReceipts();
      },
      error: (err) => {
        this.loadError.set(loadErrorText(err, 'Could not load this consignment.'));
        this.loading.set(false);
      },
    });
  }

  private loadReceipts(): void {
    this.service.receipts(this.consignment()!.id).subscribe({ next: (r) => this.receipts.set(r) });
  }

  /** Quantities and statuses change after a receipt or a post: re-read both. */
  private refresh(): void {
    const id = this.consignment()!.id;
    this.service.get(id).subscribe({ next: (c) => this.consignment.set(c) });
    this.loadReceipts();
  }

  openReceipt(): void {
    this.actionError.set(null);
    this.receiptOpen.set(true);
  }

  closeReceipt(): void {
    this.receiptOpen.set(false);
    if (this.receiptSubmission.hasAmbiguousFailure) {
      this.receiptSubmission.acknowledged();
      this.refresh();
    }
  }

  onReceiptRecorded(): void {
    this.receiptOpen.set(false);
    this.toast.show('Receipt recorded. Post it to move the accepted quantity into stock.');
    this.refresh();
  }

  submissionForReceipt(): IdempotentSubmission {
    return this.receiptSubmission;
  }

  canPost(r: ConsignmentReceipt): boolean {
    return r.status === 'Verified' && this.auth.hasPermission('StockTransfers.Receive');
  }

  async post(r: ConsignmentReceipt): Promise<void> {
    if (!this.canPost(r) || this.busyReceiptId()) return;
    const ok = await this.confirm.ask({
      title: `Post receipt #${r.sequence}`,
      body: 'The accepted quantity moves from Transit into the destination stock. Damaged, rejected and missing quantities stay in transit. This cannot be undone here.',
      confirmLabel: 'Post receipt',
    });
    if (!ok) return;
    let sub = this.postSubmissions.get(r.id);
    if (!sub) this.postSubmissions.set(r.id, (sub = new IdempotentSubmission()));
    const submission = sub;
    this.busyReceiptId.set(r.id);
    this.actionError.set(null);
    this.service.postReceipt(this.consignment()!.id, r.id, submission.key).subscribe({
      next: () => {
        this.busyReceiptId.set(null);
        submission.succeeded();
        this.toast.show('Receipt posted.');
        this.refresh();
      },
      error: (err) => {
        this.busyReceiptId.set(null);
        submission.failed(err);
        this.actionError.set(plannerProblemMessage(toProblem(err)));
      },
    });
  }
}
